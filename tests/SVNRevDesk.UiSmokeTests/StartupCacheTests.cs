using System.IO;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using static SVNRevDesk.UiSmokeTests.BrowsingTests;

namespace SVNRevDesk.UiSmokeTests;

internal static class StartupCacheTests
{
    public static async Task RunAsync(string output, string workingCopy, Action<Window, string> render)
    {
        var fixture = Path.Combine(output, "startup-working-copy");
        var storage = Path.Combine(output, "startup-settings");
        Directory.CreateDirectory(fixture);
        var repository = new ControlledRepository(fixture);
        string savedCacheText;
        long? savedRevision;
        using (var seed = new MainViewModel(new UserSettings(storage), repository))
        {
            await seed.OpenAsync(fixture, true, CancellationToken.None);
            await seed.BrowseDirectoryAsync("beta");
            await seed.BrowseDirectoryAsync("alpha");
            await seed.RefreshInBackgroundAsync();
            seed.ViewMode = MainViewModel.CardViewName;
            savedCacheText = seed.DirectoryCacheText;
            savedRevision = seed.Entries.Single().ServerRevision;
            seed.SaveSession();
        }
        var cachePath = Path.Combine(storage, "Cache", "browsing-session.json");
        var originalCache = File.ReadAllText(cachePath);
        Check(!originalCache.Contains("ExistsLocally", StringComparison.Ordinal)
            && !originalCache.Contains("StateText", StringComparison.Ordinal), "Disk cache contains data rather than computed UI properties");
        var saved = new BrowsingCacheStore(Path.Combine(storage, "Cache")).Load(fixture, true)!;
        Check(saved.Directories["alpha"].Revision != saved.Directories["beta"].Revision,
            "Persistence preserves each directory's independent checked revision");

        repository = new ControlledRepository(fixture);
        var pause = new ReadPause();
        repository.RefreshPause = pause;
        var window = new MainWindow(UserSettings.Load(storage), repository);
        using var model = (MainViewModel)window.DataContext;
        var startup = model.RestoreInBackgroundAsync();
        await pause.Started;
        Check(!model.IsBusy && model.IsReading && model.CanOperate && model.CurrentRelative == "alpha"
            && model.IsCardView && model.FindNode("alpha")?.IsSelected == true
            && model.Entries.Single().ServerRevision == savedRevision && model.DirectoryCacheText == savedCacheText,
            "Startup restores saved rows, versions, view and tree selection before the full server check finishes");
        Check(repository.ReadCount == 0, "Displaying persisted cache requires no server directory reads");
        await model.BrowseDirectoryAsync("beta");
        await model.BrowseDirectoryAsync("alpha");
        model.FilterText = "alpha";
        // A hidden test window needs layout before its bound rows can receive a simulated click.
        render(window, Path.Combine(output, "startup-cache-before-selection.png"));
        var cards = (ListBox)window.FindName("FileCards");
        cards.SelectedItems.Add(model.Entries.Single());
        Check(repository.ReadCount == 0 && model.IsReading && model.CanExecute("open")
            && ClientActions.CanOpenLocalFileDirectly(model.Entries.Single())
            && !ClientActions.CanOpenLocalFileDirectly(model.Entries.Single() with { State = FileState.BothChanged }),
            $"Cached browsing and local opening remain available without stopping the startup server check "
            + $"(reads={repository.ReadCount}, reading={model.IsReading}, open={model.CanExecute("open")}, "
            + $"cardSelection={cards.SelectedItems.Count}, modelSelection={model.SelectionText}, "
            + $"local={ClientActions.CanOpenLocalFileDirectly(model.Entries.Single())})");
        render(window, Path.Combine(output, "startup-cache-browsing.png"));
        Check(((TreeView)window.FindName("DirectoryTree")).IsEnabled && cards.IsEnabled
            && model.EntriesView.Cast<object>().Count() == 1, "Actual WPF tree, cards and filtering stay enabled during startup");
        pause.Release();
        await startup;
        Check(!model.IsReading && model.CurrentRelative == "alpha" && cards.SelectedItems.Count == 1,
            "Successful startup refresh preserves the current navigation and file selection");
        model.FilterText = "";

        // A failed initialization must retain the already restored view.
        pause = new ReadPause();
        repository.RefreshPause = pause;
        repository.FailDirectory = "beta";
        startup = model.RestoreInBackgroundAsync();
        await pause.Started;
        var previousSnapshot = model.Snapshot;
        var previousEntry = model.Entries.Single();
        pause.Release();
        await startup;
        Check(ReferenceEquals(previousSnapshot, model.Snapshot) && ReferenceEquals(previousEntry, model.Entries.Single())
            && model.StatusMessage.StartsWith("后台读取失败", StringComparison.Ordinal), "Network failure leaves startup cache usable");
        repository.FailDirectory = null;

        // Writes take priority over cancellable initialization, as they do over ordinary refreshes.
        pause = new ReadPause();
        repository.RefreshPause = pause;
        startup = model.RestoreInBackgroundAsync();
        await pause.Started;
        await model.RunAsync("Explicit operation during startup", _ =>
        {
            Check(!model.IsReading && model.IsBusy && model.Entries.Single().ExistsLocally,
                "An explicit operation drains startup reads while retaining its cached target");
            return Task.CompletedTask;
        });
        await startup;

        // Even a service that completes after cancellation cannot replace a newly opened session.
        pause = new ReadPause(ignoreCancellation: true);
        repository.RefreshPause = pause;
        startup = model.RestoreInBackgroundAsync();
        await pause.Started;
        await model.OpenInBackgroundAsync("https://example.invalid/svn/another", false);
        var otherSnapshot = model.Snapshot;
        pause.Release();
        await startup;
        Check(model.Session!.Url.EndsWith("/another", StringComparison.Ordinal)
            && ReferenceEquals(otherSnapshot, model.Snapshot), "Late startup results cannot overwrite another repository");

        await VerifyFallbackAsync(storage, fixture, originalCache);
        await VerifyOnlineCacheAsync(output, fixture);
        await VerifyRealSnapshotAsync(output, workingCopy);
        await VerifyHiddenOfficeLocksAsync(output, render);
        Console.WriteLine("PASS: persistent startup cache, slow/offline server, enabled WPF navigation/selection/filtering/local opens, cancellation, late responses, corrupt cache and session isolation.");
    }

    private static async Task VerifyFallbackAsync(string storage, string fixture, string originalCache)
    {
        var cachePath = Path.Combine(storage, "Cache", "browsing-session.json");
        foreach (var reason in new[] { "corrupt", "replaced-working-copy", "different-location" })
        {
            File.WriteAllText(cachePath, reason == "corrupt" ? "{invalid json" : originalCache);
            var repository = new ControlledRepository(fixture);
            if (reason == "replaced-working-copy") repository.RepositoryId = "replacement";
            var pause = new ReadPause();
            repository.RefreshPause = pause;
            var settings = UserSettings.Load(storage);
            settings.LastLocation = fixture;
            settings.LastLocationIsLocal = true;
            settings.LastRelativeDirectory = "alpha";
            using var model = new MainViewModel(settings, repository);
            var startup = reason == "different-location"
                ? model.OpenInBackgroundAsync("https://example.invalid/svn/different", false)
                : model.RestoreInBackgroundAsync();
            await pause.Started;
            Check(model.Session == null && model.CanOperate && model.IsReading,
                $"{reason}: unusable cache falls back to a nonexclusive server initialization");
            pause.Release();
            await startup;
            Check(model.HasSession && !model.IsReading, $"{reason}: fallback finishes normally");
        }
        File.WriteAllText(cachePath, originalCache);
    }

    private static async Task VerifyOnlineCacheAsync(string output, string fixture)
    {
        var storage = Path.Combine(output, "online-startup-settings");
        const string url = "https://example.invalid/svn/online";
        using (var seed = new MainViewModel(new UserSettings(storage), new ControlledRepository(fixture)))
        {
            await seed.OpenAsync(url, false, CancellationToken.None);
            await seed.BrowseDirectoryAsync("alpha");
            seed.SaveSession();
        }
        var repository = new ControlledRepository(fixture);
        var pause = new ReadPause();
        repository.OpenPause = pause;
        using var model = new MainViewModel(UserSettings.Load(storage), repository);
        var startup = model.RestoreInBackgroundAsync();
        await pause.Started;
        Check(model.IsOnlineOnly && model.CurrentRelative == "alpha" && model.Entries.Single().State == FileState.Unchecked
            && model.CanOperate && repository.ReadCount == 0,
            "Online cache is displayed before even the first network metadata request completes");
        model.Cancel();
        await startup;
        Check(model.CurrentRelative == "alpha" && !model.IsReading, "Cancelled online initialization retains its cache");

        repository.OnlineRepositoryId = "replacement";
        var metadataPause = new ReadPause();
        var fullPause = new ReadPause();
        repository.OpenPause = metadataPause;
        repository.RefreshPause = fullPause;
        startup = model.RestoreInBackgroundAsync();
        await metadataPause.Started;
        metadataPause.Release();
        await fullPause.Started;
        Check(!model.HasSession && model.Entries.Count == 0 && model.CanOperate,
            "A changed online repository identity invalidates cached operation targets before reinitializing");
        model.Cancel();
        await startup;
    }

    private static async Task VerifyHiddenOfficeLocksAsync(string output, Action<Window, string> render)
    {
        var fixture = Path.Combine(output, "office-lock-cache");
        Directory.CreateDirectory(fixture);
        var settings = new UserSettings(Path.Combine(fixture, "settings"))
        { LastLocation = fixture, LastLocationIsLocal = true, CloseBehavior = WindowCloseBehavior.Exit };
        const string url = "https://example.invalid/svn/test";
        var lockNames = new[] { "~$合同.docx", "~$统计表.xlsx", "~$汇报.pptx", ".~lock.合同.docx#", "图纸.dwl", "图纸.DWL2" };
        var visibleNames = new[] { "合同.docx", "恢复.asd", "备份.wbk", "普通.tmp", "图纸.dwg", "图纸.sv$", "图纸.bak", ".~lock.说明.docx", "依赖.lock" };
        var names = lockNames.Concat(visibleNames).ToArray();
        var entries = names.Select(name =>
        {
            var path = Path.Combine(fixture, name);
            File.WriteAllText(path, "cached file fixture");
            return new BrowserEntry(name, name, url + "/" + Uri.EscapeDataString(name), path, false,
                lockNames.Contains(name) ? FileState.LocalAdded : FileState.Equal,
                null, null, null, 1, "", null, null, "", "");
        }).ToList();
        var statuses = entries.Where(entry => lockNames.Contains(entry.Name))
            .ToDictionary(entry => entry.LocalPath!, entry => new WorkingStatus(entry.LocalPath!, "unversioned", "none", "", "",
                null, null, false, false, null, null));
        var snapshot = new RepositorySnapshot(7, DateTimeOffset.Now, statuses, new Dictionary<string, SvnInfo>());
        new BrowsingCacheStore(settings.CacheDirectory).Save(new(1, new(url, url, "test", fixture), snapshot,
            snapshot.CheckedAt, new Dictionary<string, SavedDirectory> { [""] = new(7, snapshot.CheckedAt, entries, $"本地新增 {lockNames.Length}") }));
        var pause = new ReadPause();
        var repository = new ControlledRepository(fixture) { RefreshPause = pause };
        var window = new MainWindow(settings, repository);
        using var model = (MainViewModel)window.DataContext;
        var startup = model.RestoreInBackgroundAsync();
        try
        {
            await pause.Started;
            Check(model.Entries.Count == visibleNames.Length && !model.Entries.Any(entry => lockNames.Contains(entry.Name))
                && model.DifferenceOverview == "0 项存在差异" && model.Roots.Single().Summary == "已检查项无差异",
                "Older disk caches hide Microsoft/LibreOffice/AutoCAD locks and fix counts before a server refresh finishes");
            Check(((DataGrid)window.FindName("FileGrid")).Items.Count == visibleNames.Length
                && ((ListBox)window.FindName("FileCards")).Items.Count == visibleNames.Length,
                "List and cards hide known locks while retaining recovery, backup, CAD autosave and ordinary temporary files");
            render(window, Path.Combine(output, "office-locks-hidden-list.png"));
            model.ViewMode = MainViewModel.CardViewName;
            render(window, Path.Combine(output, "office-locks-hidden-cards.png"));
            Check(names.All(name => File.Exists(Path.Combine(fixture, name))), "Cached display filtering leaves all files on disk");
            Console.WriteLine("PASS: Microsoft/LibreOffice/AutoCAD locks stay hidden in cached list/cards/counts; documents/recovery files/CAD autosaves remain visible.");
        }
        finally { model.Cancel(); await startup; window.Close(); }
    }

    private static async Task VerifyRealSnapshotAsync(string output, string workingCopy)
    {
        var storage = Path.Combine(output, "real-startup-settings");
        using (var seed = new MainViewModel(new UserSettings(storage)))
        {
            await seed.OpenAsync(workingCopy, true, CancellationToken.None);
            await seed.BrowseDirectoryAsync("nested");
            seed.SaveSession();
        }
        var settings = UserSettings.Load(storage);
        using var runner = new SvnRunner { ConfigurationDirectory = settings.SvnConfigurationDirectory };
        var repository = new PausedFullRefreshRepository(new RepositoryService(runner));
        using var model = new MainViewModel(settings, repository);
        var startup = model.RestoreInBackgroundAsync();
        await repository.Pause.Started;
        Check(model.CurrentRelative == "nested" && model.Entries.Single().Name == "child.txt" && model.CanOperate,
            "Real SVN metadata and child listings survive persistence and restore before a server status query completes");
        var path = model.Snapshot!.LocalInfo.Keys.First();
        Check(model.Snapshot.LocalInfo.ContainsKey(path.ToUpperInvariant())
            && model.Snapshot.Statuses.Count > 0, "Persisted SVN dictionaries retain Windows path lookup and status data");
        model.Cancel();
        await startup;
    }

    private sealed class PausedFullRefreshRepository(IRepositoryService inner) : IRepositoryService
    {
        public ReadPause Pause { get; } = new();
        public Task<RepositorySession> OpenWorkingCopyAsync(string path, CancellationToken token) => inner.OpenWorkingCopyAsync(path, token);
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
        public async Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token)
        {
            await Pause.WaitAsync(token);
            return await inner.RefreshAsync(session, token);
        }
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token) =>
            inner.RefreshDirectoryAsync(session, scope, token);
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot,
            string relative, CancellationToken token) => inner.ReadDirectoryAsync(session, snapshot, relative, token);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
