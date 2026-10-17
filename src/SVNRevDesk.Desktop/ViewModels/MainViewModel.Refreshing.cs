using System.IO;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private long cacheGeneration;
    public DateTimeOffset LastFullRefreshAt { get; private set; }
    public RepositorySnapshot? CurrentDirectorySnapshot => directoryCache.TryGetValue(currentRelative, out var cached)
        ? cached.Snapshot : Snapshot;

    public Task RefreshCurrentDirectoryAsync(CancellationToken token) =>
        RefreshDirectoriesAsync([new DirectoryRefresh(currentRelative)], token);

    public Task RefreshPathsAsync(IEnumerable<string> paths, CancellationToken token)
    {
        if (Session?.LocalRoot == null) return Task.CompletedTask;
        var scopes = new List<DirectoryRefresh>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.Equals(Session.LocalRoot, StringComparison.OrdinalIgnoreCase)
                && !RepositoryService.IsDescendant(fullPath, Session.LocalRoot))
                throw new ArgumentException("刷新路径必须位于当前工作副本内部。");
            var relative = Path.GetRelativePath(Session.LocalRoot, fullPath).Replace('\\', '/');
            if (relative == ".") relative = "";
            var directory = relative.Length == 0 || Directory.Exists(fullPath)
                || Snapshot?.LocalInfo.GetValueOrDefault(fullPath)?.IsDirectory == true
                || directoryCache.Values.Any(cache => cache.Entries.Any(entry => entry.IsDirectory
                    && string.Equals(entry.LocalPath, fullPath, StringComparison.OrdinalIgnoreCase)));
            if (directory) scopes.Add(new DirectoryRefresh(relative, IncludeDescendants: true));
            // Refresh the containing list as well, so deletion, movement and the selected row are visible.
            if (relative.Length > 0) scopes.Add(new DirectoryRefresh(ParentDirectory(relative)));
        }
        return RefreshDirectoriesAsync(scopes, token);
    }

    public async Task RefreshSwitchedDirectoryAsync(CancellationToken token)
    {
        if (Session?.LocalRoot == null) return;
        var path = RepositoryService.GetLocalPath(Session, currentRelative)!;
        // Switching the opened root changes its URL. Read only its local mapping, not the whole working copy.
        Session = await repository.OpenWorkingCopyAsync(Session.LocalRoot, token);
        Settings.RememberWorkingCopy(Session.LocalRoot!, Session.Url, Session.RepositoryId);
        await RefreshPathsAsync([path], token);
        Address = Session.LocalRoot!;
        NotifyLocation();
    }

    public async Task RefreshDirectoriesAsync(IEnumerable<DirectoryRefresh> requestedScopes, CancellationToken token)
    {
        if (Session == null || Snapshot == null) return;
        var scopes = requestedScopes.Distinct().ToList();
        foreach (var scope in scopes)
            if (!IsValidRelativeDirectory(scope.RelativePath)) throw new ArgumentException("刷新目录必须位于当前仓库内部。");
        scopes = scopes.Where(scope => !scopes.Any(other => other != scope && other.IncludeDescendants
            && other.ContainsDirectory(scope.RelativePath))).OrderBy(scope => scope.RelativePath.Length).ToList();
        if (scopes.Count == 0) return;

        var session = Session;
        var generation = cacheGeneration;
        var snapshot = Snapshot;
        var updates = new List<(DirectoryRefresh Scope, RepositorySnapshot Snapshot)>();
        var refreshed = new Dictionary<string, CachedDirectory>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        void RemoveBranch(string path)
        {
            if (!removed.Add(path)) return;
            var empty = snapshot with
            {
                Statuses = new Dictionary<string, WorkingStatus>(),
                LocalInfo = new Dictionary<string, SvnInfo>()
            };
            var missingScope = new DirectoryRefresh(path, IncludeDescendants: true);
            updates.Add((missingScope, empty));
            snapshot = MergeSnapshot(session, snapshot, empty, missingScope);
        }
        for (var index = 0; index < scopes.Count; index++)
        {
            var scope = scopes[index];
            if (removed.Any(path => path == scope.RelativePath || DirectoryRefresh.IsInside(scope.RelativePath, path))) continue;
            var partial = await FetchDirectorySnapshotAsync(session, scope, token);
            updates.Add((scope, partial));
            snapshot = MergeSnapshot(session, snapshot, partial, scope);
            var paths = directoryCache.Keys.Concat(AllNodes().Where(node => node.IsExpanded && !node.IsPlaceholder)
                .Select(node => node.RelativePath)).Where(scope.ContainsDirectory).Append(scope.RelativePath)
                .Distinct(StringComparer.Ordinal).OrderBy(path => path.Length).ToList();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (removed.Any(parent => path == parent || DirectoryRefresh.IsInside(path, parent))) continue;
                try
                {
                    var entries = await FetchDirectoryAsync(session, snapshot, path, token);
                    refreshed[path] = new(snapshot, entries);
                    if (directoryCache.TryGetValue(path, out var previous))
                    {
                        var directories = entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
                        foreach (var child in previous.Entries.Where(entry => entry.IsDirectory && !directories.Contains(entry.RelativePath)))
                            RemoveBranch(child.RelativePath);
                    }
                }
                catch (SvnException ex) when (path.Length > 0 && ex.ErrorOutput.Contains("E160013", StringComparison.Ordinal))
                {
                    RemoveBranch(path);
                    AddLog($"目录已从服务器移除：{path}");
                    var parent = ParentDirectory(path);
                    if (!scopes.Any(existing => existing.ContainsDirectory(parent))) scopes.Add(new DirectoryRefresh(parent));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if (session != Session || generation != cacheGeneration || disposed) return;

        // Publish the requested scopes together. An error or cancellation leaves every old cache intact.
        // A user may have loaded an unrelated branch during this query; preserve its newer metadata too.
        foreach (var update in updates) Snapshot = MergeSnapshot(session, Snapshot, update.Snapshot, update.Scope);
        cacheGeneration++;
        foreach (var path in directoryCache.Keys.Where(path => removed.Any(parent => path == parent
            || DirectoryRefresh.IsInside(path, parent))).ToList()) directoryCache.Remove(path);
        foreach (var (path, cached) in refreshed)
            if (!removed.Any(parent => path == parent || DirectoryRefresh.IsInside(path, parent))) directoryCache[path] = cached;
        UpdateCachedSummaries();
        foreach (var (path, _) in refreshed.OrderBy(pair => pair.Key.Length))
        {
            var node = FindNode(path);
            if (node != null && directoryCache.TryGetValue(path, out var cached)) UpdateChildren(node, cached.Entries);
        }
        foreach (var node in AllNodes().Where(node => !node.IsPlaceholder))
        {
            node.Loaded = directoryCache.TryGetValue(node.RelativePath, out var cached) && cached.HasServerListing;
            node.Summary = GetDirectorySummary(node, Snapshot);
        }
        while (!directoryCache.ContainsKey(currentRelative)) currentRelative = ParentDirectory(currentRelative);
        ShowDirectory(currentRelative, FindNode(currentRelative), directoryCache[currentRelative].Entries);
        NotifyLocation();
        RememberSession();
    }

    private static RepositorySnapshot MergeSnapshot(RepositorySession session, RepositorySnapshot previous,
        RepositorySnapshot partial, DirectoryRefresh scope)
    {
        var directory = RepositoryService.GetLocalPath(session, scope.RelativePath);
        bool Contains(string path) => directory != null && (path.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || (scope.IncludeDescendants ? RepositoryService.IsDescendant(path, directory)
                : string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)));
        var statuses = previous.Statuses.Where(pair => !Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var infos = previous.LocalInfo.Where(pair => !Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, status) in partial.Statuses) statuses[path] = status;
        foreach (var (path, info) in partial.LocalInfo) infos[path] = info;
        return new(Math.Max(previous.Revision, partial.Revision), partial.CheckedAt, statuses, infos);
    }

    private void UpdateCachedSummaries()
    {
        foreach (var (path, cached) in directoryCache.ToList())
        {
            var entries = LocalIgnores.Apply(Snapshot!, cached.Entries).Select(entry => entry.IsDirectory
                ? entry with { Summary = RepositoryService.GetSummary(Snapshot!, entry.LocalPath) } : entry).ToList();
            if (!entries.SequenceEqual(cached.Entries)) directoryCache[path] = cached with { Entries = entries };
        }
    }

    private Task<RepositorySnapshot> FetchDirectorySnapshotAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token) =>
        Task.Run(() => repository.RefreshDirectoryAsync(session, scope, token), token);
}
