using System.Collections.Concurrent;
using System.Text.Json;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

// Queries read an immutable in-memory snapshot. Only index maintenance uses SVN or the filesystem.
public sealed partial class RepositorySearchIndex : IDisposable, IAsyncDisposable
{
    private sealed record PublishedIndex(SearchIndexStatus Status, BrowserEntry[] Entries);
    private readonly RepositorySession session;
    private readonly ISearchIndexSource source;
    private readonly LocalIgnoreStore ignores;
    private readonly SearchIndexStore store;
    private readonly SemaphoreSlim maintenance = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken stopToken;
    private readonly ConcurrentDictionary<string, byte> pendingLocal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer localTimer;
    private FileSystemWatcher? watcher;
    private PublishedIndex? published;
    private Dictionary<string, SearchServerNode> serverNodes = new(StringComparer.Ordinal);
    private SearchMount[] mounts;
    private bool loaded, hasIndex, localKnown;
    private int disposed;
    private long revision;
    private DateTimeOffset? serverCheckedAt, localCheckedAt;
    private string remoteError = "", localError = "", cacheWarning = "";
    private Task? disposal;

    public event Action? Changed;
    public string CachePath => store.Path;
    public SearchIndexStatus Status => Volatile.Read(ref published)?.Status ?? new(false, 0, 0, null, null, "", false);

    public RepositorySearchIndex(RepositorySession session, ISearchIndexSource source, string cacheDirectory,
        LocalIgnoreStore ignores, string? username = null)
    {
        this.session = session; this.source = source; this.ignores = ignores;
        store = new(cacheDirectory, session, username); stopToken = lifetime.Token;
        mounts = [new("", session.Url, true)];
        localTimer = new(state => { _ = ProcessLocalChangesAsync(); }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public static bool HasSavedIndex(string cacheDirectory, RepositorySession session, string? username = null) =>
        File.Exists(SearchIndexStore.CachePath(cacheDirectory, session, username));

    public (BrowserEntry Entry, long Revision)? FindEntry(string relative)
    {
        SearchIndexStore.ValidateRelative(relative);
        var current = Volatile.Read(ref published);
        if (current?.Status.HasIndex != true) return null;
        var comparison = session.LocalRoot == null ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var entry = current.Entries.FirstOrDefault(entry => entry.RelativePath.Equals(relative, comparison));
        return entry == null ? null : (entry, current.Status.Revision);
    }

    public async Task LoadAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref loaded)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, token);
        await maintenance.WaitAsync(linked.Token);
        try
        {
            if (loaded) return;
            linked.Token.ThrowIfCancellationRequested();
            try
            {
                var saved = await store.LoadAsync(linked.Token);
                if (saved != null)
                {
                    serverNodes = saved.Nodes.ToDictionary(node => node.RelativePath, StringComparer.Ordinal);
                    mounts = saved.Mounts; revision = saved.Revision; hasIndex = true;
                    serverCheckedAt = saved.ServerCheckedAt; localCheckedAt = saved.LocalCheckedAt;
                    Publish(saved.Entries);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            { cacheWarning = "缓存不可用，将重新建立索引：" + ex.Message; }
            StartWatcher();
            try { await ReadLocalScopesAsync([new("", true)], linked.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException) { localError = "本地清单核对失败：" + ex.Message; }
            linked.Token.ThrowIfCancellationRequested();
            loaded = true;
            Publish(localKnown || !hasIndex ? Project(serverNodes, mounts) : published!.Entries);
        }
        finally { maintenance.Release(); }
    }

    public async Task<SearchIndexStatus> RefreshAsync(bool rebuild = false, long? knownRevision = null, CancellationToken token = default)
    {
        await LoadAsync(token);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, token);
        await maintenance.WaitAsync(linked.Token);
        try
        {
            // A complete local reconciliation also detects external SVN operations and missed watcher events.
            await ReadLocalScopesAsync([new("", true)], linked.Token);
            var nextMounts = FindMounts();
            var head = knownRevision is { } known && known >= revision ? known : await source.ReadRevisionAsync(session, linked.Token);
            var replace = rebuild || !hasIndex || head < revision || !nextMounts.SequenceEqual(mounts);
            var nodes = replace ? new Dictionary<string, SearchServerNode>(StringComparer.Ordinal)
                : new Dictionary<string, SearchServerNode>(serverNodes, StringComparer.Ordinal);
            foreach (var mount in nextMounts)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (replace) await ReadMountAsync(nodes, mount, nextMounts, head, linked.Token);
                else if (head != revision) await UpdateMountAsync(nodes, mount, nextMounts, head, linked.Token);
            }
            var entries = Project(nodes, nextMounts);
            var checkedAt = DateTimeOffset.Now;
            var document = new SearchIndexDocument(1, "", head, checkedAt, localCheckedAt, nextMounts, nodes.Values.ToArray(), entries);
            await store.SaveAsync(document, linked.Token);
            // Saving atomically commits this snapshot; publish that same snapshot even if cancellation arrives just after the move.
            serverNodes = nodes; mounts = nextMounts; revision = head; serverCheckedAt = checkedAt; hasIndex = true;
            remoteError = cacheWarning = "";
            Publish(entries);
            return Status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            remoteError = "服务器索引更新失败，保留原清单：" + ex.Message;
            Publish(localKnown ? Project(serverNodes, mounts) : published?.Entries ?? []);
            throw;
        }
        finally { maintenance.Release(); }
    }

    public async Task<RepositorySearchResult> SearchAsync(string scope, string query, bool includeIgnored,
        IProgress<RepositorySearchProgress>? progress, CancellationToken token = default, int limit = 5000)
    {
        SearchIndexStore.ValidateRelative(scope, true); query = query.Trim().Replace('\\', '/');
        if (query.Length == 0) throw new ArgumentException("请输入要查找的名称或路径。");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, token);
        if (Volatile.Read(ref published)?.Status.HasIndex != true) await LoadAsync(linked.Token);
        if (!Status.HasIndex || Status.NeedsRebuild) await RefreshAsync(token: linked.Token);
        var current = Volatile.Read(ref published)!;
        var count = 0; var truncated = false; var batch = new List<BrowserEntry>(200);
        foreach (var entry in current.Entries)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!DirectoryRefresh.IsInside(entry.RelativePath, scope) || !includeIgnored && entry.State == FileState.Ignored
                || !entry.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            if (count == limit) { truncated = true; break; }
            batch.Add(entry); count++;
            if (batch.Count == 200) { progress?.Report(new("本地索引", 0, batch.ToArray())); batch.Clear(); }
        }
        if (batch.Count > 0) progress?.Report(new("本地索引", 0, batch.ToArray()));
        linked.Token.ThrowIfCancellationRequested();
        return new(current.Status.Revision, 0, count, truncated)
        {
            FromIndex = true, IndexedCount = current.Status.EntryCount, ServerCheckedAt = current.Status.ServerCheckedAt,
            LocalCheckedAt = current.Status.LocalCheckedAt
        };
    }

    private SearchMount[] FindMounts()
    {
        var found = new List<SearchMount> { new("", session.Url, true) };
        if (session.LocalRoot != null)
            foreach (var info in localSnapshot.LocalInfo.Values.OrderBy(info => info.Path.Length))
            {
                if (info.RepositoryId != session.RepositoryId || info.Path.Equals(session.LocalRoot, StringComparison.OrdinalIgnoreCase)
                    || !RepositoryService.IsDescendant(info.Path, session.LocalRoot)) continue;
                var relative = Path.GetRelativePath(session.LocalRoot, info.Path).Replace('\\', '/');
                if (IsBlocked(relative, includeSelf: true)) continue;
                var parent = SearchIndexStore.OwningMount(found, relative);
                if (new Uri(info.Url).Equals(new Uri(RepositoryService.AppendUrl(parent.Url, SearchIndexStore.RelativeTo(relative, parent.RelativePath))))) continue;
                found.Add(new(relative, info.Url, info.IsDirectory));
            }
        return found.OrderBy(mount => mount.RelativePath.Length).ThenBy(mount => mount.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private async Task ReadMountAsync(Dictionary<string, SearchServerNode> nodes, SearchMount mount, SearchMount[] mapping, long head, CancellationToken token)
    {
        RemoveNodes(nodes, mount.RelativePath, mount, mapping);
        if (mount.RelativePath.Length > 0)
        {
            var info = await source.ReadNodeAsync(mount.Url, head, token);
            if (info == null) return;
            if (mount.IsDirectory) nodes[mount.RelativePath] = new(mount.RelativePath, mount.Url,
                new(mount.RelativePath.Split('/')[^1], true, null, info.ChangedRevision, info.Author, null, null));
        }
        var listing = await source.ReadEntriesAsync(mount.Url, head, mount.IsDirectory, token);
        foreach (var entry in listing)
        {
            var relative = mount.IsDirectory ? RepositoryService.JoinRelative(mount.RelativePath, entry.Name.TrimEnd('/')) : mount.RelativePath;
            AddNode(nodes, relative, entry, mount, mapping);
        }
    }

    private async Task UpdateMountAsync(Dictionary<string, SearchServerNode> nodes, SearchMount mount, SearchMount[] mapping, long head, CancellationToken token)
    {
        IReadOnlyList<SearchPathChange> changes;
        try { changes = await source.ReadChangesAsync(mount.Url, revision, head, token); }
        catch (SvnException ex) when (SvnSearchIndexSource.IsMissingPath(ex)) { await ReadMountAsync(nodes, mount, mapping, head, token); return; }
        if (changes.Count > 128) { await ReadMountAsync(nodes, mount, mapping, head, token); return; }
        var relevant = changes.Select(change => change with { RelativePath = change.RelativePath.Length == 0 ? mount.RelativePath : RepositoryService.JoinRelative(mount.RelativePath, change.RelativePath) })
            .Where(change => SearchIndexStore.OwningMount(mapping, change.RelativePath) == mount).ToArray();
        foreach (var deleted in relevant.Where(change => change.Action == "deleted")) RemoveNodes(nodes, deleted.RelativePath, mount, mapping);
        var reads = relevant.Where(change => change.Action != "deleted").ToArray();
        reads = reads.Where(change => !reads.Any(parent => parent != change && parent.IsDirectory
            && DirectoryRefresh.IsInside(change.RelativePath, parent.RelativePath))).DistinctBy(change => change.RelativePath).ToArray();
        foreach (var change in reads)
        {
            token.ThrowIfCancellationRequested();
            var url = RepositoryService.AppendUrl(mount.Url, SearchIndexStore.RelativeTo(change.RelativePath, mount.RelativePath));
            RemoveNodes(nodes, change.RelativePath, mount, mapping);
            if (change.IsDirectory)
            {
                var info = await source.ReadNodeAsync(url, head, token);
                if (info == null) continue;
                if (change.RelativePath.Length > 0) AddNode(nodes, change.RelativePath,
                    new(change.RelativePath.Split('/')[^1], true, null, info.ChangedRevision, info.Author, null, null), mount, mapping);
            }
            foreach (var entry in await source.ReadEntriesAsync(url, head, change.IsDirectory, token))
                AddNode(nodes, change.IsDirectory ? RepositoryService.JoinRelative(change.RelativePath, entry.Name.TrimEnd('/')) : change.RelativePath, entry, mount, mapping);
        }
    }

    private static void AddNode(Dictionary<string, SearchServerNode> nodes, string relative, ServerEntry entry, SearchMount mount, SearchMount[] mapping)
    {
        SearchIndexStore.ValidateRelative(relative);
        if (SearchIndexStore.OwningMount(mapping, relative) != mount || OfficeTemporaryFiles.IsLockFile(relative, entry.IsDirectory)
            || relative.Split('/').Any(part => part.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase))) return;
        nodes[relative] = new(relative, RepositoryService.AppendUrl(mount.Url, SearchIndexStore.RelativeTo(relative, mount.RelativePath)),
            entry with { Name = relative.Split('/')[^1] });
    }

    private static void RemoveNodes(Dictionary<string, SearchServerNode> nodes, string relative, SearchMount mount, SearchMount[] mapping)
    {
        foreach (var path in nodes.Keys.Where(path => (path == relative || DirectoryRefresh.IsInside(path, relative))
            && SearchIndexStore.OwningMount(mapping, path) == mount).ToArray()) nodes.Remove(path);
    }

    private void Publish(BrowserEntry[] entries)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var error = string.Join("\n", new[] { remoteError, localError, cacheWarning }.Where(message => message.Length > 0));
        var status = new SearchIndexStatus(hasIndex, revision, entries.Length, serverCheckedAt, localCheckedAt, error,
            localKnown && hasIndex && !FindMounts().SequenceEqual(mounts));
        Volatile.Write(ref published, new(status, entries));
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        watcher?.Dispose(); localTimer.Dispose(); lifetime.Cancel();
        disposal = FinishDisposalAsync();
    }

    private async Task FinishDisposalAsync()
    {
        await maintenance.WaitAsync();
        try { source.Dispose(); lifetime.Dispose(); } finally { maintenance.Release(); }
    }

    public async ValueTask DisposeAsync() { Dispose(); if (disposal != null) await disposal; }
}
