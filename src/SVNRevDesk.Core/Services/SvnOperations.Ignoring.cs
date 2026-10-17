using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public async Task<string> SetIgnoredAsync(string path, string localRoot, bool ignored, CancellationToken token)
    {
        if (localIgnores == null) throw new InvalidOperationException("尚未配置本机忽略存储。");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        if (!RepositoryService.IsDescendant(path, localRoot))
            throw new InvalidOperationException("只能忽略当前打开范围内的项目，不能忽略根目录。");
        var unavailable = AdditionUnavailableReason(path, localRoot);
        if (unavailable.Length > 0) throw new InvalidOperationException(unavailable);
        // SVN does not report a child inside an unversioned/ignored directory. Its nearest
        // reported ancestor establishes the child's status without adding any SVN metadata.
        var targets = new List<string> { path };
        for (var parent = Path.GetDirectoryName(path); parent != null && Inside(parent, localRoot); parent = Path.GetDirectoryName(parent))
            targets.Add(parent);
        var statuses = SvnXml.ReadStatus(await runner.RunAsync(new[] { "status", "--xml", "--no-ignore", "--verbose", "--depth", "empty" }
            .Concat(targets.Select(SvnRunner.Target)), token));
        var status = statuses.Where(item => targets.Contains(item.Path, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Path.Length).FirstOrDefault();
        if (status?.Item is not ("unversioned" or "ignored"))
            throw new InvalidOperationException("本机忽略只适用于尚未加入版本控制的项目。");
        localIgnores.Reload();
        if (!ignored && localIgnores.FindRule(path) == null)
            throw new InvalidOperationException("此项目由 SVN 属性或全局规则忽略，无法通过本机取消操作覆盖。本次未修改 SVN 规则。");
        token.ThrowIfCancellationRequested();
        localIgnores.Set(path, ignored);
        return $"{(ignored ? "已仅在本机忽略" : "已取消本机忽略")}：{path}\n本机配置：{localIgnores.ConfigurationPath}\n未修改 SVN 属性，不会提交或同步给其他用户。";
    }

    private async Task<string> AddWithLocalIgnoresAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        var additions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (localIgnores!.Matches(path)) throw new InvalidOperationException("请先取消本机忽略，再添加项目：" + path);
            var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            while (root != null && !Directory.Exists(Path.Combine(root, ".svn"))) root = Path.GetDirectoryName(root);
            if (root == null) throw new InvalidOperationException("项目不在 SVN 工作副本中：" + path);
            var changes = await ReadCommitChangesAsync([path], root, token);
            foreach (var change in changes.Where(change => change.NeedsAddition && change.CanCommit)) additions.Add(change.Path);
        }
        return additions.Count == 0 ? "没有可添加的项目。" : await RunWithTargetsAsync(["add", "--parents", "--depth", "empty"], additions.OrderBy(path => path.Length), token);
    }
}
