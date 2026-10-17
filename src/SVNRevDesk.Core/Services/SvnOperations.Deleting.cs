using System.Text;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public async Task<string> DeleteSelectedAsync(IEnumerable<string> paths, string localRoot, CancellationToken token)
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
        var localOnly = targets.Where(path => statuses[path].Item is "unversioned" or "ignored" or "added").ToList();
        var added = localOnly.Where(path => statuses[path].Item == "added").ToList();
        var versioned = targets.Except(localOnly, StringComparer.OrdinalIgnoreCase).ToList();
        string? backup = null;
        if (localOnly.Count > 0)
        {
            var backupRoot = Path.GetFullPath(backupDirectory ?? Path.Combine(Path.GetTempPath(), "SVNRevDesk", "Backups"));
            if (targets.Any(path => Inside(backupRoot, path)))
                throw new InvalidOperationException("备份目录位于待删除项目内，请将程序放在该目录之外后再删除。");
            backup = Path.Combine(backupRoot, "deleted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            foreach (var path in localOnly)
                await BackupNewItemAsync(path, Path.Combine(backup, Path.GetRelativePath(localRoot, path)), localRoot, token);
        }

        var output = new StringBuilder();
        if (backup != null) output.AppendLine($"本地新增项目删除前备份：{backup}");
        try
        {
            // A long backup must not allow an external add/commit to change which deletion rule applies.
            var current = await ReadDeletionStatusesAsync(targets, token);
            if (targets.Any(path => current[path].Item != statuses[path].Item))
                throw new InvalidOperationException("项目的本地 SVN 状态已变化，请刷新后重新删除。");
            if (versioned.Count > 0)
                output.Append(await DeleteAsync(versioned, token));

            // Only newly scheduled additions may use --force, after their contents have been backed up.
            // SVN cancels their addition and removes the local item; existing server items never use it.
            if (added.Count > 0)
                output.Append(await RunWithTargetsAsync(["delete", "--force"], added, token));
            foreach (var path in localOnly.Except(added, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                ValidateDeletionPath(path, localRoot);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
                output.AppendLine($"已删除未版本控制项目：{path}");
            }
            return output.ToString();
        }
        catch (Exception ex) when (backup != null && ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"删除未完成；新增项目的内容备份保留在：{backup}\n{ex.Message}", ex);
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
            if (!statuses.TryGetValue(path, out var status) || status.Item is not
                ("unversioned" or "ignored" or "added" or "normal" or "modified" or "missing" or "deleted" or "replaced")
                || status.TreeConflict || status.Properties == "conflicted")
                throw new InvalidOperationException($"无法安全删除此项目，请先处理其工作副本状态：{path}");
        }
        return statuses;
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
