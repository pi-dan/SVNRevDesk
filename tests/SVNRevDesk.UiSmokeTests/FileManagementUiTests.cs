using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class FileManagementUiTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var owner = new Window { Left = -8000, Top = -8000, Width = 100, Height = 100, ShowActivated = false };
        owner.Show();
        var date = DateTimeOffset.Now;
        HistoryEntry[] entries = [new(123, "alice", date, "更新文档\n完整提交说明"), new(118, "bob", date, "整理文件"), new(3, "carol", date, "初次提交")];
        var calls = 0;
        var history = new HistoryWindow(owner, "https://svn.example.test/project/docs", (start, peg, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(new HistoryPage(entries.Take(2).ToArray(), 123, 117));
            Check(start == 117 && peg == 123, "Standalone history preserves its pagination cursor and frozen revision");
            return Task.FromResult(new HistoryPage([entries[2]], 123, null));
        }, CancellationToken.None);
        await Drive(history, async () =>
        {
            var grid = Find<DataGrid>(history, "RevisionHistory"); await Until(() => grid.Items.Count == 2);
            Check(Find<ComboBox>(history, "RevisionInput").Visibility == Visibility.Collapsed && !Descendants<Button>(history).Any(b => b.Name == "AcceptRevision"),
                "Standalone history is a browser with no revision-action input or Next button");
            grid.SelectedItem = entries[0]; Check(Find<TextBox>(history, "CommitDetails").Text.Contains("完整提交说明"), "History selection displays the full multiline commit message");
            var filter = Find<TextBox>(history, "HistorySearch"); filter.Text = "bob";
            Check(grid.Items.Count == 1 && ((HistoryEntry)grid.Items[0]).Revision == 118, "Standalone history filters by author");
            filter.Text = "r123"; Check(grid.Items.Count == 1, "Standalone history filters by revision number");
            filter.Text = "absent"; Check(grid.Items.Count == 0 && Find<TextBox>(history, "CommitDetails").Text.StartsWith("选择记录"), "Empty history filters clear stale details");
            filter.Text = ""; Click(history, "LoadEarlier"); await Until(() => grid.Items.Count == 3);
            Check(!Find<Button>(history, "LoadEarlier").IsEnabled, "History pagination ends at the earliest visible record");
            grid.SelectedItem = entries[0]; await Task.Delay(80); render(history, Path.Combine(output, "history-browser.png"), 960, 641);
            Check(grid.Columns.Last().ActualWidth >= 200, "The history message column remains readable after filtering and paging");
        });
        calls = 0;
        history = new HistoryWindow(owner, "file:///history", (_, _, _) => ++calls == 1
            ? Task.FromException<HistoryPage>(new IOException("offline")) : Task.FromResult(new HistoryPage([entries[0]], 123, null)), CancellationToken.None);
        await Drive(history, async () =>
        {
            await Until(() => Find<TextBlock>(history, "HistoryStatus").Text.Contains("读取失败"));
            Click(history, "LoadEarlier"); await Until(() => Find<DataGrid>(history, "RevisionHistory").Items.Count == 1);
            Check(calls == 2, "Standalone history can retry after a read failure");
        });

        var sample = new BrowserEntry("report.txt", "docs/deep/report.txt", "file:///repo/docs/deep/report.txt", null, false, FileState.Unchecked,
            null, null, 123, 20, "alice", date, null, "", "");
        calls = 0; string? selectedScope = null;
        var search = new RepositorySearchWindow(owner, "file:///repo", "docs", async (scope, query, ignored, progress, token) =>
        {
            calls++; selectedScope = scope;
            Check(query == "report", "Search passes the entered keyword to the repository service");
            progress.Report(new("docs/deep", 2, [sample])); await Task.Delay(40, token);
            return new(123, 2, 1, false);
        }, CancellationToken.None);
        await Drive(search, async () =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "report"; Click(search, "StartSearch");
            await Until(() => Find<TextBlock>(search, "SearchStatus").Text.StartsWith("搜索完成"));
            var grid = Find<DataGrid>(search, "SearchResults");
            Check(selectedScope == "docs" && grid.Items.Count == 1, "Search defaults to the current directory subtree and displays incremental results");
            Find<ComboBox>(search, "SearchScope").SelectedIndex = 1; Click(search, "StartSearch");
            await Until(() => calls == 2 && Find<Button>(search, "StartSearch").IsEnabled);
            Check(selectedScope == "" && grid.Items.Count == 1, "Searching the opened root replaces the previous result set");
            grid.SelectedItem = sample; render(search, Path.Combine(output, "cross-directory-search.png"), 1040, 611);
            Click(search, "LocateResult");
        });
        Check(search.SelectedResult == sample, "Locating a search result returns its exact repository-relative path");

        IProgress<RepositorySearchProgress>? oldProgress = null;
        calls = 0;
        search = new RepositorySearchWindow(owner, "file:///repo", "", async (_, _, _, progress, token) =>
        {
            calls++;
            if (calls == 1) { oldProgress = progress; await Task.Delay(30000, token); }
            progress.Report(new("docs", 1, [sample])); await Task.Delay(20, token); return new(123, 1, 1, false);
        }, CancellationToken.None);
        await Drive(search, async () =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "report"; Click(search, "StartSearch");
            await Until(() => calls == 1); Click(search, "StopSearch");
            await Until(() => Find<TextBlock>(search, "SearchStatus").Text.Contains("已停止"));
            Check(Find<Button>(search, "StartSearch").IsEnabled, "A stopped search can be started again");
            Click(search, "StartSearch"); await Until(() => Find<TextBlock>(search, "SearchStatus").Text.StartsWith("搜索完成"));
            oldProgress!.Report(new("stale", 9, [sample])); await Task.Delay(30);
            Check(Find<DataGrid>(search, "SearchResults").Items.Count == 1, "A late progress callback from an old search cannot contaminate a new search");
        });
        var late = new TaskCompletionSource<RepositorySearchResult>(); CancellationToken closeToken = default;
        search = new RepositorySearchWindow(owner, "file:///repo", "", (_, _, _, _, token) => { closeToken = token; return late.Task; }, CancellationToken.None);
        await Drive(search, async () =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "report"; Click(search, "StartSearch"); await Until(() => closeToken.CanBeCanceled); search.Close();
        });
        Check(closeToken.IsCancellationRequested, "Closing search cancels its outstanding repository read");
        late.SetResult(new(123, 1, 0, false)); await Task.Delay(30);

        var renameRoot = Path.Combine(output, "rename-preview"); Directory.CreateDirectory(renameRoot);
        var a = Path.Combine(renameRoot, "draft-a.txt"); var b = Path.Combine(renameRoot, "draft-b.txt"); File.WriteAllText(a, "a"); File.WriteAllText(b, "b");
        calls = 0;
        var rename = new BatchRenameWindow(owner, [new(a, false), new(b, false)], (rows, _) =>
        {
            if (++calls == 1) return Task.FromException<FileMovePlan>(new IOException("目标状态已变化"));
            return Task.FromResult(new FileMovePlan(renameRoot, renameRoot, rows.Select(r => new FileMoveItem(r.Source, r.Destination, false, false, false, false)).ToArray()));
        }, CancellationToken.None);
        await Drive(rename, async () =>
        {
            Check(!Find<Button>(rename, "AcceptRename").IsEnabled, "An unchanged rename preview cannot execute");
            Find<TextBox>(rename, "RenamePrefix").Text = "../";
            Check(!Find<Button>(rename, "AcceptRename").IsEnabled && Find<TextBlock>(rename, "RenameStatus").Text.Contains("错误 2"), "Invalid generated filenames disable batch execution");
            Find<TextBox>(rename, "RenamePrefix").Text = ""; Find<TextBox>(rename, "RenameFind").Text = "draft-a"; Find<TextBox>(rename, "RenameReplace").Text = "draft-b";
            Check(!Find<Button>(rename, "AcceptRename").IsEnabled, "Duplicate preview names disable batch execution");
            Find<TextBox>(rename, "RenameFind").Text = "draft-"; Find<TextBox>(rename, "RenameReplace").Text = "资料-";
            Find<TextBox>(rename, "RenameSuffix").Text = "-"; Find<CheckBox>(rename, "RenameNumber").IsChecked = true;
            var rows = Find<DataGrid>(rename, "RenamePreview").Items.Cast<BatchRenameRow>().ToArray();
            Check(rows.Select(r => r.NewName).SequenceEqual(["资料-a-001.txt", "资料-b-002.txt"]), "The batch window updates ordered numbering and extension-preserving names live");
            await Task.Delay(80); render(rename, Path.Combine(output, "batch-rename.png"), 1020, 631);
            Check(Find<DataGrid>(rename, "RenamePreview").Columns.All(column => column.ActualWidth >= 240), "All rename preview columns remain visible after live rule changes");
            Click(rename, "AcceptRename"); await Until(() => Find<TextBlock>(rename, "RenameStatus").Text.Contains("目标状态已变化"));
            Check(rename.IsVisible && File.Exists(a) && File.Exists(b), "Failed SVN validation keeps the rename preview open without mutating files");
            Click(rename, "AcceptRename"); await Task.Yield();
        });
        Check(rename.Plan?.Items.Count == 2 && File.Exists(a) && File.Exists(b), "Accepting a rename preview returns the validated plan for the operation layer");
        var latePlan = new TaskCompletionSource<FileMovePlan>(); closeToken = default;
        rename = new BatchRenameWindow(owner, [new(a, false)], (_, token) => { closeToken = token; return latePlan.Task; }, CancellationToken.None);
        await Drive(rename, async () =>
        {
            Find<TextBox>(rename, "RenamePrefix").Text = "close-"; Click(rename, "AcceptRename"); await Until(() => closeToken.CanBeCanceled); rename.Close();
        });
        Check(closeToken.IsCancellationRequested, "Closing batch preview cancels pending SVN validation");
        latePlan.SetResult(new(renameRoot, renameRoot, [])); await Task.Delay(30);
        Check(rename.Plan == null && File.Exists(a), "A late rename validation result cannot execute after the preview closes");
        await CachedIndexWindow(owner, output, render);
        owner.Close();
        await RealNavigationAndRename(output, render);
    }

    private sealed class DelayedIndexSource : ISearchIndexSource
    {
        public bool Delay, Fail, Entered;
        public int Lists;
        public CancellationToken LastToken;
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<long> ReadRevisionAsync(RepositorySession session, CancellationToken token)
        {
            LastToken = token; Entered = true;
            if (Delay) await Release.Task.WaitAsync(token);
            if (Fail) throw new IOException("offline UI fixture");
            return 7;
        }
        public Task<IReadOnlyList<ServerEntry>> ReadEntriesAsync(string url, long revision, bool recursive, CancellationToken token)
        { Lists++; return Task.FromResult<IReadOnlyList<ServerEntry>>([new("cached-report.txt", false, 12, 7, "alice", DateTimeOffset.Now, null)]); }
        public Task<SvnInfo?> ReadNodeAsync(string url, long revision, CancellationToken token) => Task.FromResult<SvnInfo?>(null);
        public Task<IReadOnlyList<SearchPathChange>> ReadChangesAsync(string url, long from, long to, CancellationToken token) => Task.FromResult<IReadOnlyList<SearchPathChange>>([]);
        public Task<RepositorySnapshot> ReadLocalAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token) =>
            Task.FromResult(new RepositorySnapshot(0, DateTimeOffset.Now, new Dictionary<string, WorkingStatus>(), new Dictionary<string, SvnInfo>()));
        public void Dispose() { }
    }

    private static async Task CachedIndexWindow(Window owner, string output, Action<Window, string, int, int> render)
    {
        var source = new DelayedIndexSource();
        await using var index = new RepositorySearchIndex(new("https://svn.example.test/repo", "https://svn.example.test/repo", "cached-ui", null),
            source, Path.Combine(output, "index-cache"), new LocalIgnoreStore(Path.Combine(output, "index-ignores.json")));
        await index.RefreshAsync(); source.Delay = true; source.Entered = false;
        var window = new RepositorySearchWindow(owner, "https://svn.example.test/repo", "", index, CancellationToken.None);
        await Drive(window, async () =>
        {
            await Until(() => source.Entered);
            Find<TextBox>(window, "SearchQuery").Text = "report";
            var grid = Find<DataGrid>(window, "SearchResults"); await Until(() => grid.Items.Count == 1);
            Check(!Find<Button>(window, "RefreshIndex").IsEnabled && Find<TextBlock>(window, "IndexStatus").Text.Contains("仍可搜索缓存"),
                "Cached results remain searchable while a slow background server check is blocked");
            Find<TextBox>(window, "SearchQuery").Text = "missing"; Find<TextBox>(window, "SearchQuery").Text = "cached-report";
            await Until(() => Find<TextBlock>(window, "SearchStatus").Text.StartsWith("搜索完成") && grid.Items.Count == 1);
            source.Fail = true; source.Release.SetResult();
            await Until(() => Find<Button>(window, "RefreshIndex").IsEnabled);
            await Until(() => grid.Items.Count == 1);
            Check(Find<TextBlock>(window, "IndexStatus").Text.Contains("使用缓存") && index.Status.Revision == 7,
                "Offline index checks visibly preserve cached results and the last verified snapshot");
            await Task.Delay(80); render(window, Path.Combine(output, "search-index-offline.png"), 1040, 611);
            source.Delay = source.Fail = false; var lists = source.Lists;
            Click(window, "RebuildIndex"); await Until(() => Find<Button>(window, "RebuildIndex").IsEnabled);
            Check(source.Lists == lists + 1 && !Find<TextBlock>(window, "IndexStatus").Text.Contains("使用缓存"),
                "The rebuild button reloads the complete server list and clears the offline warning");
        });
        source.Delay = true; source.Entered = false; source.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window = new RepositorySearchWindow(owner, "https://svn.example.test/repo", "", index, CancellationToken.None);
        await Drive(window, async () => { await Until(() => source.Entered); window.Close(); });
        Check(source.LastToken.IsCancellationRequested, "Closing indexed search cancels its window-owned server maintenance");
        source.Release.TrySetResult();
        source.Delay = false; await index.RefreshAsync();
        Check(index.Status.HasIndex, "Closing a search window leaves its shared session index usable");
    }

    private static async Task RealNavigationAndRename(string output, Action<Window, string, int, int> render)
    {
        var repoPath = Path.Combine(output, "repository");
        var info = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        info.ArgumentList.Add("create"); info.ArgumentList.Add(repoPath);
        using (var admin = Process.Start(info)!) { var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync(); if (admin.ExitCode != 0) throw new IOException(await error); }
        using var runner = new SvnRunner(); var ops = new SvnOperations(runner);
        var wc = Path.Combine(output, "working-copy"); var url = new Uri(repoPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await ops.CheckoutAsync(url, wc, false, CancellationToken.None);
        var docs = Path.Combine(wc, "docs"); Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "report-a.txt"), "a\n"); File.WriteAllText(Path.Combine(docs, "report-b.txt"), "b\n");
        await ops.AddAsync([docs], CancellationToken.None); await ops.CommitAsync([wc], "UI fixture", CancellationToken.None);
        var outside = Path.Combine(wc, "outside.txt"); File.WriteAllText(outside, "unrelated\n");
        await ops.AddAsync([outside], CancellationToken.None); await ops.CommitAsync([wc], "unrelated outside commit", CancellationToken.None);
        var window = new MainWindow(new UserSettings(Path.Combine(output, "real-settings")) { CloseBehavior = WindowCloseBehavior.Exit });
        using var model = (MainViewModel)window.DataContext;
        await model.RunAsync("Open UI fixture", token => model.OpenAsync(wc, true, token));
        typeof(MainWindow).GetField("windowLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Left = -8000; window.Top = -8000; window.ShowActivated = false; window.Show();
        model.FilterText = "no match"; model.DifferencesOnly = true; model.SelectedState = FileState.LocalModified.DisplayName();
        var drive = DriveNext<RepositorySearchWindow>(window, async search =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "report-a"; Click(search, "StartSearch");
            var grid = Find<DataGrid>(search, "SearchResults"); await Until(() => grid.Items.Count == 1 && Find<Button>(search, "StartSearch").IsEnabled);
            Check(Find<TextBlock>(search, "IndexStatus").Text.Contains("服务器核对") && Find<TextBox>(search, "SearchQuery").IsEnabled,
                "Indexed search shows verification timestamps and keeps keyword input enabled");
            Find<TextBox>(search, "SearchQuery").Text = "report-b";
            await Until(() => grid.Items.Count == 1 && ((BrowserEntry)grid.Items[0]).Name == "report-b.txt");
            Check(true, "Typing a new keyword automatically replaces indexed results");
            Find<TextBox>(search, "SearchQuery").Text = "report-a";
            await Until(() => grid.Items.Count == 1 && ((BrowserEntry)grid.Items[0]).Name == "report-a.txt");
            await Task.Delay(80); render(search, Path.Combine(output, "cross-directory-search-index.png"), 1040, 611);
            Click(search, "RebuildIndex"); await Until(() => Find<Button>(search, "RebuildIndex").IsEnabled);
            Check(grid.Items.Count == 1, "Rebuilding the index retains the active query");
            grid.SelectedIndex = 0; Click(search, "LocateResult");
        });
        ((Button)window.FindName("CrossDirectorySearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await drive; await Until(() => !model.IsBusy);
        var mainGrid = (DataGrid)window.FindName("FileGrid");
        Check(model.CurrentRelative == "docs" && mainGrid.SelectedItem is BrowserEntry { Name: "report-a.txt" }
            && model.FilterText == "" && !model.DifferencesOnly && model.SelectedState == "全部状态", "The real search button locates and selects a deep result even when main-view filters previously hid it");
        render(window, Path.Combine(output, "main-search-entry.png"), 1000, 620);
        var selected = model.Entries.ToArray(); model.SetSelection(selected);
        Check(model.CanExecute("batch-rename") && model.CanExecute("log"), "Multi-selection exposes batch rename and scoped history actions");
        var action = new ClientActions(window, model);
        drive = DriveNext<HistoryWindow>(window, async history =>
        {
            var grid = Find<DataGrid>(history, "RevisionHistory");
            await Until(() => grid.Items.Count > 0 && !Find<Button>(history, "LoadEarlier").IsEnabled);
            Check(grid.Items.Cast<HistoryEntry>().All(entry => entry.Message == "UI fixture") && grid.Items.Count == 1,
                "The real multi-selection history action excludes unrelated commits and deduplicates shared revisions");
            Check(Find<TextBlock>(history, "RevisionHistoryScope").Text.Contains("docs/report-a.txt")
                && Find<TextBlock>(history, "RevisionHistoryScope").Text.Contains("docs/report-b.txt"), "The history browser visibly identifies each selected target");
        });
        await model.RunAsync("History UI scope", token => action.ExecuteAsync("log", selected, token)); await drive;
        drive = DriveNext<BatchRenameWindow>(window, async rename =>
        {
            Find<TextBox>(rename, "RenamePrefix").Text = "ready-"; Click(rename, "AcceptRename");
            await Until(() => !rename.IsVisible);
        });
        await model.RunAsync("Batch UI rename", token => action.ExecuteAsync("batch-rename", selected, token)); await drive;
        Check(model.Entries.Count(e => e.Name.StartsWith("ready-")) == 2 && File.Exists(Path.Combine(docs, "ready-report-a.txt")),
            "The real batch action executes SVN moves and refreshes the current directory immediately");
        var index = model.GetSearchIndex();
        Check((await IndexMatches(index, "ready-report")).Length == 2, "SVN batch rename refreshes the existing local search index");
        var created = Path.Combine(docs, "created-index.txt");
        await model.RunAsync("Create indexed item", token => model.CreateLocalItemAsync("docs", new NewItemRequest("created-index.txt", NewItemKind.Text), false, token));
        Check((await IndexMatches(index, "created-index")).Single().LocalPath == created, "Application file creation updates the search index immediately");
        await ops.CopyBranchAsync(url + "/docs", url + "/remote-only", "new remote directory", CancellationToken.None);
        await model.RefreshAllInBackgroundAsync();
        Check((await IndexMatches(index, "remote-only/report")).Length == 2, "The same full refresh used by the hourly timer updates unbrowsed indexed paths");
        await ops.CopyBranchAsync(url + "/docs", url + "/manual-only", "manual refresh directory", CancellationToken.None);
        await model.RunAsync("Manual refresh", model.RefreshCurrentDirectoryAsync);
        Check((await IndexMatches(index, "manual-only/report")).Length == 2, "Manual scoped refresh also updates the complete existing search index");
        Check(!BrowserActionAvailability.CanExecute("batch-rename", model.Session, []) && BrowserActionAvailability.CanExecute("batch-rename", new(url, url, "repo", null), selected) == false,
            "Batch rename is disabled without a local working copy or selected files");
        window.Close();
    }

    private static async Task<BrowserEntry[]> IndexMatches(RepositorySearchIndex index, string query)
    {
        var matches = new List<BrowserEntry>();
        await index.SearchAsync("", query, false, new InlineProgress<RepositorySearchProgress>(value => matches.AddRange(value.Matches)));
        return matches.ToArray();
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private static Task DriveNext<T>(Window owner, Func<T, Task> action) where T : Window
    {
        var completion = new TaskCompletionSource();
        owner.Dispatcher.BeginInvoke(async () =>
        {
            T? dialog = null;
            try { await Until(() => Application.Current.Windows.OfType<T>().Any()); dialog = Application.Current.Windows.OfType<T>().Single(); await action(dialog); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        });
        return completion.Task;
    }

    private static async Task Drive(Window window, Func<Task> action)
    {
        window.Left = -8000; window.Top = -8000; window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowActivated = false;
        var completion = new TaskCompletionSource();
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            try { await action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); }
            finally { if (window.IsVisible) window.Close(); }
        });
        window.ShowDialog(); await completion.Task;
    }
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(element => element.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew(); while (!condition()) { if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("File management UI did not settle"); await Task.Delay(20); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
