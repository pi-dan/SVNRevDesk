using SVNRevDesk.Core.Svn;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed record FileMoveItem(string Source, string Destination, bool IsDirectory, bool IsVersioned,
    bool DestinationExists, bool IsSameLocation)
{
    public long? DestinationSize { get; init; }
    public DateTime? DestinationModifiedAt { get; init; }
    public WorkingStatus? DestinationStatus { get; init; }
    public bool IsReverseMove { get; init; }
    public string OverwriteUnavailableReason { get; init; } = "";
    public bool CanOverwrite => DestinationExists && !IsSameLocation && OverwriteUnavailableReason.Length == 0;
}
public enum FileMoveConflictBehavior { Stop, Skip, OverwriteFiles }
public sealed record FileMoveOptions(FileMoveConflictBehavior Conflicts = FileMoveConflictBehavior.Stop, bool BackupBeforeOverwrite = true);
public sealed record FileMovePlan(string LocalRoot, string DestinationDirectory, IReadOnlyList<FileMoveItem> Items)
{
    public IReadOnlyList<FileMoveItem> Conflicts => Items.Where(item => item.DestinationExists && !item.IsSameLocation).ToArray();
}
public sealed record FileMoveProgress(string Path, int CompletedItems, int ItemCount);

public sealed partial class SvnOperations
{
    public Task<FileMovePlan> PrepareFileMoveAsync(IEnumerable<string> sources, string directory, string localRoot, CancellationToken token) =>
        PrepareFileMovesAsync(sources.Select(source => (source, Path.Combine(directory, Path.GetFileName(source)))), localRoot, token);

    public Task<FileMovePlan> PrepareFileRenameAsync(string source, string destination, string localRoot, CancellationToken token) =>
        PrepareFileMovesAsync([(source, destination)], localRoot, token);

    private async Task<FileMovePlan> PrepareFileMovesAsync(IEnumerable<(string Source, string Destination)> requests,
        string localRoot, CancellationToken token)
    {
        localRoot = NormalizeMovePath(localRoot);
        var paths = requests.Select(pair => (Source: NormalizeMovePath(pair.Source), Destination: NormalizeMovePath(pair.Destination)))
            .DistinctBy(pair => pair.Source, StringComparer.OrdinalIgnoreCase).ToArray();
        paths = paths.Where(pair => !paths.Any(parent => !SameMovePath(parent.Source, pair.Source)
            && RepositoryService.IsDescendant(pair.Source, parent.Source))).ToArray();
        if (paths.Length == 0) throw new ArgumentException("请先选择要移动的文件或目录。");
        var directory = Path.GetDirectoryName(paths[0].Destination)!;
        if (paths.Any(pair => !SameMovePath(Path.GetDirectoryName(pair.Destination)!, directory)))
            throw new ArgumentException("一次移动只能选择一个目标目录。");
        LocalFileTransfer.ValidateDirectory(localRoot, directory);
        var parentStatus = (await ReadMoveStatusesAsync(directory, "empty", token)).FirstOrDefault(s => SameMovePath(s.Path, directory));
        if (parentStatus?.Item is "deleted" or "missing" or "conflicted" or "obstructed" or "external"
            || parentStatus?.TreeConflict == true || parentStatus?.Properties == "conflicted")
            throw new IOException("目标目录存在删除、缺失或冲突，请先处理后再移动。\n" + directory);
        if (paths.Select(pair => pair.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new IOException("多个源项目重名，请分开移动或先重命名。");

        var items = new List<FileMoveItem>();
        foreach (var (source, destination) in paths)
        {
            token.ThrowIfCancellationRequested();
            LocalItemCreator.ValidateName(Path.GetFileName(destination));
            if (SameMovePath(source, localRoot) || RepositoryService.IsDescendant(localRoot, source))
                throw new IOException("不能移动打开的根目录或它的上级目录。");
            ValidateMoveTree(source, token);
            var isDirectory = Directory.Exists(source);
            if (isDirectory && RepositoryService.IsDescendant(destination, source))
                throw new IOException("不能将目录移动到它自己的子目录。\n" + source);
            var inside = RepositoryService.IsDescendant(source, localRoot);
            // External working copies must retain their own SVN metadata and scheduling.
            if (!inside && FindMoveWorkingCopy(source) != null)
                throw new IOException("其他工作副本中的项目不能直接剪切到这里，请使用复制，或单独打开其工作副本。\n" + source);
            if (inside) LocalFileTransfer.ValidateDirectory(localRoot, Path.GetDirectoryName(source)!);
            var statuses = inside ? await ReadMoveStatusesAsync(source, isDirectory ? "infinity" : "empty", token) : [];
            var status = statuses.FirstOrDefault(status => SameMovePath(status.Path, source));
            if (statuses.Any(status => status.TreeConflict || status.Item is "conflicted" or "obstructed" or "external"
                || status.Properties == "conflicted") || status?.Item is "deleted" or "missing")
                throw new IOException("请先处理冲突、缺失或已安排删除的状态后再移动。\n" + source);
            var versioned = status != null && status.Item is not ("unversioned" or "ignored" or "none");
            var sameLocation = SameMovePath(source, destination);
            if (versioned && !sameLocation)
            {
                if (parentStatus == null || parentStatus.Item is not ("normal" or "modified" or "added" or "replaced"))
                    throw new IOException("移动已受版本控制的项目时，目标目录需要先加入版本控制并处理冲突。\n" + directory);
                if (Path.GetFileName(source) != Path.GetFileName(destination)) ValidateLiteralDestination(Path.GetFileName(destination));
            }
            LocalFileTransfer.ValidatePath(destination);
            var destinationStatus = (await ReadMoveStatusesAsync(destination, "empty", token))
                .FirstOrDefault(s => SameMovePath(s.Path, destination));
            var exists = File.Exists(destination) || Directory.Exists(destination)
                || destinationStatus != null && destinationStatus.Item is not ("unversioned" or "ignored" or "none");
            var destinationFile = new FileInfo(destination);
            var destinationIsFile = destinationFile.Exists;
            if (!destinationIsFile && !Directory.Exists(destination) && destinationStatus?.Item is "deleted" or "missing")
            {
                // Local info retains the node kind even when its file has been removed. No server query is needed.
                var info = SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", SvnRunner.Target(destination)], token)).Single();
                destinationIsFile = !info.IsDirectory;
            }
            var reverseMove = !destinationFile.Exists && status?.Item == "added" && status.MovedFrom != null
                && destinationStatus?.Item == "deleted" && destinationStatus.MovedTo != null
                && SameMovePath(status.MovedFrom, destination) && SameMovePath(destinationStatus.MovedTo, source);
            items.Add(new(source, destination, isDirectory, versioned, exists, sameLocation)
            {
                DestinationSize = destinationFile.Exists ? destinationFile.Length : null,
                DestinationModifiedAt = destinationFile.Exists ? destinationFile.LastWriteTimeUtc : null,
                DestinationStatus = destinationStatus,
                IsReverseMove = reverseMove,
                OverwriteUnavailableReason = MoveOverwriteUnavailableReason(isDirectory, versioned, destinationIsFile,
                    destinationFile.Exists, destinationStatus, reverseMove)
            });
        }
        return new(localRoot, directory, items);
    }

    private static string MoveOverwriteUnavailableReason(bool sourceIsDirectory, bool sourceIsVersioned,
        bool destinationIsFile, bool destinationHasContent, WorkingStatus? destinationStatus, bool reverseMove)
    {
        if (sourceIsDirectory || !destinationIsFile) return "同名目录或类型不同，保留源项目并跳过。";
        if (destinationStatus?.TreeConflict == true || destinationStatus?.Properties == "conflicted"
            || destinationStatus?.Item is "conflicted" or "obstructed" or "external" or "incomplete")
            return "目标存在冲突或不完整状态，请先处理。";
        if (destinationStatus?.Item == "deleted" && destinationHasContent)
            return "目标已安排删除但仍有本地内容，请先处理其删除状态。";
        if (!reverseMove && (destinationStatus?.MovedFrom != null || destinationStatus?.MovedTo != null))
            return "目标涉及尚未提交的移动，请先提交或撤销。";
        if (!destinationHasContent && destinationStatus?.Item == "missing" && destinationStatus.BaseRevision == null)
            return "目标尚未提交且文件缺失，请先恢复文件或撤销新增状态。";
        if (!destinationHasContent && destinationStatus?.Copied == true && !reverseMove)
            return "缺失的目标涉及尚未提交的复制或替换，请先处理。";
        if (sourceIsVersioned && (destinationStatus?.Item == "replaced" || destinationStatus?.Copied == true))
            return "目标涉及尚未提交的复制或替换，请先提交或撤销。";
        return "";
    }

    public Task<int> MoveFilesAsync(FileMovePlan plan, bool skipExisting, CancellationToken token,
        IProgress<FileMoveProgress>? progress = null) => MoveFilesAsync(plan,
            new FileMoveOptions(skipExisting ? FileMoveConflictBehavior.Skip : FileMoveConflictBehavior.Stop), token, progress);

    public async Task<int> MoveFilesAsync(FileMovePlan plan, FileMoveOptions options, CancellationToken token,
        IProgress<FileMoveProgress>? progress = null, Action<string>? backupCreated = null)
    {
        // Recheck after any destination selection or conflict dialog, before changing either side.
        var current = await PrepareFileMovesAsync(plan.Items.Select(item => (item.Source, item.Destination)), plan.LocalRoot, token);
        if (options.Conflicts == FileMoveConflictBehavior.Stop && current.Conflicts.Count > 0)
            throw new IOException("目标已有同名项目，已保留源文件，请重新选择移动位置。");
        if (options.Conflicts == FileMoveConflictBehavior.OverwriteFiles)
            foreach (var item in current.Conflicts.Where(item => item.CanOverwrite))
                CheckMoveDestination(plan.Items.Single(original => SameMovePath(original.Source, item.Source)), item);
        var completed = 0;
        foreach (var item in current.Items)
        {
            token.ThrowIfCancellationRequested();
            if (item.IsSameLocation || item.DestinationExists && (options.Conflicts != FileMoveConflictBehavior.OverwriteFiles || !item.CanOverwrite)) continue;
            LocalFileTransfer.ValidateDirectory(current.LocalRoot, Path.GetDirectoryName(item.Destination)!);
            if (item.DestinationExists)
            {
                progress?.Report(new(item.Source, completed, current.Items.Count));
                await OverwriteMoveFileAsync(item, current.LocalRoot, options.BackupBeforeOverwrite, token, backupCreated);
                completed++;
                progress?.Report(new(item.Destination, completed, current.Items.Count));
                continue;
            }
            if (File.Exists(item.Destination) || Directory.Exists(item.Destination)) throw new IOException("移动目标已变化：" + item.Destination);
            progress?.Report(new(item.Source, completed, current.Items.Count));
            if (item.IsVersioned)
            {
                await RunLocalMoveAsync(item, token);
            }
            else await Task.Run(() => MoveUnversionedItem(item.Source, item.Destination, token,
                path => progress?.Report(new(path, completed, current.Items.Count))), token);
            completed++;
            progress?.Report(new(item.Destination, completed, current.Items.Count));
        }
        return completed;
    }

    private Task<string> RunLocalMoveAsync(FileMoveItem item, CancellationToken token)
    {
        // Using '.' preserves names containing a space before @, including in ancestor paths.
        var target = Path.GetFileName(item.Source) == Path.GetFileName(item.Destination) ? "." : ".\\" + Path.GetFileName(item.Destination);
        return runner.RunAsync(["move", SvnRunner.Target(item.Source), target], token,
            workingDirectory: Path.GetDirectoryName(item.Destination));
    }

    private static void MoveUnversionedItem(string source, string destination, CancellationToken token, Action<string> report)
    {
        token.ThrowIfCancellationRequested();
        LocalFileTransfer.ValidatePath(source);
        LocalFileTransfer.ValidatePath(destination);
        report(source);
        if (!Directory.Exists(source)) { File.Move(source, destination, overwrite: false); return; }
        if (SameMovePath(Path.GetPathRoot(source)!, Path.GetPathRoot(destination)!))
        {
            Directory.Move(source, destination);
            return;
        }
        // Across volumes, move one file at a time without overwrites. Cancellation retains
        // completed destinations and the remaining sources; no recursive source deletion is used.
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("目标已存在：" + destination);
        Directory.CreateDirectory(destination);
        foreach (var child in Directory.EnumerateFileSystemEntries(source))
        {
            LocalItemCreator.ValidateName(Path.GetFileName(child));
            MoveUnversionedItem(child, Path.Combine(destination, Path.GetFileName(child)), token, report);
        }
        Directory.Delete(source, recursive: false);
    }

    private static void ValidateMoveTree(string source, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            LocalFileTransfer.ValidatePath(path);
            LocalItemCreator.ValidateName(Path.GetFileName(path));
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("源项目不存在，请刷新后重新选择。", path);
            if (Directory.Exists(path))
            {
                if (Directory.Exists(Path.Combine(path, ".svn"))) throw new IOException("不能移动工作副本根目录或包含嵌套工作副本的目录。\n" + path);
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
            for (var parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
                if (Path.GetFileName(parent).Equals(".svn", StringComparison.OrdinalIgnoreCase)) throw new IOException("不能移动 SVN 元数据。");
        }
    }

    private static string? FindMoveWorkingCopy(string path)
    {
        for (var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path); current != null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(Path.Combine(current, ".svn"))) return current;
        return null;
    }
    private async Task<IReadOnlyList<Models.WorkingStatus>> ReadMoveStatusesAsync(string path, string depth, CancellationToken token) =>
        SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--verbose", "--no-ignore", "--ignore-externals",
            "--depth", depth, SvnRunner.Target(path)], token));

    private static string NormalizeMovePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SameMovePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
