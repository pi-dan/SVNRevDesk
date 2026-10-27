using System.Text;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public async Task<string> DeleteSelectedAsync(IEnumerable<string> paths, string localRoot, CancellationToken token,
        IEnumerable<string>? serverPaths = null)
    {
        var targets = paths.ToArray();
        var remoteTargets = serverPaths?.ToArray();
        var completedBackups = new List<string>();
        try
        {
            return await DeleteSelectedCoreAsync(targets, localRoot, token, remoteTargets, completedBackups);
        }
        catch (Exception ex) when (NeedsDeletionCleanup(ex))
        {
            token.ThrowIfCancellationRequested();
            string cleanup;
            try
            {
                cleanup = await CleanupAsync(Path.GetFullPath(localRoot), token);
            }
            catch (Exception cleanupError) when (cleanupError is not OperationCanceledException)
            {
                throw new InvalidOperationException($"自动清理工作副本失败：{cleanupError.Message}\n原删除错误：{ex.Message}", cleanupError);
            }
            // Cleanup can finish pending work and change added/deleted states. Re-read and
            // back up the current targets instead of replaying an obsolete SVN command.
            try
            {
                return "已自动清理上次未完成的 SVN 操作，重试删除。\n" + cleanup
                    + string.Concat(completedBackups.Select(path => $"清理前内容备份：{path}\n"))
                    + await DeleteSelectedCoreAsync(targets, localRoot, token, remoteTargets, completedBackups);
            }
            catch (Exception retryError) when (retryError is not OperationCanceledException)
            {
                throw new InvalidOperationException($"工作副本已自动清理，但重试删除未完成：{retryError.Message}\n首次删除信息：{ex.Message}", retryError);
            }
        }
    }

    private static bool NeedsDeletionCleanup(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is SvnException svn && (svn.ErrorOutput.Contains("E155037", StringComparison.Ordinal)
                || svn.ErrorOutput.Contains("E155004", StringComparison.Ordinal))) return true;
        return false;
    }

    private async Task<string> DeleteSelectedCoreAsync(IEnumerable<string> paths, string localRoot, CancellationToken token,
        IEnumerable<string>? serverPaths, List<string> completedBackups)
    {
        localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        var targets = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0) throw new ArgumentException("请先选择要删除的文件或目录。");
        foreach (var path in targets) ValidateDeletionPath(path, localRoot);
        targets = targets.Where(path => !targets.Any(parent => parent != path
            && RepositoryService.IsDescendant(path, parent))).ToList();

        // Read current local SVN state. The browser's cached 'local added' label covers both kinds.
        var statuses = await ReadDeletionStatusesAsync(targets, token);
        var knownServerPaths = (serverPaths ?? []).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in targets.Where(path => knownServerPaths.Contains(path) && statuses[path].Item == "none"
            && !File.Exists(path) && !Directory.Exists(path)))
        {
            // Sparse nodes have no local metadata to schedule for deletion. Materialize only
            // the selected node and its parents, without fetching a directory's children.
            await runner.RunAsync(["update", "--parents", "--depth", "empty", "--ignore-externals", SvnRunner.Target(path)], token);
            var prepared = await ReadDeletionStatusesAsync([path], token);
            if (prepared[path].Item is "none" or "unversioned" or "ignored")
                throw new InvalidOperationException($"未能取得待删除项目的 SVN 信息，请刷新后重试：{path}");
            statuses[path] = prepared[path];
        }
        var localOnly = targets.Where(path => statuses[path].Item is "unversioned" or "ignored" or "none").ToList();
        var versioned = targets.Except(localOnly, StringComparer.OrdinalIgnoreCase).ToList();
        string? backup = null;
        if (targets.Any(path => File.Exists(path) || Directory.Exists(path)))
        {
            var backupRoot = Path.GetFullPath(backupDirectory ?? Path.Combine(Path.GetTempPath(), "SVNRevDesk", "Backups"));
            if (targets.Any(path => Inside(backupRoot, path)))
                throw new InvalidOperationException("备份目录位于待删除项目内，请将程序放在该目录之外后再删除。");
            backup = Path.Combine(backupRoot, "deleted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            foreach (var path in targets.Where(path => File.Exists(path) || Directory.Exists(path)))
                await BackupNewItemAsync(path, Path.Combine(backup, Path.GetRelativePath(localRoot, path)), localRoot, token);
            completedBackups.Add(backup);
        }

        var output = new StringBuilder();
        if (backup != null) output.AppendLine($"删除前内容备份：{backup}");
        try
        {
            // A long backup must not allow an external add/commit to change which deletion rule applies.
            var current = await ReadDeletionStatusesAsync(targets, token);
            if (targets.Any(path => current[path].Item != statuses[path].Item))
                throw new InvalidOperationException("项目的本地 SVN 状态已变化，请刷新后重新删除。");
            // Schedule SVN deletion separately from disk removal: obstructions may have a
            // different on-disk kind, and read-only children need their attributes cleared.
            if (versioned.Count > 0)
                output.Append(await RunWithTargetsAsync(["delete", "--force", "--keep-local"], versioned, token));

            foreach (var path in targets)
            {
                token.ThrowIfCancellationRequested();
                ValidateDeletionPath(path, localRoot);
                DeleteLocalContent(path, localRoot, token);
                output.AppendLine($"已删除本地项目：{path}");
            }
            return output.ToString();
        }
        catch (Exception ex) when (backup != null && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"删除未完成；内容备份保留在：{backup}\n{ex.Message}", ex);
        }
    }

    private async Task<Dictionary<string, WorkingStatus>> ReadDeletionStatusesAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        var statuses = new Dictionary<string, WorkingStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in paths.Chunk(32))
        {
            var xml = await runner.RunAsync(new[] { "status", "--xml", "--verbose", "--no-ignore", "--ignore-externals", "--depth", "empty" }
                .Concat(batch.Select(SvnRunner.Target)), token);
            foreach (var status in SvnXml.ReadStatus(xml)) statuses[status.Path] = status;
        }
        foreach (var path in paths)
        {
            // SVN may omit a target below an unversioned directory, or an already absent target.
            statuses.TryAdd(path, new WorkingStatus(path, "none", "none", "none", "none", null, null, false, false, null, null));
        }
        return statuses;
    }

    private static void DeleteLocalContent(string path, string localRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateDeletionPath(path, localRoot);
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteLocalContent(child, localRoot, token);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }
        else
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    private static void ValidateDeletionPath(string path, string localRoot)
    {
        if (!RepositoryService.IsDescendant(path, localRoot))
            throw new ArgumentException("删除项目必须位于当前工作副本内，不能删除打开的根目录。");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (Path.GetFileName(current).Equals(".svn", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不能删除 SVN 元数据。");
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"链接项目请单独处理：{current}");
            if (current.Equals(localRoot, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(Path.Combine(current, ".svn")))
                throw new InvalidOperationException($"嵌套工作副本请单独打开后处理：{current}");
        }
    }

    private static async Task BackupNewItemAsync(string source, string destination, string localRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateDeletionPath(source, localRoot);
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                await BackupNewItemAsync(child, Path.Combine(destination, Path.GetFileName(child)), localRoot, token);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await input.CopyToAsync(output, token);
        }
    }
}
