using System.Text;

namespace SVNRevDesk.Core.Services;

public enum NewItemKind { Folder, Text, EmptyFile, WordDocument, Spreadsheet, Presentation }
public sealed record NewItemRequest(string Name, NewItemKind Kind, string InitialText = "");

// Creation is a local filesystem operation. SVN scheduling is a separate, optional step.
public static class LocalItemCreator
{
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')
            || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
            throw new ArgumentException("请输入有效的名称，不能包含路径、特殊字符，或以空格 / 点结尾。");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && "123456789¹²³".Contains(stem[3]))
            throw new ArgumentException("此名称是 Windows 保留名称，请换一个名称。");
        if (name.Equals(".svn", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能创建 SVN 元数据目录 .svn。");
        if (name.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("此名称用于临时文件，请换一个名称。");
    }

    public static void ValidateRequest(NewItemRequest request)
    {
        ValidateName(request.Name);
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentOutOfRangeException(nameof(request));
        var extension = OfficeDocumentCreator.GetExtension(request.Kind);
        if (extension != null && !Path.GetExtension(request.Name).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"所选文档类型的文件名必须以 {extension} 结尾。");
    }

    public static async Task<string> CreateAsync(string localRoot, string directory, NewItemRequest request, CancellationToken token)
    {
        ValidateRequest(request);
        var kind = request.Kind == NewItemKind.EmptyFile
            ? OfficeDocumentCreator.GetKindFromName(request.Name) ?? NewItemKind.EmptyFile : request.Kind;
        localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!directory.Equals(localRoot, StringComparison.OrdinalIgnoreCase) && !RepositoryService.IsDescendant(directory, localRoot))
            throw new ArgumentException("新建位置必须位于当前工作副本内。");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("本地目录不存在，请先下载该目录。");
        // Do not follow junctions/symlinks to a location outside the opened working copy.
        for (string? current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            if (current != localRoot && Path.GetFileName(current).Equals(".svn", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不能在 SVN 元数据目录中创建项目。");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("不能在链接目录中创建项目，请直接打开实际工作副本目录。");
            if (current.Equals(localRoot, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(Path.Combine(current, ".svn")))
                throw new ArgumentException("此目录属于嵌套工作副本，请单独打开后创建项目。");
        }
        var destination = Path.Combine(directory, request.Name);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("已存在同名文件或文件夹，请换一个名称。");
        var temporary = Path.Combine(directory, ".svnclient-new-" + Guid.NewGuid().ToString("N"));
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.Kind == NewItemKind.Folder)
            {
                Directory.CreateDirectory(temporary);
                token.ThrowIfCancellationRequested();
                Directory.Move(temporary, destination);
            }
            else
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    if (request.Kind == NewItemKind.Text)
                        await output.WriteAsync(Encoding.UTF8.GetBytes(request.InitialText), token);
                    else if (kind is NewItemKind.WordDocument or NewItemKind.Spreadsheet or NewItemKind.Presentation)
                        await OfficeDocumentCreator.WriteAsync(output, kind, token);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
            }
            return destination;
        }
        finally
        {
            // Only the uniquely named staging item belongs to us; existing destination items stay intact.
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: false);
        }
    }
}
