using System.Diagnostics;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class SearchIndexTests
{
    private sealed class Inline<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class CountingSource(ISearchIndexSource inner) : ISearchIndexSource
    {
        public int RemoteCalls, Lists, LocalCalls;
        public int LocalFailures;
        public bool Offline;
        private void Remote() { RemoteCalls++; if (Offline) throw new IOException("offline fixture"); }
        public Task<long> ReadRevisionAsync(RepositorySession session, CancellationToken token) { Remote(); return inner.ReadRevisionAsync(session, token); }
        public Task<IReadOnlyList<ServerEntry>> ReadEntriesAsync(string url, long revision, bool recursive, CancellationToken token)
        { Remote(); Lists++; return inner.ReadEntriesAsync(url, revision, recursive, token); }
        public Task<SvnInfo?> ReadNodeAsync(string url, long revision, CancellationToken token) { Remote(); return inner.ReadNodeAsync(url, revision, token); }
        public Task<IReadOnlyList<SearchPathChange>> ReadChangesAsync(string url, long from, long to, CancellationToken token)
        { Remote(); return inner.ReadChangesAsync(url, from, to, token); }
        public Task<RepositorySnapshot> ReadLocalAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
        {
            LocalCalls++;
            if (LocalFailures > 0) { LocalFailures--; throw new IOException("local scan fixture failure"); }
            return inner.ReadLocalAsync(session, scope, token);
        }
        public void Dispose() => inner.Dispose();
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var repo = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("create"); start.ArgumentList.Add(repo);
        using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException("svnadmin failed"); }
        var url = new Uri(repo + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var wc = Path.Combine(root, "working-copy"); var peer = Path.Combine(root, "peer");
        using var runner = new SvnRunner(); var token = CancellationToken.None;
        var ops = new SvnOperations(runner, Path.Combine(root, "Temp"), Path.Combine(root, "Backups"));
        var service = new RepositoryService(runner);
        await ops.CheckoutAsync(url, wc, false, token);
        Directory.CreateDirectory(Path.Combine(wc, "docs", "deep")); Directory.CreateDirectory(Path.Combine(wc, "other"));
        File.WriteAllText(Path.Combine(wc, "docs", "deep", "report@one.txt"), "one");
        File.WriteAllText(Path.Combine(wc, "other", "report.txt"), "two");
        await ops.AddAsync([Path.Combine(wc, "docs"), Path.Combine(wc, "other")], token);
        await ops.CommitAsync([wc], "index fixture", token); await ops.CheckoutAsync(url, peer, false, token);
        var ignores = new LocalIgnoreStore(Path.Combine(root, "ignores.json")); var cache = Path.Combine(root, "Cache");
        var online = await service.OpenUrlAsync(url, token);
        var source = new CountingSource(new SvnSearchIndexSource(runner));
        await using var index = new RepositorySearchIndex(online, source, cache, ignores);
        await index.RefreshAsync();
        check(source.Lists == 1 && File.Exists(index.CachePath), "Initial index uses one recursive listing and persists it locally");
        var before = (source.RemoteCalls, source.LocalCalls);
        for (var i = 0; i < 15; i++)
        {
            var found = await Search(index, "", "REPORT");
            if (found.Entries.Length != 2 || !found.Summary.FromIndex) throw new InvalidOperationException("Incorrect repeated indexed query");
        }
        check(before == (source.RemoteCalls, source.LocalCalls), "Searching an existing index issues no SVN or local maintenance calls");
        check((await Search(index, "docs", @"deep\report@")).Entries.Single().RelativePath == "docs/deep/report@one.txt", "Indexed search honors scope, Windows separators and literal @");
        check((await Search(index, "", "report", limit: 1)).Summary.LimitReached, "Indexed search detects additional matches beyond the result limit");
        var lists = source.Lists; await index.RefreshAsync();
        check(source.Lists == lists, "Unchanged HEAD checks do not relist repository directories");
        var saved = File.ReadAllBytes(index.CachePath); var stamp = index.Status.ServerCheckedAt;
        source.Offline = true;
        try { await index.RefreshAsync(); throw new InvalidOperationException("Expected offline failure"); } catch (IOException) { }
        check(index.Status.ServerCheckedAt == stamp && index.Status.Error.Contains("offline") && saved.SequenceEqual(File.ReadAllBytes(index.CachePath)), "Failed server checks retain the verified revision, timestamp and disk cache");
        check((await Search(index, "", "report")).Entries.Length == 2, "Offline server checks preserve searchable cached results");
        var offlineSource = new CountingSource(new SvnSearchIndexSource(runner)) { Offline = true };
        await using (var reopened = new RepositorySearchIndex(online, offlineSource, cache, ignores))
        {
            await reopened.LoadAsync();
            check((await Search(reopened, "", "report")).Entries.Length == 2 && offlineSource.RemoteCalls == 0, "A restarted offline session searches the persisted index without remote calls");
        }
        source.Offline = false;
        File.WriteAllText(Path.Combine(peer, "other", "new-report.txt"), "new");
        await ops.AddAsync([Path.Combine(peer, "other", "new-report.txt")], token);
        await ops.DeleteAsync([Path.Combine(peer, "other", "report.txt")], token);
        File.AppendAllText(Path.Combine(peer, "docs", "deep", "report@one.txt"), "changed");
        await ops.CommitAsync([peer], "remote delta", token);
        await index.RefreshAsync();
        var delta = await Search(index, "", "report");
        check(delta.Entries.Length == 2 && delta.Entries.Any(e => e.Name == "new-report.txt") && !delta.Entries.Any(e => e.Name == "report.txt"), "Incremental remote updates apply additions, deletions and content changes");
        await ops.CopyBranchAsync(url + "/docs", url + "/branch", "copy directory", token);
        await index.RefreshAsync();
        check((await Search(index, "branch", "report")).Entries.Length == 1, "Remote copied directories include descendants in the index");
        var oldRevision = index.Status.Revision; using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); try { await index.RefreshAsync(true, token: cancel.Token); } catch (OperationCanceledException) { }
        }
        check(index.Status.Revision == oldRevision && !Directory.EnumerateFiles(Path.GetDirectoryName(index.CachePath)!, "*.tmp").Any(), "Cancelled rebuild preserves the published index and leaves no temporary cache files");
        var session = await service.OpenWorkingCopyAsync(wc, token);
        var localSource = new CountingSource(new SvnSearchIndexSource(runner));
        await using var local = new RepositorySearchIndex(session, localSource, cache, ignores);
        await local.RefreshAsync(); var remoteCalls = localSource.RemoteCalls;
        check(localSource.LocalCalls == 1, "Initial refresh reconciles the complete working copy exactly once");
        var restartAdded = Path.Combine(wc, "restart-added.txt");
        File.WriteAllText(restartAdded, "created while the reopened index was closed");
        var reopenedLocalSource = new CountingSource(new SvnSearchIndexSource(runner));
        await using (var reopenedLocal = new RepositorySearchIndex(session, reopenedLocalSource, cache, ignores))
        {
            await reopenedLocal.RefreshAsync();
            check(reopenedLocalSource.LocalCalls == 1 && reopenedLocalSource.Lists == 0
                && (await Search(reopenedLocal, "docs", "report")).Entries.Length == 1
                && reopenedLocal.FindEntry("restart-added.txt")?.Entry.LocalPath == restartAdded,
                "Restarting with a saved index reconciles local files once and reuses the server listing");
            await reopenedLocal.RefreshAsync();
            check(reopenedLocalSource.LocalCalls == 2,
                "A later explicit refresh still performs a fresh local reconciliation");
        }
        var retrySource = new CountingSource(new SvnSearchIndexSource(runner)) { LocalFailures = 1 };
        await using (var retryIndex = new RepositorySearchIndex(session, retrySource, cache, ignores))
        {
            await retryIndex.RefreshAsync();
            check(retrySource.LocalCalls == 2 && retryIndex.Status.Error.Length == 0
                && retryIndex.FindEntry("restart-added.txt")?.Entry.LocalPath == restartAdded,
                "A failed initial scan is retried instead of reusing an incomplete local snapshot");
        }
        var loadSource = new CountingSource(new SvnSearchIndexSource(runner));
        await using (var loadedIndex = new RepositorySearchIndex(session, loadSource, cache, ignores))
        {
            await loadedIndex.LoadAsync();
            check(loadSource.LocalCalls == 1 && loadedIndex.FindEntry("restart-added.txt") != null,
                "Standalone cache loading still reconciles local changes for offline searches");
            await loadedIndex.RefreshAsync();
            check(loadSource.LocalCalls == 2, "An explicit refresh after standalone loading does not skip reconciliation");
        }
        File.Delete(restartAdded);
        var added = Path.Combine(wc, "local-report.txt"); File.WriteAllText(added, "uncommitted");
        await Until(async () => (await Search(local, "", "local-report")).Entries.Length == 1);
        check(localSource.RemoteCalls == remoteCalls, "Filesystem watcher indexes new uncommitted files without checking the server");
        var renamed = Path.Combine(wc, "renamed-report.txt"); File.Move(added, renamed);
        await Until(async () => (await Search(local, "", "renamed-report")).Entries.Length == 1 && (await Search(local, "", "local-report")).Entries.Length == 0);
        check(true, "Filesystem watcher applies local renames");
        File.Delete(renamed); await Until(async () => (await Search(local, "", "renamed-report")).Entries.Length == 0);
        check(true, "Filesystem watcher removes deleted unversioned files");
        File.Delete(Path.Combine(wc, "docs", "deep", "report@one.txt")); await local.RefreshLocalAsync();
        check((await Search(local, "docs", "report")).Entries.Single().State == FileState.Missing, "Local deletion retains the searchable server node and its missing state");
        var ignored = Path.Combine(wc, "ignored"); Directory.CreateDirectory(ignored); File.WriteAllText(Path.Combine(ignored, "secret-report.txt"), "ignored");
        ignores.Set(ignored, true); File.WriteAllText(Path.Combine(wc, "~$report.docx"), "lock");
        await local.RefreshLocalAsync();
        check(!(await Search(local, "", "secret-report")).Entries.Any() && (await Search(local, "", "secret-report", true)).Entries.Length == 1
            && !(await Search(local, "", "~$report", true)).Entries.Any(), "Index observes local ignores and excludes Office temporary files");
        await ops.UpdateAsync(wc, true, token); await ops.SwitchAsync(url + "/branch", Path.Combine(wc, "docs"), token);
        await local.RefreshAsync();
        check((await Search(local, "docs", "report")).Entries.Single().Url.Contains("/branch/"), "Index rebuilds switched directory mappings");
        await runner.RunAsync(["propset", "index:test", "value", SvnRunner.Target(Path.Combine(wc, "docs"))]);
        await ops.CommitAsync([Path.Combine(wc, "docs")], "switched root property", token);
        await local.RefreshAsync();
        check((await Search(local, "docs", "report")).Entries.Single().Url.Contains("/branch/"), "Property-only changes at a switched mount root retain valid indexed descendants");
        await runner.RunAsync(["switch", "--ignore-ancestry", SvnRunner.Target(url + "/other/new-report.txt"),
            Path.Combine(wc, "branch", "deep", "report@one.txt")]);
        await local.RefreshAsync();
        check((await Search(local, "branch", "report@one")).Entries.Single().Url.EndsWith("/other/new-report.txt"), "Index honors switched file mappings as well as switched directories");
        var stableLocalCalls = localSource.LocalCalls; await Task.Delay(1400);
        var settledLocalCalls = localSource.LocalCalls; await Task.Delay(1400);
        check(localSource.LocalCalls == settledLocalCalls && settledLocalCalls >= stableLocalCalls, "Local SVN status reads settle without a self-triggering watcher loop");
        var nested = Path.Combine(wc, "nested"); await ops.CheckoutAsync(url + "/other", nested, false, token); await local.RefreshLocalAsync();
        check(!(await Search(local, "", "nested/new-report")).Entries.Any(), "Index excludes descendants of nested working copies");
        var sparse = Path.Combine(root, "sparse"); await ops.CheckoutAsync(url, sparse, true, token);
        await using (var sparseIndex = new RepositorySearchIndex(await service.OpenWorkingCopyAsync(sparse, token), new SvnSearchIndexSource(runner), cache, ignores))
        {
            await sparseIndex.RefreshAsync();
            check((await Search(sparseIndex, "docs", "report")).Entries.Length == 1 && !Directory.Exists(Path.Combine(sparse, "docs")), "Sparse indexes include undownloaded files without changing working copy depth");
            check(sparseIndex.CachePath != local.CachePath && local.CachePath != index.CachePath, "Online and separate working copy sessions have isolated disk indexes");
        }
        File.WriteAllText(index.CachePath, "{\"Version\":1,\"Mounts\":[null]}");
        await using (var corrupt = new RepositorySearchIndex(online, new SvnSearchIndexSource(runner), cache, ignores))
        {
            await corrupt.LoadAsync(); check(!corrupt.Status.HasIndex, "Corrupt caches are rejected without crashing");
            await corrupt.RefreshAsync(); check((await Search(corrupt, "", "report")).Entries.Length == 3, "A corrupt cache can be rebuilt from the server");
        }
    }

    private static async Task<(RepositorySearchResult Summary, BrowserEntry[] Entries)> Search(RepositorySearchIndex index, string scope, string query, bool ignored = false, int limit = 5000)
    {
        var entries = new List<BrowserEntry>();
        var summary = await index.SearchAsync(scope, query, ignored, new Inline<RepositorySearchProgress>(p => entries.AddRange(p.Matches)), limit: limit);
        return (summary, entries.ToArray());
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Index watcher did not converge"); await Task.Delay(100); }
    }
}
