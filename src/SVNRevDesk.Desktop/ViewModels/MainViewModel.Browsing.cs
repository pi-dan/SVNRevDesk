using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Desktop.ViewModels;

// Directory caches and cancellable background reads are kept separate from exclusive SVN operations.
public sealed partial class MainViewModel
{
    private sealed record CachedDirectory(RepositorySnapshot Snapshot, IReadOnlyList<BrowserEntry> Entries)
    {
        public bool HasServerListing { get; init; } = true;
    }
    private sealed class BackgroundRead(string description, bool navigation)
    {
        public string Description { get; } = description;
        public bool Navigation { get; } = navigation;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly Dictionary<string, CachedDirectory> directoryCache = new(StringComparer.Ordinal);
    private readonly List<BackgroundRead> backgroundReads = [];
    // Only index maintenance is fire-and-forget: unlike navigation reads nothing re-triggers it,
    // so an explicit operation that cancels it must requeue it afterwards.
    private BackgroundRead? queuedIndexRead;
    private long navigationVersion;
    private long readStatusVersion;
    private bool disposed;
    private bool backgroundLoginOffered;
    public Func<bool>? RequestBackgroundLogin { get; set; }
    public bool IsUpdatingTree { get; private set; }
    public bool IsReading => backgroundReads.Count > 0;
    public string ReadStatusMessage => backgroundReads.LastOrDefault()?.Description ?? "";
    public string DirectoryCacheText => directoryCache.TryGetValue(currentRelative, out var cached)
        ? !cached.HasServerListing ? "本地目录预览 · 服务器列表尚未缓存"
            : $"目录缓存 r{cached.Snapshot.Revision} · 最近检查 {cached.Snapshot.CheckedAt:yyyy-MM-dd HH:mm:ss}"
        : "此目录尚无缓存";

    public Task BrowseDirectoryAsync(string relative)
    {
        if (IsBusy || disposed || Session == null || Snapshot == null) return Task.CompletedTask;
        if (directoryCache.TryGetValue(relative, out var cached) && cached.HasServerListing && FindNode(relative) != null)
        {
            // A cached switch must not wait for an obsolete slow request, nor interrupt a full refresh.
            CancelBackgroundReads(navigationOnly: true);
            readStatusVersion++;
            StatusMessage = "已显示目录缓存";
            return NavigateAsync(relative, CancellationToken.None);
        }
        CancelBackgroundReads();
        return StartBackgroundRead("后台读取服务器目录，可继续切换目录", token => NavigateAsync(relative, token), navigation: true);
    }

    public Task SelectTreeDirectoryAsync(DirectoryNode node)
    {
        if (IsBusy || disposed || Session == null || Snapshot == null || node.IsPlaceholder)
            return Task.CompletedTask;
        var wasUpdating = IsUpdatingTree;
        IsUpdatingTree = true;
        try
        {
            // Keep only the selected level open, even if descendants were expanded on an earlier visit.
            var descendants = new Stack<DirectoryNode>(node.Children);
            while (descendants.TryPop(out var child))
            {
                child.IsExpanded = false;
                foreach (var descendant in child.Children) descendants.Push(descendant);
            }
            node.IsExpanded = true;
        }
        finally { IsUpdatingTree = wasUpdating; }
        return BrowseDirectoryAsync(node.RelativePath);
    }

    public Task ExpandDirectoryAsync(DirectoryNode node)
    {
        if (IsBusy || disposed || node.Loaded || node.IsPlaceholder) return Task.CompletedTask;
        return StartBackgroundRead("后台读取子目录，可继续浏览", token => LoadChildrenAsync(node, token), navigation: true);
    }

    public Task RefreshInBackgroundAsync()
    {
        if (IsBusy || disposed || Session == null) return Task.CompletedTask;
        CancelBackgroundReads();
        return StartBackgroundRead("后台刷新当前目录，可继续浏览", RefreshCurrentDirectoryAsync, navigation: false);
    }

    public Task RefreshAllInBackgroundAsync()
    {
        if (IsBusy || disposed || Session == null) return Task.CompletedTask;
        CancelBackgroundReads();
        return StartBackgroundRead("后台检查整个打开范围，可继续浏览", RefreshAllAsync, navigation: false);
    }

    public Task RefreshPathsInBackgroundAsync(IEnumerable<string> paths)
    {
        if (IsBusy || disposed || Session?.LocalRoot == null) return Task.CompletedTask;
        var changed = paths.ToList();
        CancelBackgroundReads();
        return StartBackgroundRead("后台检查变化所在目录，可继续浏览", token => RefreshPathsAsync(changed, token), navigation: false);
    }

    private Task StartBackgroundRead(string description, Func<CancellationToken, Task> action, bool navigation,
        bool reportCompletion = true, bool indexMaintenance = false)
    {
        var read = new BackgroundRead(description, navigation);
        var version = reportCompletion ? ++readStatusVersion : readStatusVersion;
        backgroundReads.Add(read);
        if (indexMaintenance) queuedIndexRead = read;
        NotifyReading();
        read.Task = ExecuteBackgroundReadAsync(read, version, action, reportCompletion);
        return read.Task;
    }

    // StopBackgroundReadsAsync cancels queued index maintenance; nothing else re-triggers it. An explicit
    // operation that cancels it must requeue it, or the background index label disappears for good.
    internal void RequeueInterruptedIndexMaintenance()
    {
        if (queuedIndexRead != null) return;
        QueueSearchIndexUpdate(Session!);
    }

    private async Task ExecuteBackgroundReadAsync(BackgroundRead read, long version, Func<CancellationToken, Task> action,
        bool reportCompletion)
    {
        try
        {
            try
            {
                await action(read.Cancellation.Token);
            }
            catch (SvnException ex) when (ex.IsAuthenticationError && HasSession
                && !backgroundLoginOffered && RequestBackgroundLogin != null
                && version == readStatusVersion && !read.Cancellation.IsCancellationRequested && !disposed)
            {
                // Restored tabs retain directory caches but never retain session passwords.
                // Ask once instead of silently failing each newly visited directory.
                backgroundLoginOffered = true;
                if (!RequestBackgroundLogin()) throw;
                read.Cancellation.Token.ThrowIfCancellationRequested();
                await action(read.Cancellation.Token);
            }
            if (reportCompletion && version == readStatusVersion && !disposed && !read.Cancellation.IsCancellationRequested)
                StatusMessage = read.Navigation ? "目录已载入并缓存" : "后台刷新完成";
        }
        catch (OperationCanceledException)
        {
            if (reportCompletion && version == readStatusVersion && !disposed && !IsBusy)
                StatusMessage = "后台读取已取消，保留已载入的缓存。";
        }
        catch (Exception ex)
        {
            if (reportCompletion && version == readStatusVersion && !disposed)
                StatusMessage = ex is SvnException { IsAuthenticationError: true }
                    ? "SVN 认证失败，尚未读取服务器状态。请通过“文件 → SVN 登录”重新输入账号密码；本地文件仍可操作。"
                    : (HasSession ? "后台读取失败，保留目录缓存：" : "仓库打开失败：") + ex.Message.Split('\n')[0];
            if (!disposed) AddLog(ex.Message);
        }
        finally
        {
            backgroundReads.Remove(read);
            if (queuedIndexRead == read) queuedIndexRead = null;
            read.Cancellation.Dispose();
            if (!disposed) NotifyReading();
        }
    }

    private void CancelBackgroundReads(bool navigationOnly = false)
    {
        foreach (var read in backgroundReads.ToList())
            if (!navigationOnly || read.Navigation) read.Cancellation.Cancel();
    }

    private async Task StopBackgroundReadsAsync()
    {
        readStatusVersion++;
        CancelBackgroundReads();
        await Task.WhenAll(backgroundReads.Select(read => read.Task).ToArray());
    }

    private async Task DisposeRunnerAfterReadsAsync()
    {
        await Task.WhenAll(backgroundReads.Select(read => read.Task).ToArray());
        Runner.Dispose();
    }

    private void NotifyReading()
    {
        Notify(nameof(IsReading));
        Notify(nameof(ReadStatusMessage));
        Notify(nameof(CanCancel));
        Notify(nameof(EmptyStateTitle)); Notify(nameof(EmptyStateDescription));
    }

    private async Task<IReadOnlyList<BrowserEntry>> ReadCachedDirectoryAsync(RepositorySession session,
        RepositorySnapshot snapshot, string relative, CancellationToken token)
    {
        while (session == Session && !disposed)
        {
            token.ThrowIfCancellationRequested();
            if (directoryCache.TryGetValue(relative, out var cached) && cached.HasServerListing) return cached.Entries;
            var generation = cacheGeneration;
            var scope = new DirectoryRefresh(relative);
            var partial = await FetchDirectorySnapshotAsync(session, scope, token);
            snapshot = MergeSnapshot(session, Snapshot!, partial, scope);
            var entries = await FetchDirectoryAsync(session, snapshot, relative, token);
            token.ThrowIfCancellationRequested();
            if (session != Session || disposed) return entries;
            if (generation != cacheGeneration) continue;
            Snapshot = MergeSnapshot(session, Snapshot!, partial, scope);
            directoryCache[relative] = new(snapshot, entries);
            // Tree expansion can finish a preview independently of its original navigation.
            // Publish the rows and cache label as well, without navigating or changing selection.
            if (currentRelative == relative)
            {
                SetEntries(entries);
                Notify(nameof(DirectoryCacheText));
                Notify(nameof(SnapshotText));
            }
            return entries;
        }
        return [];
    }

    // XML parsing and local enumeration can also be costly. Keep service work off the WPF dispatcher.
    private Task<RepositorySnapshot> FetchSnapshotAsync(RepositorySession session, CancellationToken token) =>
        Task.Run(() => repository.RefreshAsync(session, token), token);

    private Task<IReadOnlyList<BrowserEntry>> FetchDirectoryAsync(RepositorySession session,
        RepositorySnapshot snapshot, string relative, CancellationToken token) =>
        Task.Run(() => repository.ReadDirectoryAsync(session, snapshot, relative, token), token);

    private void ShowDirectory(string relative, DirectoryNode? node, IReadOnlyList<BrowserEntry>? entries, bool updateChildren = true)
    {
        currentRelative = relative;
        SetEntries(entries ?? []);
        if (updateChildren && node != null && entries != null)
        {
            UpdateChildren(node, entries);
            if (directoryCache.TryGetValue(relative, out var cached)) node.Loaded = cached.HasServerListing;
        }
        foreach (var other in AllNodes().Where(other => !other.IsPlaceholder)) other.IsSelected = other == node;
        Notify(nameof(CurrentRelativePathText));
        Notify(nameof(CurrentPath));
        Notify(nameof(CurrentLocalPath)); Notify(nameof(CanOpenCurrentDirectory));
        Notify(nameof(DirectoryCacheText));
        Notify(nameof(SnapshotText));
        Notify(nameof(CanGoParent));
    }

    private static string ParentDirectory(string relative)
    {
        var separator = relative.LastIndexOf('/');
        return separator < 0 ? "" : relative[..separator];
    }
}
