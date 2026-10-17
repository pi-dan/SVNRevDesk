using System.Diagnostics;

namespace SVNRevDesk.Core.Services;

public sealed record FileTransferItem(string Source, string Destination, bool IsDirectory,
    long Size, DateTime ModifiedAt, long? DestinationSize, DateTime? DestinationModifiedAt);

public sealed record FileTransferPlan(string LocalRoot, string DestinationDirectory,
    IReadOnlyList<FileTransferItem> Items, IReadOnlyList<string> TopLevelPaths)
{
    public IReadOnlyList<FileTransferItem> Conflicts => Items.Where(item => !item.IsDirectory && item.DestinationSize != null).ToArray();
}

public sealed record FileTransferProgress(string Path, int CompletedFiles, int FileCount, long Bytes, long TotalBytes);

// Prepare all paths before writing. Files are published atomically, with backups for confirmed replacements.
public static class LocalFileTransfer
{
    public static FileTransferPlan Prepare(IEnumerable<string> sources, string localRoot, string directory,
        CancellationToken token = default, IReadOnlyDictionary<string, string>? originalPaths = null)
    {
        localRoot = Normalize(localRoot);
        directory = Normalize(directory);
        ValidateDirectory(localRoot, directory);
        var roots = sources.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        roots = roots.Where(path => !roots.Any(parent => !Same(path, parent) && RepositoryService.IsDescendant(path, parent))).ToArray();
        if (roots.Length == 0) throw new ArgumentException("剪贴板中没有文件或文件夹。");
        var items = new List<FileTransferItem>();
        var topLevel = new List<string>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in roots)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(source);
            LocalItemCreator.ValidateName(name);
            var destination = Path.Combine(directory, name);
            var original = originalPaths?.GetValueOrDefault(source) is { Length: > 0 } originalPath ? Normalize(originalPath) : source;
            if (Same(original, destination) || Same(source, destination) || topLevel.Contains(destination, StringComparer.OrdinalIgnoreCase))
                destination = GetCopyName(directory, name, topLevel, Directory.Exists(source));
            if (Directory.Exists(source) && (RepositoryService.IsDescendant(destination, source) || RepositoryService.IsDescendant(destination, original)))
                throw new IOException($"不能将文件夹复制到它自己的子目录：\n{source}");
            AddItems(source, destination);
            topLevel.Add(destination);
        }
        return new(localRoot, directory, items, topLevel);

        void AddItems(string source, string destination)
        {
            token.ThrowIfCancellationRequested();
            ValidatePath(source);
            ValidatePath(destination);
            if (!destinations.Add(destination)) throw new IOException($"复制目标重复：{destination}");
            if (!RepositoryService.IsDescendant(destination, directory)) throw new IOException("复制目标超出了当前目录。");
            var attributes = File.GetAttributes(source);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory ? File.Exists(destination) : Directory.Exists(destination))
                throw new IOException($"同名项目的类型不同，请先改名或移走：\n{destination}");
            if (isDirectory)
            {
                // Merging content into another working copy would bypass its SVN mapping.
                if (Directory.Exists(Path.Combine(destination, ".svn")))
                    throw new IOException($"目标属于嵌套工作副本，请单独打开：\n{destination}");
                items.Add(new(source, destination, true, 0, Directory.GetLastWriteTimeUtc(source), null, null));
                foreach (var child in Directory.EnumerateFileSystemEntries(source))
                {
                    var childName = Path.GetFileName(child);
                    if (childName.Equals(".svn", StringComparison.OrdinalIgnoreCase)
                        || childName.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase)) continue;
                    LocalItemCreator.ValidateName(childName);
                    AddItems(child, Path.Combine(destination, childName));
                }
            }
            else
            {
                var sourceInfo = new FileInfo(source);
                var destinationInfo = new FileInfo(destination);
                items.Add(new(source, destination, false, sourceInfo.Length, sourceInfo.LastWriteTimeUtc,
                    destinationInfo.Exists ? destinationInfo.Length : null,
                    destinationInfo.Exists ? destinationInfo.LastWriteTimeUtc : null));
            }
        }
    }

    public static async Task CopyAsync(FileTransferPlan plan, bool replaceExisting, string backupDirectory,
        CancellationToken token, IProgress<FileTransferProgress>? progress = null, Action<string>? backupCreated = null,
        bool backupBeforeOverwrite = true)
    {
        ValidateDirectory(plan.LocalRoot, plan.DestinationDirectory);
        var files = plan.Items.Where(item => !item.IsDirectory && (replaceExisting || item.DestinationSize == null)).ToArray();
        var totalBytes = files.Sum(item => item.Size);
        var count = 0;
        long bytes = 0;
        string? backup = null;
        var reportTimer = Stopwatch.StartNew();
        foreach (var item in plan.Items)
        {
            token.ThrowIfCancellationRequested();
            ValidateDirectory(plan.LocalRoot, Path.GetDirectoryName(item.Destination)!);
            ValidatePath(item.Destination);
            ValidatePath(item.Source);
            if (item.IsDirectory)
            {
                if (File.Exists(item.Destination) || Directory.Exists(Path.Combine(item.Destination, ".svn")))
                    throw new IOException($"目标目录已变化：{item.Destination}");
                Directory.CreateDirectory(item.Destination);
                continue;
            }
            if (!replaceExisting && item.DestinationSize != null) continue;
            CheckDestination(item);
            Report(item.Destination);
            var temporary = Path.Combine(Path.GetDirectoryName(item.Destination)!, ".svnclient-new-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    var buffer = new byte[128 * 1024];
                    int length;
                    while ((length = await input.ReadAsync(buffer, token)) != 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, length), token);
                        bytes += length;
                        if (reportTimer.ElapsedMilliseconds >= 100) { Report(item.Destination); reportTimer.Restart(); }
                    }
                }
                var sourceInfo = new FileInfo(item.Source);
                if (sourceInfo.Length != item.Size || sourceInfo.LastWriteTimeUtc != item.ModifiedAt)
                    throw new IOException($"复制期间源文件已变化，请重试：\n{item.Source}");
                token.ThrowIfCancellationRequested();
                ValidateDirectory(plan.LocalRoot, Path.GetDirectoryName(item.Destination)!);
                ValidatePath(item.Destination);
                CheckDestination(item);
                if (item.DestinationSize != null && backupBeforeOverwrite)
                {
                    backup ??= Path.Combine(Normalize(backupDirectory), "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                    var backupFile = Path.Combine(backup, Path.GetRelativePath(plan.LocalRoot, item.Destination));
                    ValidatePath(backupFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    await using (var original = new FileStream(item.Destination, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                    await using (var saved = new FileStream(backupFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                        await original.CopyToAsync(saved, token);
                    backupCreated?.Invoke(backupFile);
                    token.ThrowIfCancellationRequested();
                    CheckDestination(item);
                }
                File.SetLastWriteTimeUtc(temporary, item.ModifiedAt);
                File.Move(temporary, item.Destination, overwrite: item.DestinationSize != null);
                count++;
                Report(item.Destination);
            }
            finally
            {
                ValidatePath(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        void Report(string path) => progress?.Report(new(path, count, files.Length, bytes, totalBytes));
    }

    public static void ValidateDirectory(string localRoot, string directory)
    {
        localRoot = Normalize(localRoot);
        directory = Normalize(directory);
        if (!Same(directory, localRoot) && !RepositoryService.IsDescendant(directory, localRoot))
            throw new IOException("粘贴位置必须位于当前本地工作副本内。");
        ValidatePath(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("本地目录不存在，请先下载该目录。");
        for (var current = directory; current != null; current = Path.GetDirectoryName(current))
        {
            if (Path.GetFileName(current).Equals(".svn", StringComparison.OrdinalIgnoreCase)) throw new IOException("不能向 SVN 元数据目录粘贴。");
            if (Same(current, localRoot)) break;
            if (Directory.Exists(Path.Combine(current, ".svn"))) throw new IOException("目标属于嵌套工作副本，请单独打开后粘贴。");
        }
    }

    public static void ValidatePath(string path)
    {
        for (var current = Normalize(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"不能复制或写入链接目录，请使用实际文件路径：\n{current}");
        }
    }

    private static void CheckDestination(FileTransferItem item)
    {
        var current = new FileInfo(item.Destination);
        if (Directory.Exists(item.Destination) || current.Exists != item.DestinationSize.HasValue
            || current.Exists && (current.Length != item.DestinationSize || current.LastWriteTimeUtc != item.DestinationModifiedAt))
            throw new IOException($"粘贴期间目标已变化，已保留该项目，请重试：\n{item.Destination}");
    }

    private static string GetCopyName(string directory, string name, IReadOnlyList<string> reserved, bool isDirectory)
    {
        var extension = isDirectory ? "" : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        for (var index = 1; ; index++)
        {
            var candidateName = stem + (index == 1 ? " - 副本" : $" - 副本 ({index})") + extension;
            LocalItemCreator.ValidateName(candidateName);
            var candidate = Path.Combine(directory, candidateName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !reserved.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return candidate;
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
