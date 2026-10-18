using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed class TextMergeSession
{
    internal TextMergeSession(string path, string localRoot, string directory, string signature, string[] sources, string[] hashes)
    { Path = path; LocalRoot = localRoot; Directory = directory; Signature = signature; Sources = sources; Hashes = hashes; }
    public string Path { get; }
    public string Directory { get; }
    public string LocalFile => System.IO.Path.Combine(Directory, "local" + System.IO.Path.GetExtension(Path));
    public string BaseFile => System.IO.Path.Combine(Directory, "base" + System.IO.Path.GetExtension(Path));
    public string IncomingFile => System.IO.Path.Combine(Directory, "incoming" + System.IO.Path.GetExtension(Path));
    public string ResultFile => System.IO.Path.Combine(Directory, "result" + System.IO.Path.GetExtension(Path));
    public string WorkingBackup => System.IO.Path.Combine(Directory, "working" + System.IO.Path.GetExtension(Path));
    internal string LocalRoot { get; }
    internal string Signature { get; }
    internal string[] Sources { get; }
    internal string[] Hashes { get; }
}

public sealed partial class SvnOperations
{
    private const int MaximumMergeBytes = 20 * 1024 * 1024;
    private sealed record TextConflictDetails(string Signature, string[] Files);

    public async Task<TextMergeSession> PrepareTextMergeAsync(string path, string localRoot, CancellationToken token)
    {
        path = Path.GetFullPath(path); localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        var details = await ReadTextConflictAsync(path, localRoot, token);
        var files = new[] { path }.Concat(details.Files).ToArray();
        var contents = files.Select(ReadMergeBytes).ToArray();
        // SVN conflict markers can mix native marker bytes with UTF-16 content in the
        // working file. Validate the three clean inputs, and preserve that file verbatim.
        foreach (var bytes in contents.Skip(1)) _ = DecodeMergeText(bytes);
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(backupDirectory ?? Path.Combine(AppContext.BaseDirectory, "Backups"));
        EnsureMergePath(root);
        var directory = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session = new TextMergeSession(path, localRoot, directory, details.Signature, files,
            contents.Select(bytes => Convert.ToHexString(SHA256.HashData(bytes))).ToArray());
        // These are independent copies: neither WinMerge nor cancellation changes the working copy.
        var destinations = new[] { session.WorkingBackup, session.BaseFile, session.LocalFile, session.IncomingFile };
        for (var i = 0; i < contents.Length; i++) await File.WriteAllBytesAsync(destinations[i], contents[i], token);
        await File.WriteAllTextAsync(Path.Combine(directory, "context.txt"), $"工作文件：{path}\n\n{details.Signature}", token);
        await VerifyTextMergeAsync(session, token);
        return session;
    }

    public async Task ApplyTextMergeAsync(TextMergeSession session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A saved draft and an explicit user confirmation are both required by the UI.
        EnsureMergePath(session.ResultFile);
        var bytes = ReadMergeBytes(session.ResultFile);
        var text = DecodeMergeText(bytes);
        if (text.ReplaceLineEndings("\n").Split('\n').Any(line => line.StartsWith("<<<<<<<", StringComparison.Ordinal)
            || line.StartsWith(">>>>>>>", StringComparison.Ordinal) || line.StartsWith("|||||||", StringComparison.Ordinal)))
            throw new InvalidOperationException("合并结果仍包含冲突标记，请在 WinMerge 中继续处理并保存。");
        await VerifyTextMergeAsync(session, token);
        var staging = Path.Combine(Path.GetDirectoryName(session.Path)!, ".svnrevdesk-merge-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(staging, bytes, token);
            await VerifyTextMergeAsync(session, token);
            token.ThrowIfCancellationRequested();
            // Rename a complete file on the same volume, rather than truncating the user's file.
            File.Replace(staging, session.Path, destinationBackupFileName: null);
            try
            {
                await runner.RunAsync(["resolve", "--depth", "empty", "--accept", "working", SvnRunner.Target(session.Path)], token);
                var remaining = SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--depth", "empty", SvnRunner.Target(session.Path)], token));
                if (remaining.Any(status => status.Item == "conflicted" || status.Properties == "conflicted" || status.TreeConflict))
                    throw new InvalidOperationException("SVN 仍报告冲突。");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"合并结果已写回，但未确认 SVN 冲突状态，请刷新后检查。原文件及合并结果保存在：{session.Directory}", ex);
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private async Task VerifyTextMergeAsync(TextMergeSession session, CancellationToken token)
    {
        var current = await ReadTextConflictAsync(session.Path, session.LocalRoot, token);
        if (current.Signature != session.Signature || !current.Files.SequenceEqual(session.Sources.Skip(1), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("冲突状态已变化，请关闭窗口后重新处理；已保存的合并草稿仍保留。");
        for (var i = 0; i < session.Sources.Length; i++)
            if (Convert.ToHexString(SHA256.HashData(ReadMergeBytes(session.Sources[i]))) != session.Hashes[i])
                throw new InvalidOperationException("工作文件或冲突版本已被其他程序修改，请重新处理；此次不会覆盖新修改。");
    }

    private async Task<TextConflictDetails> ReadTextConflictAsync(string path, string localRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!RepositoryService.IsDescendant(path, localRoot) || !File.Exists(path))
            throw new InvalidOperationException("文本合并需要当前工作副本内的现有文件。");
        EnsureMergePath(path);
        var status = SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--depth", "empty", SvnRunner.Target(path)], token)).SingleOrDefault();
        if (status is not { Item: "conflicted", TreeConflict: false } || status.Properties == "conflicted")
            throw new InvalidOperationException("此入口仅处理文本内容冲突；属性冲突、目录树冲突或已解决项目请单独处理。");
        var xml = XDocument.Parse(await runner.RunAsync(["info", "--xml", SvnRunner.Target(path)], token));
        var entry = xml.Root?.Element("entry");
        var conflicts = entry?.Elements("conflict").ToArray() ?? [];
        if (conflicts.Length != 1 || (string?)conflicts[0].Attribute("type") != "text" || entry?.Element("tree-conflict") != null)
            throw new InvalidOperationException("无法读取单一文本冲突的三个版本，请刷新后重试。");
        var wcRoot = entry?.Element("wc-info")?.Element("wcroot-abspath")?.Value;
        if (wcRoot == null || !RepositoryService.IsDescendant(path, Path.GetFullPath(wcRoot))) throw new InvalidOperationException("无法确认工作副本位置。");
        for (var parent = Path.GetDirectoryName(path); parent != null && !parent.Equals(localRoot, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(Path.Combine(parent, ".svn"))) throw new InvalidOperationException("嵌套工作副本请单独打开后处理。");
        var conflict = conflicts[0];
        var files = new[] { "prev-base-file", "prev-wc-file", "cur-base-file" }.Select(name =>
        {
            var value = conflict.Element(name)?.Value;
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("SVN 未保留完整的基础、本地和服务器版本，无法三方合并。");
            var file = Path.GetFullPath(value, Path.GetDirectoryName(path)!);
            if (!string.Equals(Path.GetDirectoryName(file), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)
                || file.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("冲突版本路径不在文件所在目录。");
            EnsureMergePath(file); return file;
        }).ToArray();
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) throw new InvalidOperationException("冲突版本路径重复。");
        var properties = XDocument.Parse(await runner.RunAsync(["proplist", "--xml", "--verbose", SvnRunner.Target(path)], token));
        var mime = properties.Descendants("property").FirstOrDefault(property => (string?)property.Attribute("name") == "svn:mime-type")?.Value;
        if (!string.IsNullOrWhiteSpace(mime) && !mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此文件的 SVN MIME 类型不是文本，不支持文本合并。");
        // SVN may emit attributes in a different order between processes.
        var signature = string.Join("\n", conflict.DescendantsAndSelf().Select(element => element.Name + "|"
            + string.Join(";", element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal))
            + "|" + (element.HasElements ? "" : element.Value)).Order(StringComparer.Ordinal));
        signature += "\n" + entry?.Element("repository")?.Element("uuid")?.Value + "\n" + entry?.Element("url")?.Value;
        return new(signature, files);
    }

    private static byte[] ReadMergeBytes(string path)
    {
        EnsureMergePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumMergeBytes) throw new InvalidOperationException("文本合并暂支持每个版本不超过 20 MiB 的文件。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private static string DecodeMergeText(byte[] bytes)
    {
        string text;
        var hasUnicodeBom = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })
            || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        try
        {
            Encoding encoding; var offset = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); offset = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true, true); offset = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
            else { encoding = new UTF8Encoding(false, true); if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3; }
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex) when (!hasUnicodeBom)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { text = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("无法识别文本编码，未打开或写回文件。", ex); }
        }
        catch (DecoderFallbackException ex) { throw new InvalidOperationException("文本编码与 BOM 不一致，未打开或写回文件。", ex); }
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("检测到二进制内容，不支持文本合并。");
        return text;
    }

    private static void EnsureMergePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if (Path.GetFileName(current).Equals(".svn", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能合并 SVN 元数据。");
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("链接项目请在实际工作副本内单独处理。");
        }
    }
}
