using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using SVNRevDesk.Core.Formatting;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Desktop.Services;

public sealed record FilePreviewContent(string Source, string Message, string? Text = null, BitmapSource? Image = null);

public static class FilePreviewService
{
    public const int MaximumTextBytes = 1024 * 1024;
    public const int MaximumImageBytes = 10 * 1024 * 1024;
    private const int MaximumTextCharacters = 100_000;
    private const int MaximumTextLines = 5000;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico" };

    public static Task<FilePreviewContent> LoadAsync(BrowserEntry entry, string revision,
        SvnRunner connection, UserSettings settings, CancellationToken token) =>
        Task.Run(() => LoadCoreAsync(entry, revision, connection, settings, token), token);

    private static async Task<FilePreviewContent> LoadCoreAsync(BrowserEntry entry, string revision,
        SvnRunner connection, UserSettings settings, CancellationToken token)
    {
        var image = ImageExtensions.Contains(Path.GetExtension(entry.Name));
        var kind = FileTypeIcons.GetKind(entry.Name, entry.IsDirectory);
        if (!image && kind is not (FileTypeKind.Text or FileTypeKind.Code or FileTypeKind.Configuration or FileTypeKind.Unknown)
            && Path.GetExtension(entry.Name).ToLowerInvariant() is not (".csv" or ".tsv" or ".svg"))
            return new("", "此类型暂不支持预览，请双击打开。");
        var limit = image ? MaximumImageBytes : MaximumTextBytes;
        if (entry.ExistsLocally)
            return await ReadAsync(entry.LocalPath!, image, limit, "本地文件", token);
        if (entry.State == FileState.ServerDeleted || !long.TryParse(revision, out var number) || number < 1)
            return new("", "没有可预览的本地文件或已检查的服务器版本。");
        if (entry.Size > limit) return TooLarge(image);

        var directory = Path.Combine(settings.TemporaryDirectory, "Preview");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
        // A dedicated runner keeps a slow preview out of the browser/operation queue.
        using var runner = new SvnRunner
        {
            Executable = connection.Executable, Username = connection.Username, Password = connection.Password,
            ConfigurationDirectory = connection.ConfigurationDirectory,
            ConfigurationTemplateDirectory = connection.ConfigurationTemplateDirectory
        };
        try
        {
            await runner.RunAsync(["cat", "-r", revision, SvnRunner.AtRevision(entry.Url, revision)], token,
                temporary, maximumOutputBytes: limit);
            return await ReadAsync(temporary, image, limit, $"服务器 r{revision}", token);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<FilePreviewContent> ReadAsync(string path, bool image, int limit, string source, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > limit) return TooLarge(image) with { Source = source };
        using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (count > limit - buffer.Length) return TooLarge(image) with { Source = source };
            buffer.Write(block, 0, count);
        }
        var bytes = buffer.ToArray();
        source += " · " + FileSizeFormatter.Format(bytes.Length);
        return await Task.Run(() => image ? ReadImage(bytes, source, token) : ReadText(bytes, source, token), token);
    }

    private static FilePreviewContent ReadImage(byte[] bytes, string source, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if ((long)width * height > 40_000_000) return new(source, "图片像素过多，请双击打开。");
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 1600);
        else bitmap.DecodePixelHeight = Math.Min(height, 1600);
        bitmap.EndInit();
        bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        return new(source, $"{width:N0} × {height:N0} 像素" + (decoder.Frames.Count > 1 ? " · 首帧预览" : ""), Image: bitmap);
    }

    private static FilePreviewContent ReadText(byte[] bytes, string source, CancellationToken token)
    {
        string text;
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { text = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            catch (DecoderFallbackException) { return new(source, "无法识别文本编码，请双击打开。"); }
        }
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            return new(source, "二进制内容暂不支持预览，请双击打开。");
        token.ThrowIfCancellationRequested();
        var length = Math.Min(text.Length, MaximumTextCharacters);
        var lines = 1;
        for (var index = 0; index < length; index++)
            if (text[index] == '\n' && ++lines > MaximumTextLines) { length = index; break; }
        var message = length < text.Length ? "仅显示前 100,000 字符或 5,000 行，查看全文请双击打开。"
            : text.Length == 0 ? "空文件" : "只读文本预览";
        return new(source, message, Text: text[..length]);
    }

    private static FilePreviewContent TooLarge(bool image) =>
        new("", image ? "图片超过 10 MiB，请双击打开。" : "文本超过 1 MiB，请双击打开。");
}
