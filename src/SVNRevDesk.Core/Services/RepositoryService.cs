using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed class RepositoryService(SvnRunner runner) : IRepositoryService
{
    public async Task<RepositorySession> OpenWorkingCopyAsync(string directory, CancellationToken token)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var info = SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", SvnRunner.Target(directory)], token)).Single();
        if (!info.IsDirectory || info.WorkingCopyRoot == null)
            throw new InvalidOperationException("请选择已检出的 SVN 工作副本目录。");
        return new RepositorySession(info.Url, info.RepositoryRoot, info.RepositoryId, directory);
    }

    public async Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "svn" or "svn+ssh" or "file"))
            throw new ArgumentException("请输入有效的 SVN 仓库 URL。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请通过登录窗口输入凭据，不要将账号密码写入 URL。");
        var info = SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", SvnRunner.Target(uri.AbsoluteUri)], token)).Single();
        if (!info.IsDirectory) throw new InvalidOperationException("请输入仓库目录地址，而不是单个文件地址。");
        return new RepositorySession(info.Url, info.RepositoryRoot, info.RepositoryId, null);
    }

    public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token) =>
        ReadSnapshotAsync(session, "", "infinity", token);

    public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
    {
        if (Path.IsPathRooted(scope.RelativePath) || scope.RelativePath.Contains('\\')
            || scope.RelativePath.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("刷新目录必须位于当前仓库内部。");
        return ReadSnapshotAsync(session, scope.RelativePath, scope.IncludeDescendants ? "infinity" : "immediates", token);
    }

    private async Task<RepositorySnapshot> ReadSnapshotAsync(RepositorySession session, string relative, string depth, CancellationToken token)
    {
        var head = SvnXml.ReadInfo(await runner.RunAsync(
            ["info", "--xml", "-r", "HEAD", SvnRunner.Target(session.RepositoryRoot)], token)).Single().Revision;
        var statuses = new Dictionary<string, WorkingStatus>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, SvnInfo>(StringComparer.OrdinalIgnoreCase);
        if (session.LocalRoot != null)
        {
            var target = GetLocalPath(session, relative)!;
            var statusXml = await runner.RunAsync(["status", "--xml", "--verbose", "--show-updates",
                "--ignore-externals", "--no-ignore", "--depth", depth, "-r", head.ToString(), SvnRunner.Target(target)], token);
            foreach (var status in SvnXml.ReadStatus(statusXml)) statuses[status.Path] = status;
            // A sparse or unversioned directory has no working-copy metadata to read.
            if (statuses.TryGetValue(target, out var targetStatus)
                && (targetStatus.BaseRevision.HasValue || targetStatus.Item is "added" or "replaced")
                && targetStatus.Item is not ("unversioned" or "ignored" or "external"))
            {
                var infoXml = await runner.RunAsync(["info", "--xml", "--depth", depth, SvnRunner.Target(target)], token);
                foreach (var info in SvnXml.ReadInfo(infoXml)) infos[Path.GetFullPath(info.Path)] = info;
            }
        }
        return new RepositorySnapshot(head, DateTimeOffset.Now, statuses, infos);
    }

    public async Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session,
        RepositorySnapshot snapshot, string relativePath, CancellationToken token)
    {
        var localDirectory = GetLocalPath(session, relativePath);
        var directoryUrl = GetDirectoryUrl(session, snapshot, relativePath);
        var localStatus = FindStatus(snapshot, localDirectory);
        var localOnly = localStatus?.Item is "added" or "unversioned" or "ignored" || localStatus?.RepositoryItem == "deleted";
        // Children of a local-only directory cannot have a repository URL yet.
        if (!localOnly && localDirectory != null)
            localOnly = snapshot.Statuses.Values.Any(s => (s.Item is "added" or "unversioned" or "ignored")
                && IsDescendant(localDirectory, s.Path));

        var serverEntries = new List<ServerEntry>();
        if (!localOnly)
        {
            var xml = await runner.RunAsync(["list", "--xml", "--depth", "immediates", "-r",
                snapshot.Revision.ToString(), SvnRunner.AtRevision(directoryUrl, snapshot.Revision.ToString())], token);
            serverEntries.AddRange(SvnXml.ReadList(xml));
        }

        var entries = new Dictionary<string, BrowserEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in serverEntries)
        {
            if (OfficeTemporaryFiles.IsLockFile(server.Name, server.IsDirectory)) continue;
            var relative = JoinRelative(relativePath, server.Name);
            var localPath = GetLocalPath(session, relative);
            var status = FindStatus(snapshot, localPath);
            var exists = localPath != null && (server.IsDirectory ? Directory.Exists(localPath) : File.Exists(localPath));
            var state = FileStateClassifier.Classify(true, exists, status, session.LocalRoot != null);
            snapshot.LocalInfo.TryGetValue(localPath ?? "", out var info);
            entries[server.Name] = new BrowserEntry(server.Name, relative, AppendUrl(directoryUrl, server.Name),
                localPath, server.IsDirectory, state, status?.BaseRevision,
                status?.ChangedRevision ?? info?.ChangedRevision, server.ChangedRevision, server.Size,
                server.Author, server.ChangedAt, server.Lock ?? status?.ServerLock,
                GetDetail(status, session.LocalRoot != null), server.IsDirectory ? GetSummary(snapshot, localPath) : "");
        }

        if (localDirectory != null)
        {
            if (Directory.Exists(localDirectory))
            {
                foreach (var local in Directory.EnumerateFileSystemEntries(localDirectory))
                {
                    if (Path.GetFileName(local).Equals(".svn", StringComparison.OrdinalIgnoreCase)) continue;
                    AddLocalEntry(entries, session, snapshot, relativePath, local);
                }
            }
            // Deleted and missing items may no longer appear in filesystem enumeration.
            foreach (var status in snapshot.Statuses.Values)
                if (string.Equals(Path.GetDirectoryName(status.Path), localDirectory, StringComparison.OrdinalIgnoreCase))
                    AddLocalEntry(entries, session, snapshot, relativePath, status.Path);
        }
        return entries.Values.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddLocalEntry(Dictionary<string, BrowserEntry> entries, RepositorySession session,
        RepositorySnapshot snapshot, string parentRelative, string localPath)
    {
        var name = Path.GetFileName(localPath);
        if (entries.ContainsKey(name) || name.Equals(".svn", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase)) return;
        var status = FindStatus(snapshot, localPath);
        if (status == null && snapshot.Statuses.Values.Any(parent => parent.Item == "ignored" && IsDescendant(localPath, parent.Path)))
            status = new(localPath, "ignored", "none", "", "", null, null, false, false, null, null);
        if (status == null && snapshot.Statuses.Values.Any(parent => parent.Item == "unversioned" && IsDescendant(localPath, parent.Path)))
            status = new WorkingStatus(localPath, "unversioned", "none", "", "", null, null, false, false, null, null)
            { IsLocallyIgnored = snapshot.Statuses.Values.Any(parent => parent.IsLocallyIgnored && IsDescendant(localPath, parent.Path)) };
        snapshot.LocalInfo.TryGetValue(localPath, out var info);
        var directory = info?.IsDirectory ?? Directory.Exists(localPath);
        if (OfficeTemporaryFiles.IsLockFile(name, directory)) return;
        var relative = JoinRelative(parentRelative, name);
        var exists = directory ? Directory.Exists(localPath) : File.Exists(localPath);
        // A moved/copied directory carries normal children from its copy source. Their new paths
        // do not exist on the server yet, even though those children already have SVN metadata.
        var displayStatus = status;
        if (status?.Item is "normal" or "modified" or "replaced"
            && snapshot.Statuses.Values.Any(parent => parent.Item == "added" && IsDescendant(localPath, parent.Path)))
            displayStatus = status with { Item = "added" };
        var state = FileStateClassifier.Classify(false, exists, displayStatus, true);
        entries[name] = new BrowserEntry(name, relative, AppendUrl(session.Url, relative), localPath, directory,
            state, status?.BaseRevision, status?.ChangedRevision ?? info?.ChangedRevision, null,
            !directory && exists ? new FileInfo(localPath).Length : null, "", null, status?.ServerLock,
            GetDetail(status, true), directory ? GetSummary(snapshot, localPath) : "")
        { IsUnversioned = status?.Item == "unversioned" };
    }

    public static IReadOnlyList<BrowserEntry> ReadLocalDirectory(RepositorySession session, RepositorySnapshot snapshot,
        string relative, IReadOnlyList<BrowserEntry> previous, bool hasServerListing)
    {
        var entries = new Dictionary<string, BrowserEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in previous)
        {
            if (entry.ServerRevision == null || OfficeTemporaryFiles.IsLockFile(entry.Name, entry.IsDirectory)) continue;
            var status = FindStatus(snapshot, entry.LocalPath);
            entries[entry.Name] = entry with
            {
                State = FileStateClassifier.Classify(true, entry.ExistsLocally, status, true),
                BaseRevision = status?.BaseRevision ?? entry.BaseRevision,
                LocalRevision = status?.ChangedRevision ?? entry.LocalRevision,
                Detail = GetDetail(status, true),
                Summary = entry.IsDirectory ? GetSummary(snapshot, entry.LocalPath) : entry.Summary
            };
        }
        var local = GetLocalPath(session, relative)!;
        foreach (var path in Directory.Exists(local) ? Directory.EnumerateFileSystemEntries(local) : [])
        {
            if (Path.GetFileName(path).StartsWith(".svnclient-new-", StringComparison.Ordinal)) continue;
            AddLocalEntry(entries, session, snapshot, relative, path);
        }
        foreach (var status in snapshot.Statuses.Values.Where(status => string.Equals(Path.GetDirectoryName(status.Path), local, StringComparison.OrdinalIgnoreCase)))
            AddLocalEntry(entries, session, snapshot, relative, status.Path);
        // An unvisited server directory is a local preview, not a complete remote listing.
        if (!hasServerListing)
            foreach (var (name, entry) in entries.ToList())
            {
                var status = FindStatus(snapshot, entry.LocalPath);
                if (entry.State != FileState.Ignored && status?.Item is not ("added" or "unversioned") && status?.RepositoryItem != "deleted")
                    entries[name] = entry with { State = FileState.Unchecked, Detail = "已显示本地项目，服务器目录列表尚未缓存。" };
            }
        return entries.Values.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string? GetLocalPath(RepositorySession session, string relative)
    {
        if (session.LocalRoot == null) return null;
        var path = Path.GetFullPath(Path.Combine(session.LocalRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (path != session.LocalRoot && !IsDescendant(path, session.LocalRoot))
            throw new InvalidOperationException("文件路径超出了工作副本范围。");
        return path;
    }

    private static string GetDirectoryUrl(RepositorySession session, RepositorySnapshot snapshot, string relative)
    {
        var local = GetLocalPath(session, relative);
        if (local != null && snapshot.LocalInfo.TryGetValue(local, out var info))
        {
            if (info.RepositoryId != session.RepositoryId)
                throw new InvalidOperationException("外部工作副本属于其他仓库，请单独打开。");
            return info.Url;
        }
        return AppendUrl(session.Url, relative);
    }

    public static string AppendUrl(string url, string relative) => relative.Length == 0 ? url
        : url.TrimEnd('/') + "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
    public static string JoinRelative(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    public static bool IsDescendant(string path, string parent) => path.StartsWith(
        parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static WorkingStatus? FindStatus(RepositorySnapshot snapshot, string? local) =>
        local != null && snapshot.Statuses.TryGetValue(local, out var status) ? status : null;

    public static string GetDetail(WorkingStatus? status, bool hasWorkingCopy)
    {
        if (!hasWorkingCopy) return "未关联本地工作副本";
        if (status == null) return "尚未下载，或未纳入版本控制";
        if (status.IsLocallyIgnored) return LocalIgnoreStore.LocalDetail;
        var parts = new List<string>();
        if (status.Item == "deleted") parts.Add("已安排本地删除，尚未提交");
        if (status.Item == "added") parts.Add("已安排添加，尚未提交");
        if (status.Item == "unversioned") parts.Add("未纳入版本控制");
        if (status.Item == "ignored") parts.Add("被 SVN 共享属性或客户端全局规则忽略；本机操作不会修改这些规则");
        if (status.Item == "obstructed") parts.Add("文件类型阻塞，需要人工处理");
        if (status.Item == "external") parts.Add("外部工作副本，请单独打开");
        if (status.Switched) parts.Add("已切换到其他仓库路径");
        if (status.Properties == "modified") parts.Add("属性已修改");
        if (status.RepositoryProperties == "modified") parts.Add("服务器属性已修改");
        if (status.LocalLock != null && status.ServerLock == null) parts.Add("本地锁令牌可能已失效");
        return string.Join("；", parts);
    }

    public static string GetSummary(RepositorySnapshot snapshot, string? directory)
    {
        if (directory == null) return "未关联本地";
        var ignoredDirectories = snapshot.Statuses.Values.Where(s => s.Item == "ignored" || s.IsLocallyIgnored).Select(s => s.Path).ToArray();
        var counts = snapshot.Statuses.Values.Where(s => IsDescendant(s.Path, directory) && s.Item is not ("ignored" or "external") && !s.IsLocallyIgnored)
            .Where(s => !ignoredDirectories.Any(parent => IsDescendant(s.Path, parent)))
            .Where(s => !OfficeTemporaryFiles.IsLockFile(s.Path,
                Directory.Exists(s.Path) || snapshot.LocalInfo.GetValueOrDefault(s.Path)?.IsDirectory == true))
            .Select(s => FileStateClassifier.Classify(s.RepositoryItem != "deleted" && s.Item is not ("added" or "unversioned"),
                File.Exists(s.Path) || Directory.Exists(s.Path), s, true))
            .Where(state => state != FileState.Equal && state != FileState.Unchecked)
            .GroupBy(state => state).Select(group => $"{group.Key.DisplayName()} {group.Count()}").ToList();
        // Sparse, never-downloaded descendants are discovered lazily; do not claim a complete count.
        return counts.Count == 0 ? "已检查项无差异" : string.Join(" · ", counts);
    }
}
