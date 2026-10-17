using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public async Task<IReadOnlyList<WorkingStatus>> ReadLocalDirectoryStatusAsync(string directory, string localRoot, CancellationToken token,
        bool includeDescendants = false)
    {
        var statuses = SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--verbose", "--no-ignore",
            "--ignore-externals", "--depth", includeDescendants ? "infinity" : "immediates", SvnRunner.Target(directory)], token)).ToList();
        var ancestors = new List<string>();
        for (var parent = Path.GetDirectoryName(directory); parent != null && Inside(parent, localRoot); parent = Path.GetDirectoryName(parent))
            ancestors.Add(parent);
        if (ancestors.Count > 0) statuses.AddRange(await ReadLocalStatusesAsync(ancestors, "empty", token));
        localIgnores?.Reload();
        return localIgnores == null ? statuses : statuses.Select(localIgnores.Apply).ToList();
    }

    public async Task<IReadOnlyList<CommitChange>> ReadCommitChangesAsync(IEnumerable<string> paths,
        string localRoot, CancellationToken token)
    {
        localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        localIgnores?.Reload();
        var scopes = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (scopes.Count == 0) throw new ArgumentException("请先选择要提交的文件或目录。");
        foreach (var path in scopes)
            if (!Inside(path, localRoot)) throw new ArgumentException("提交路径必须位于当前工作副本内。");
        scopes = scopes.Where(path => !scopes.Any(parent => parent != path && RepositoryService.IsDescendant(path, parent))).ToList();

        var statuses = (await ReadLocalStatusesAsync(scopes, "infinity", token)).ToDictionary(status => status.Path, StringComparer.OrdinalIgnoreCase);
        scopes = scopes.Where(path => localIgnores?.Matches(path) != true
            || !statuses.TryGetValue(path, out var status) || status.Item is not ("unversioned" or "ignored")).ToList();
        if (scopes.Count == 0) return [];
        // SVN moves are one change across two locations. Include only their explicit counterparts,
        // even when the commit was opened from just the source or destination directory.
        var visitedMovePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var counterparts = statuses.Values.SelectMany(status => new[] { status.MovedFrom, status.MovedTo })
                .Where(path => path != null && Inside(path, localRoot) && !statuses.ContainsKey(path) && visitedMovePaths.Add(path))
                .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (counterparts.Length == 0) break;
            var related = await ReadLocalStatusesAsync(counterparts, "infinity", token);
            foreach (var status in related) statuses[status.Path] = status;
            scopes.AddRange(counterparts.Where(path => !scopes.Any(scope => Inside(path, scope))));
            if (related.Count == 0) break;
        }

        // A selected added file may require new parents outside its selected scope. Only inspect those
        // ancestor nodes, without scanning their other children or reading any server information.
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scopes)
        {
            var parent = Path.GetDirectoryName(path);
            while (parent != null && Inside(parent, localRoot))
            {
                if (!scopes.Any(scope => Inside(parent, scope))) ancestors.Add(parent);
                if (parent.Equals(localRoot, StringComparison.OrdinalIgnoreCase)) break;
                parent = Path.GetDirectoryName(parent);
            }
        }
        if (ancestors.Count > 0)
        {
            var parentStatuses = await ReadLocalStatusesAsync(ancestors, "empty", token);
            foreach (var status in parentStatuses.Where(status => status.Item is "added" or "replaced" or "unversioned")) statuses[status.Path] = status;
        }

        // An explicit child scope may have no SVN status until its unversioned parent is
        // discovered. Remove ignored scopes now, before required parents become candidates.
        var ignoredScopes = scopes.Where(path => localIgnores?.Matches(path) == true
            && (statuses.TryGetValue(path, out var status) ? status.Item is "unversioned" or "ignored"
                : statuses.Values.Any(parent => parent.Item is "unversioned" or "ignored" && RepositoryService.IsDescendant(path, parent.Path)))).ToArray();
        if (ignoredScopes.Length > 0)
        {
            scopes.RemoveAll(path => ignoredScopes.Contains(path, StringComparer.OrdinalIgnoreCase));
            if (scopes.Count == 0) return [];
            foreach (var path in statuses.Keys.Where(path => !scopes.Any(scope => Inside(path, scope) || Inside(scope, path))).ToArray())
                statuses.Remove(path);
        }

        await ExpandUnversionedDirectoriesAsync(statuses, scopes, localRoot, token);

        localIgnores?.Reload();
        if (localIgnores != null)
            foreach (var path in statuses.Keys.ToArray()) statuses[path] = localIgnores.Apply(statuses[path]);

        var changes = statuses.Values.Where(status => !status.IsLocallyIgnored && (status.HasLocalChanges
            || status.Item is "unversioned" or "missing" or "obstructed" or "incomplete")).ToList();
        if (changes.Count == 0) return [];
        var infos = new Dictionary<string, SvnInfo>(StringComparer.OrdinalIgnoreCase);
        var versionedScopes = scopes.Where(path => !statuses.TryGetValue(path, out var status)
            || status.Item is not ("unversioned" or "ignored" or "external")).ToList();
        if (versionedScopes.Count > 0)
        {
            foreach (var batch in versionedScopes.Chunk(32))
            {
                var info = SvnXml.ReadInfo(await runner.RunAsync(new[] { "info", "--xml", "--depth", "infinity" }
                    .Concat(batch.Select(SvnRunner.Target)), token));
                foreach (var item in info) infos[Path.GetFullPath(item.Path)] = item;
            }
        }
        bool IsDirectory(WorkingStatus status) => Directory.Exists(status.Path) || infos.GetValueOrDefault(status.Path)?.IsDirectory == true;
        var deletedDirectories = changes.Where(status => status.Item == "deleted" && IsDirectory(status)).Select(status => status.Path).ToList();
        var newParents = changes.Where(status => status.Item is "added" or "replaced" or "unversioned" && IsDirectory(status)).Select(status => status.Path).ToList();
        // SVN directory deletion is indivisible; represent it once rather than offer misleading child checkboxes.
        return changes.Where(status => !deletedDirectories.Any(parent => RepositoryService.IsDescendant(status.Path, parent))
                && !OfficeTemporaryFiles.IsLockFile(status.Path, IsDirectory(status)))
            .Select(status => new CommitChange(status.Path, Path.GetRelativePath(localRoot, status.Path).Replace('\\', '/'), IsDirectory(status), status,
                newParents.Where(parent => RepositoryService.IsDescendant(status.Path, parent)).ToList(),
                status.Item == "unversioned" ? AdditionUnavailableReason(status.Path, localRoot)
                    : new[] { status.MovedFrom, status.MovedTo }.Any(path => path != null && !Inside(path, localRoot))
                        ? "移动的另一端不在当前打开范围，请打开共同上级目录或工作副本根目录后提交。" : "")
            { RequiredMoves = new[] { status.MovedFrom, status.MovedTo }.Where(path => path != null).Cast<string>().ToArray() })
            .OrderBy(change => change.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<string> CommitSelectedAsync(IReadOnlyList<CommitChange> selection, string message, string localRoot, CancellationToken token)
    {
        if (selection.Count == 0) throw new ArgumentException("请至少勾选一个提交项目。");
        if (selection.Any(change => !change.CanCommit)) throw new ArgumentException("所选项目包含缺失、冲突或不可提交项目，请先处理。");
        var paths = selection.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Recheck local status after the dialog; external edits must not turn an unchecked item into a target.
        var current = (await ReadCommitChangesAsync(paths, localRoot, token)).ToDictionary(change => change.Path, StringComparer.OrdinalIgnoreCase);
        if (paths.Any(path => !current.TryGetValue(path, out var change) || !change.CanCommit))
            throw new ArgumentException("部分项目的本地状态已变化，请重新打开提交窗口检查。");
        var checkedChanges = paths.Select(path => current[path]).ToList();
        if (checkedChanges.Any(change => change.RequiredParents.Any(parent => !paths.Contains(parent))))
            throw new ArgumentException("提交新增子项时，必须同时勾选其新增或替换的父目录。");
        if (checkedChanges.Any(change => change.RequiredMoves.Any(path => !paths.Contains(path))))
            throw new ArgumentException("移动操作需要同时勾选原位置的删除和新位置的新增，请重新检查提交列表。");

        var additions = checkedChanges.Where(change => change.NeedsAddition).OrderBy(change => change.Path.Length).Select(change => change.Path).ToList();
        var output = "";
        // Shallow, explicit add targets prevent unchecked children from being scheduled for addition.
        if (additions.Count > 0)
            output = await RunWithTargetsAsync(["add", "--parents", "--depth", "empty"], additions, token);
        // Every checked item is an explicit target. Directory properties/additions must not recursively
        // commit unchecked children. A directory deletion remains a whole-tree operation in SVN.
        return output + await RunWithTargetsAsync(["commit", "--depth", "empty"], paths, token, message);
    }

    private async Task<string> RunWithTargetsAsync(IReadOnlyList<string> arguments, IEnumerable<string> paths,
        CancellationToken token, string? message = null)
    {
        // A targets file avoids the Windows command-line length limit when selecting many files.
        var directory = temporaryDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var targetsFile = Path.Combine(directory, "svnclient-targets-" + Guid.NewGuid().ToString("N") + ".txt");
        var targets = paths.Select(SvnRunner.Target).ToList();
        try
        {
            try
            {
                await File.WriteAllLinesAsync(targetsFile, targets, SvnRunner.TargetFileEncoding, token);
            }
            catch (System.Text.EncoderFallbackException)
            {
                // Some Windows SVN clients also narrow process arguments to the local code page.
                // Refuse unsupported names rather than silently sending a different target path.
                throw new ArgumentException("当前 SVN 客户端的系统编码无法表示部分文件名，请使用支持这些文件名的 SVN 客户端或修改文件名。");
            }
            var command = arguments.Concat(["--targets", targetsFile]);
            return message == null ? await runner.RunAsync(command, token) : await RunWithTextFileAsync(command, message, token);
        }
        finally
        {
            if (File.Exists(targetsFile)) File.Delete(targetsFile);
        }
    }

    private async Task<IReadOnlyList<WorkingStatus>> ReadLocalStatusesAsync(IEnumerable<string> paths, string depth, CancellationToken token)
    {
        var statuses = new List<WorkingStatus>();
        foreach (var batch in paths.Chunk(32))
            statuses.AddRange(SvnXml.ReadStatus(await runner.RunAsync(new[] { "status", "--xml", "--depth", depth, "--ignore-externals" }
                .Concat(batch.Select(SvnRunner.Target)), token)));
        return statuses;
    }

    private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || RepositoryService.IsDescendant(path, root);
}
