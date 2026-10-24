using System.IO;
using System.Text.Json;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private string? sessionCacheDirectory;
    public Exception? OpenError { get; private set; }

    public void UseDedicatedSessionCache(string location, bool isLocal) =>
        sessionCacheDirectory = RepositoryLocation.CacheDirectory(Settings.CacheDirectory, location, isLocal);

    public SavedRepositoryTab? CaptureSession() => Session == null ? null : new(Session.LocalRoot ?? Session.Url,
        Session.LocalRoot != null, currentRelative, AllNodes().Where(node => !node.IsPlaceholder && node.IsExpanded)
            .Select(node => node.RelativePath).ToList());

    public Task RestoreTabInBackgroundAsync(SavedRepositoryTab saved)
    {
        if (IsBusy || disposed) return Task.CompletedTask;
        CancelBackgroundReads();
        return StartBackgroundRead("正在恢复仓库会话", token => OpenCachedSessionAsync(saved.Location, saved.IsLocal,
            saved.RelativeDirectory, saved.ExpandedDirectories, token), navigation: false);
    }

    public Task RestoreInBackgroundAsync() => string.IsNullOrWhiteSpace(Settings.LastLocation)
        ? Task.CompletedTask : OpenInBackgroundAsync(Settings.LastLocation, Settings.LastLocationIsLocal, restoreNavigation: true);

    public Task OpenInBackgroundAsync(string location, bool isLocal, bool restoreNavigation = false)
    {
        if (IsBusy || disposed) return Task.CompletedTask;
        var relative = restoreNavigation ? Settings.LastRelativeDirectory : "";
        var expanded = restoreNavigation ? Settings.ExpandedDirectories.ToList() : [];
        CancelBackgroundReads();
        return StartBackgroundRead("后台初始化服务器信息，可继续浏览缓存", token =>
            OpenCachedSessionAsync(location, isLocal, relative, expanded, token), navigation: false);
    }

    private async Task OpenCachedSessionAsync(string location, bool isLocal, string relative,
        IReadOnlyList<string> expanded, CancellationToken token)
    {
        OpenError = null;
        try { await OpenCachedSessionCoreAsync(location, isLocal, relative, expanded, token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OpenError = ex;
            throw;
        }
    }

    private async Task OpenCachedSessionCoreAsync(string location, bool isLocal, string relative,
        IReadOnlyList<string> expanded, CancellationToken token)
    {
        SavedBrowsingSession? saved = null;
        try
        {
            // Disk reads and JSON parsing also run off the dispatcher for large working copies.
            saved = await Task.Run(() => new BrowsingCacheStore(sessionCacheDirectory ?? Settings.CacheDirectory).Load(location, isLocal)
                ?? (sessionCacheDirectory == null ? null : new BrowsingCacheStore(Settings.CacheDirectory).Load(location, isLocal)), token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            AddLog($"浏览缓存无法使用，将重新查询服务器：{ex.Message}");
        }
        token.ThrowIfCancellationRequested();

        RepositorySession? localSession = null;
        if (saved != null && isLocal)
        {
            // Local svn info needs no server. A switched or replaced working copy must not reuse old URLs.
            localSession = await FetchSessionAsync(location, true, token);
            if (!SameRepository(saved.Session, localSession)) saved = null;
        }
        token.ThrowIfCancellationRequested();
        if (saved == null)
        {
            await OpenAsync(location, isLocal, token);
            await RestoreNavigationAsync(relative, expanded, token);
            return;
        }

        ApplySavedSession(saved, relative, expanded);
        StatusMessage = "已显示上次浏览缓存，正在后台检查服务器";

        // Online sessions can be displayed before the first network call. Verify identity before refreshing.
        var verified = localSession ?? await FetchSessionAsync(location, false, token);
        token.ThrowIfCancellationRequested();
        if (!SameRepository(saved.Session, verified))
        {
            // The URL may now refer to a different repository. Do not keep its old operation targets.
            Session = null;
            Snapshot = null;
            LastFullRefreshAt = default;
            cacheGeneration++;
            navigationVersion++;
            directoryCache.Clear();
            Roots.Clear();
            currentRelative = "";
            SetEntries([]);
            NotifyLocation();
            AddLog("仓库身份已变化，已清除旧浏览缓存并重新初始化。");
            await OpenAsync(location, isLocal, token);
            return;
        }
        if (verified.LocalRoot != null) Settings.RememberWorkingCopy(verified.LocalRoot, verified.Url, verified.RepositoryId);
        await RefreshAllAsync(token);
    }

    private void ApplySavedSession(SavedBrowsingSession saved, string relative, IReadOnlyList<string> expanded)
    {
        ResetSearchIndex();
        Session = saved.Session;
        Snapshot = LocalIgnores.Apply(saved.Snapshot);
        var refreshSummaries = Session.LocalRoot != null && (LocalIgnores.HasRules
            || saved.Snapshot.Statuses.Values.Any(status => status.IsLocallyIgnored)
            || saved.Directories.Values.Any(directory => directory.Entries.Any(entry => entry.IsLocallyIgnored))
            || Snapshot.Statuses.Values.Any(status => OfficeTemporaryFiles.IsLockFile(status.Path,
                Directory.Exists(status.Path) || Snapshot.LocalInfo.GetValueOrDefault(status.Path)?.IsDirectory == true)));
        LastFullRefreshAt = saved.LastFullRefreshAt;
        cacheGeneration++;
        navigationVersion++;
        directoryCache.Clear();
        foreach (var (path, directory) in saved.Directories)
        {
            // Per-directory versions/times remain accurate without duplicating a full snapshot on disk.
            var snapshot = Snapshot with { Revision = directory.Revision, CheckedAt = directory.CheckedAt };
            var entries = LocalIgnores.Apply(snapshot, directory.Entries);
            if (refreshSummaries) entries = entries.Select(entry => entry.IsDirectory
                ? entry with { Summary = RepositoryService.GetSummary(snapshot, entry.LocalPath) } : entry).ToList();
            directoryCache[path] = new(snapshot, entries) { HasServerListing = directory.HasServerListing };
        }
        Address = Session.LocalRoot ?? Session.Url;
        var wasUpdating = IsUpdatingTree;
        IsUpdatingTree = true;
        try
        {
            Roots.Clear();
            Roots.Add(CreateRootNode(Session));
            if (!IsValidRelativeDirectory(relative)) relative = "";
            var needed = new HashSet<string>(StringComparer.Ordinal) { "" };
            foreach (var path in expanded.Append(relative).Where(IsValidRelativeDirectory))
            {
                var ancestor = path;
                while (ancestor.Length > 0)
                {
                    needed.Add(ancestor);
                    ancestor = ParentDirectory(ancestor);
                }
            }
            // Build visible branches only; other saved lists stay available for instant later browsing.
            var nodes = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal) { [""] = Roots[0] };
            foreach (var path in needed.OrderBy(path => path.Length))
            {
                if (nodes.TryGetValue(path, out var node) && directoryCache.TryGetValue(path, out var directory))
                {
                    UpdateChildren(node, directory.Entries, updateSummary: false);
                    node.Loaded = directory.HasServerListing;
                    node.Summary = refreshSummaries ? GetDirectorySummary(node, directory.Snapshot) : saved.Directories[path].Summary;
                    if (refreshSummaries)
                        foreach (var child in node.Children.Where(child => !child.IsPlaceholder))
                            child.Summary = GetDirectorySummary(child, directory.Snapshot);
                    foreach (var child in node.Children.Where(child => !child.IsPlaceholder)) nodes[child.RelativePath] = child;
                }
            }
            Roots[0].IsExpanded = true;
            foreach (var path in expanded)
                if (nodes.TryGetValue(path, out var node)) node.IsExpanded = true;
            while (relative.Length > 0 && (!directoryCache.ContainsKey(relative) || !nodes.ContainsKey(relative)))
                relative = ParentDirectory(relative);
            ShowDirectory(relative, nodes[relative], directoryCache[relative].Entries, updateChildren: false);
            // Ensure all ancestors of the restored selection are visible.
            var parent = relative;
            while (parent.Length > 0)
            {
                parent = ParentDirectory(parent);
                if (nodes.TryGetValue(parent, out var node)) node.IsExpanded = true;
            }
        }
        finally { IsUpdatingTree = wasUpdating; }
        RememberSession();
        NotifyLocation();
    }

    private async Task RestoreNavigationAsync(string relative, IReadOnlyList<string> expanded, CancellationToken token)
    {
        foreach (var path in expanded.OrderBy(path => path.Count(c => c == '/')))
        {
            if (!IsValidRelativeDirectory(path)) continue;
            var node = await FindNearestDirectoryAsync(path, token);
            if (node.RelativePath == path)
            {
                await LoadChildrenAsync(node, token);
                node.IsExpanded = true;
            }
        }
        if (!IsValidRelativeDirectory(relative)) relative = "";
        var destination = await FindNearestDirectoryAsync(relative, token);
        if (destination.RelativePath != relative)
            AddLog($"上次目录已不存在，恢复到最近的上级目录：{destination.RelativePath}");
        await NavigateAsync(destination.RelativePath, token);
    }

    private void SaveBrowsingCache()
    {
        if (Session == null || Snapshot == null || !directoryCache.ContainsKey("")) return;
        var nodes = AllNodes().Where(node => !node.IsPlaceholder).ToDictionary(node => node.RelativePath, StringComparer.Ordinal);
        var directories = directoryCache.ToDictionary(pair => pair.Key,
            pair => new SavedDirectory(pair.Value.Snapshot.Revision, pair.Value.Snapshot.CheckedAt,
                pair.Value.Entries, nodes.GetValueOrDefault(pair.Key)?.Summary ?? "未检查") { HasServerListing = pair.Value.HasServerListing }, StringComparer.Ordinal);
        new BrowsingCacheStore(sessionCacheDirectory ?? Settings.CacheDirectory).Save(new(1, Session, Snapshot, LastFullRefreshAt, directories));
    }

    private Task<RepositorySession> FetchSessionAsync(string location, bool isLocal, CancellationToken token) =>
        Task.Run(() => isLocal ? repository.OpenWorkingCopyAsync(location, token) : repository.OpenUrlAsync(location, token), token);

    private static bool SameRepository(RepositorySession left, RepositorySession right) =>
        left.RepositoryId == right.RepositoryId && new Uri(left.Url).Equals(new Uri(right.Url))
        && new Uri(left.RepositoryRoot).Equals(new Uri(right.RepositoryRoot))
        && string.Equals(left.LocalRoot, right.LocalRoot, StringComparison.OrdinalIgnoreCase);

    private static DirectoryNode CreateRootNode(RepositorySession session)
    {
        var name = session.LocalRoot != null ? Path.GetFileName(session.LocalRoot)
            : Uri.UnescapeDataString(new Uri(session.Url).Segments.Last().Trim('/'));
        return new DirectoryNode(string.IsNullOrWhiteSpace(name) ? "仓库" : name, "");
    }
}
