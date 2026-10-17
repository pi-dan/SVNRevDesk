using System.IO;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class BrowsingTests
{
    public static async Task RunAsync(string output, Action<Window, string> render)
    {
        var fixture = Path.Combine(output, "browsing-fixture");
        var repository = new ControlledRepository(fixture);
        var window = new MainWindow(new UserSettings(Path.Combine(fixture, "settings")), repository);
        using var model = (MainViewModel)window.DataContext;
        await model.RunAsync("Open browsing fixture", token => model.OpenAsync(fixture, true, token));
        await model.BrowseDirectoryAsync("alpha");
        await model.BrowseDirectoryAsync("");
        await model.BrowseDirectoryAsync("beta");
        var calls = repository.ReadCount;
        await model.BrowseDirectoryAsync("alpha");
        await model.BrowseDirectoryAsync("");
        Check(repository.ReadCount == calls && !model.IsReading, "Repeated navigation uses the cache without a server call");

        // Intentionally deliver an old result even after cancellation to verify the publication guard.
        var slow = new ReadPause(ignoreCancellation: true);
        repository.DirectoryPauses["gamma"] = slow;
        var oldNavigation = model.BrowseDirectoryAsync("gamma");
        await slow.Started;
        Check(model.CurrentRelative == "gamma" && model.Entries.Count == 0 && model.IsReading && !model.IsBusy,
            "An uncached directory changes path immediately and clears unrelated rows");
        var cachedSwitch = model.BrowseDirectoryAsync("alpha");
        Check(cachedSwitch.IsCompletedSuccessfully && model.CurrentRelative == "alpha" && model.Entries.Single().Name == "alpha.txt",
            "A cached switch completes while an old server request is still blocked");
        slow.Release();
        await oldNavigation;
        Check(model.CurrentRelative == "alpha" && model.Entries.Single().Name == "alpha.txt" && model.FindNode("alpha")!.IsSelected,
            "An obsolete response cannot replace the current directory or tree selection");

        slow = new ReadPause(ignoreCancellation: true);
        repository.DirectoryPauses["gamma"] = slow;
        oldNavigation = model.BrowseDirectoryAsync("gamma");
        await slow.Started;
        await model.BrowseDirectoryAsync("delta");
        slow.Release();
        await oldNavigation;
        Check(model.CurrentRelative == "delta" && model.Entries.Single().Name == "delta.txt",
            "The latest uncached navigation wins over a late result");

        var refreshPause = new ReadPause();
        repository.RefreshPause = refreshPause;
        var refresh = model.RefreshAllInBackgroundAsync();
        await refreshPause.Started;
        await model.BrowseDirectoryAsync("alpha");
        model.ViewMode = MainViewModel.CardViewName;
        var cards = (ListBox)window.FindName("FileCards");
        cards.SelectedItems.Add(model.Entries.Single());
        model.FilterText = "alpha";
        Check(model.IsReading && model.CanOperate && ((TreeView)window.FindName("DirectoryTree")).IsEnabled
            && cards.IsEnabled && model.EntriesView.Cast<object>().Count() == 1,
            "Background refresh keeps navigation, selection, filters, and explicit operations enabled");
        render(window, Path.Combine(output, "cached-browsing.png"));
        refreshPause.Release();
        await refresh;
        Check(model.CurrentRelative == "alpha" && model.Entries.Single().ServerRevision == model.Snapshot!.Revision
            && cards.SelectedItems.Cast<BrowserEntry>().Single().RelativePath == "alpha/alpha.txt",
            "Refresh publishes the new cache into the current directory and preserves selection");
        calls = repository.ReadCount;
        await model.BrowseDirectoryAsync("beta");
        await model.BrowseDirectoryAsync("alpha");
        Check(repository.ReadCount == calls, "Refreshed visited directories remain available without another server call");
        model.FilterText = "";

        var previousSnapshot = model.Snapshot;
        var previousEntry = model.Entries.Single();
        repository.FailDirectory = "beta";
        await model.RefreshAllInBackgroundAsync();
        Check(ReferenceEquals(model.Snapshot, previousSnapshot) && ReferenceEquals(model.Entries.Single(), previousEntry)
            && model.StatusMessage.StartsWith("后台读取失败", StringComparison.Ordinal),
            "A failed refresh preserves the complete previous snapshot and directory cache");
        repository.FailDirectory = null;

        refreshPause = new ReadPause(ignoreCancellation: true);
        repository.RefreshPause = refreshPause;
        refresh = model.RefreshAllInBackgroundAsync();
        await refreshPause.Started;
        await model.BrowseDirectoryAsync("gamma");
        var navigatedSnapshot = model.Snapshot;
        refreshPause.Release();
        await refresh;
        Check(model.CurrentRelative == "gamma" && ReferenceEquals(model.Snapshot, navigatedSnapshot),
            "A cancelled refresh cannot publish a late snapshot over a new navigation");

        refreshPause = new ReadPause();
        repository.RefreshPause = refreshPause;
        refresh = model.RefreshAllInBackgroundAsync();
        await refreshPause.Started;
        var operationRan = false;
        await model.RunAsync("Explicit operation has priority", _ =>
        {
            operationRan = true;
            Check(!model.IsReading && model.IsBusy && !model.CanOperate,
                "An explicit SVN operation cancels and drains reads before running exclusively");
            return Task.CompletedTask;
        });
        await refresh;
        Check(operationRan && !model.IsBusy, "Slow background refresh does not hold an explicit operation indefinitely");

        refreshPause = new ReadPause();
        repository.RefreshPause = refreshPause;
        refresh = model.RefreshAllInBackgroundAsync();
        await refreshPause.Started;
        model.Cancel();
        await refresh;
        Check(!model.IsReading && model.Entries.Single().Name == "gamma.txt", "Manual cancellation retains the browsed cache");

        await model.BrowseDirectoryAsync("alpha");
        repository.MissingDirectories.Add("alpha");
        await model.RefreshAllInBackgroundAsync();
        Check(model.CurrentRelative == "" && model.Roots.Single().IsSelected && model.FindNode("alpha") == null,
            "A refreshed deletion returns to the existing parent and removes the stale tree node");

        calls = repository.ReadCount;
        await model.RunAsync("Open another repository", token => model.OpenAsync("https://example.invalid/svn/other", false, token));
        Check(repository.ReadCount > calls && model.Session!.LocalRoot == null && model.CurrentRelative == "",
            "Opening another repository creates a separate directory cache");

        var rootPause = new ReadPause();
        repository.DirectoryPauses[""] = rootPause;
        refresh = model.RefreshAllInBackgroundAsync();
        await rootPause.Started;
        var beta = model.FindNode("beta")!;
        beta.IsExpanded = true;
        await model.ExpandDirectoryAsync(beta);
        rootPause.Release();
        await refresh;
        calls = repository.ReadCount;
        await model.BrowseDirectoryAsync("beta");
        Check(repository.ReadCount == calls && model.Entries.Single().ServerRevision == model.Snapshot!.Revision,
            "A branch expanded during refresh is included in the new cache without another query");
        await VerifyScopedRefreshAsync(window, repository, fixture);
        Console.WriteLine("PASS: cached navigation, slow/obsolete responses, background refresh, selection, failed/cancelled reads, operation priority, deletion, and repository isolation.");
    }

    private static async Task VerifyScopedRefreshAsync(MainWindow window, ControlledRepository repository, string fixture)
    {
        var model = (MainViewModel)window.DataContext;
        await model.RunAsync("Open scoped refresh fixture", token => model.OpenAsync(fixture, true, token));
        await model.BrowseDirectoryAsync("beta");
        var betaEntry = model.Entries.Single();
        var betaCache = model.DirectoryCacheText;
        await model.BrowseDirectoryAsync("gamma");
        repository.ScopeRequests.Clear();
        repository.ReadPaths.Clear();
        var fullChecks = repository.FullRefreshCount;
        var lastFull = model.LastFullRefreshAt;
        await model.RefreshInBackgroundAsync();
        Check(repository.FullRefreshCount == fullChecks && repository.ScopeRequests.SequenceEqual([new DirectoryRefresh("gamma")])
            && repository.ReadPaths.SequenceEqual(["gamma"]) && model.LastFullRefreshAt == lastFull,
            "Manual refresh reads only the current directory and does not postpone the full automatic check");
        var reads = repository.ReadCount;
        await model.BrowseDirectoryAsync("beta");
        Check(repository.ReadCount == reads && ReferenceEquals(model.Entries.Single(), betaEntry) && model.DirectoryCacheText == betaCache,
            "Refreshing one directory preserves the version, timestamp and cached entries of other directories");

        repository.ScopeRequests.Clear();
        repository.ReadPaths.Clear();
        await model.RunAsync("File operation refresh", token => model.RefreshPathsAsync([model.Entries.Single().LocalPath!], token));
        Check(repository.ScopeRequests.SequenceEqual([new DirectoryRefresh("beta")]) && repository.ReadPaths.SequenceEqual(["beta"]),
            "A file operation refreshes its containing directory without checking another branch");

        repository.ScopeRequests.Clear();
        repository.ReadPaths.Clear();
        await model.RunAsync("Directory operation refresh", token => model.RefreshPathsAsync([Path.Combine(model.Session!.LocalRoot!, "gamma")], token));
        Check(repository.ScopeRequests.Contains(new DirectoryRefresh("gamma", true))
            && repository.ReadPaths.All(path => path is "" or "gamma") && model.LastFullRefreshAt == lastFull,
            "A directory operation refreshes its subtree and containing row while leaving unrelated directory contents cached");

        var previous = model.Entries.Single();
        repository.FailDirectory = "beta";
        await model.RefreshInBackgroundAsync();
        Check(ReferenceEquals(model.Entries.Single(), previous) && model.StatusMessage.StartsWith("后台读取失败", StringComparison.Ordinal),
            "A failed scoped refresh retains the previously displayed entries");
        repository.FailDirectory = null;
        await model.BrowseDirectoryAsync("gamma");
        repository.MissingDirectories.Add("gamma");
        await model.RefreshInBackgroundAsync();
        Check(model.CurrentRelative == "" && model.FindNode("gamma") == null,
            "Deleting the current directory during a scoped refresh falls back to its existing parent");
        Console.WriteLine("PASS: manual/file/directory refresh scopes, independent cache versions, automatic scheduling, scoped failure and deletion.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    internal sealed class ReadPause(bool ignoreCancellation = false)
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Release() => released.TrySetResult();
        public async Task WaitAsync(CancellationToken token)
        {
            started.TrySetResult();
            if (ignoreCancellation) await released.Task;
            else await released.Task.WaitAsync(token);
        }
    }

    // Controlled waits simulate a slow network without relying on a business server or timing races.
    internal sealed class ControlledRepository(string localRoot) : IRepositoryService
    {
        private long revision;
        public int ReadCount { get; private set; }
        public int FullRefreshCount { get; private set; }
        public List<DirectoryRefresh> ScopeRequests { get; } = [];
        public List<string> ReadPaths { get; } = [];
        public Dictionary<string, ReadPause> DirectoryPauses { get; } = new(StringComparer.Ordinal);
        public ReadPause? RefreshPause { get; set; }
        public ReadPause? OpenPause { get; set; }
        public string RepositoryId { get; set; } = "test";
        public string? OnlineRepositoryId { get; set; }
        public string? FailDirectory { get; set; }
        public HashSet<string> MissingDirectories { get; } = new(StringComparer.Ordinal);

        public Task<RepositorySession> OpenWorkingCopyAsync(string directory, CancellationToken token)
        {
            Directory.CreateDirectory(directory);
            return Task.FromResult(new RepositorySession("https://example.invalid/svn/test", "https://example.invalid/svn/test", RepositoryId, localRoot));
        }

        public async Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token)
        {
            var pause = OpenPause;
            OpenPause = null;
            if (pause != null) await pause.WaitAsync(token);
            return new RepositorySession(url, url, OnlineRepositoryId ?? url, null);
        }

        public async Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token)
        {
            FullRefreshCount++;
            return await CreateSnapshotAsync(token);
        }

        private async Task<RepositorySnapshot> CreateSnapshotAsync(CancellationToken token)
        {
            var pause = RefreshPause;
            RefreshPause = null;
            if (pause != null) await pause.WaitAsync(token);
            return new(Interlocked.Increment(ref revision), DateTimeOffset.Now, new Dictionary<string, WorkingStatus>(), new Dictionary<string, SvnInfo>());
        }

        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
        {
            ScopeRequests.Add(scope);
            return CreateSnapshotAsync(token);
        }

        public async Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session,
            RepositorySnapshot snapshot, string relativePath, CancellationToken token)
        {
            ReadCount++;
            ReadPaths.Add(relativePath);
            if (DirectoryPauses.Remove(relativePath, out var pause)) await pause.WaitAsync(token);
            if (FailDirectory == relativePath) throw new IOException("Controlled network failure");
            if (MissingDirectories.Contains(relativePath)) throw new SvnException("list", 1, "E160013: directory removed");
            var names = relativePath.Length == 0
                ? new[] { "alpha", "beta", "gamma", "delta" }.Where(name => !MissingDirectories.Contains(name))
                : [relativePath + ".txt"];
            return names.Select(name => CreateEntry(session, snapshot, relativePath, name)).ToList();
        }

        private static BrowserEntry CreateEntry(RepositorySession session, RepositorySnapshot snapshot, string parent, string name)
        {
            var relative = parent.Length == 0 ? name : parent + "/" + name;
            var local = session.LocalRoot == null ? null : Path.Combine(session.LocalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (local != null)
            {
                if (parent.Length == 0) Directory.CreateDirectory(local);
                else File.WriteAllText(local, "Controlled browser fixture");
            }
            return new(name, relative, session.Url + "/" + relative, local, parent.Length == 0,
                session.LocalRoot == null ? FileState.Unchecked : FileState.Equal, snapshot.Revision, snapshot.Revision, snapshot.Revision,
                parent.Length == 0 ? null : 1048576, "tester", DateTimeOffset.Now, null, "", "已检查项无差异");
        }
    }
}
