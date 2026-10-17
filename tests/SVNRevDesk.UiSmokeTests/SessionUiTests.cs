using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class SessionUiTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "session-fixture"); Directory.CreateDirectory(root);
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var (first, firstUrl) = await Fixture("first");
        var (second, _) = await Fixture("second");
        var settings = new UserSettings(Path.Combine(root, "settings")) { CloseBehavior = WindowCloseBehavior.Exit, AutoRefresh = false };
        var window = new MainWindow(settings) { Left = -8000, Top = -8000, ShowActivated = false };
        typeof(MainWindow).GetField("windowLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Show();
        var tabs = (ListBox)window.FindName("SessionTabs");
        var grid = (DataGrid)window.FindName("FileGrid");
        await Open(first);
        var firstModel = (MainViewModel)window.DataContext;
        await firstModel.BrowseDirectoryAsync("nested");
        firstModel.FilterText = "shared";
        var selected = firstModel.Entries.Single(); grid.SelectedItems.Add(selected);
        firstModel.Runner.Username = "first-user"; firstModel.Runner.Password = "memory-only-secret";
        await Open(second);
        var secondModel = (MainViewModel)window.DataContext;
        Check(tabs.Items.Count == 2 && firstModel != secondModel && secondModel.CurrentRelative == "" && secondModel.FilterText == ""
            && grid.SelectedItems.Count == 0 && secondModel.Runner.Username == null && secondModel.Runner.Password == null,
            "Opening another repository creates independent navigation, filters, selection and in-memory credentials");
        Check(settings.WorkingCopyLinks.Count == 2 && settings.WorkingCopyLinks.Single(link => link.LocalPath == first).Url == firstUrl,
            "Verified working-copy paths and repository URLs are automatically retained in a long-term association list");
        await secondModel.BrowseDirectoryAsync("nested");
        grid.SelectedItems.Add(secondModel.Entries.Single());
        await Open(first + Path.DirectorySeparatorChar);
        Check(tabs.Items.Count == 2 && ReferenceEquals(window.DataContext, firstModel) && firstModel.CurrentRelative == "nested"
            && firstModel.FilterText == "shared" && grid.SelectedItems.Count == 1 && Equals(grid.SelectedItems[0], selected),
            "Reopening the same working-copy path reuses its tab and restores directory, filtering and selection");
        Check(firstModel.Runner.Username == "first-user" && firstModel.Runner.Password == "memory-only-secret", "Session credentials survive a tab switch only in their own runner");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = firstModel.RunAsync("busy fixture", _ => gate.Task);
        await Open(second);
        Check(ReferenceEquals(window.DataContext, firstModel), "An exclusive SVN operation prevents changing its active UI target");
        gate.SetResult(); await busy;

        await Open(firstUrl);
        var online = (MainViewModel)window.DataContext;
        Check(tabs.Items.Count == 3 && online.IsOnlineOnly && !online.CanExecute("update-directory-revision") && !online.CanExecute("reverse-merge-directory"),
            "The same repository URL has a separate online tab with local write actions disabled");
        await Open(first);
        Check(firstModel.CanExecute("update-revision") && firstModel.CanExecute("reverse-merge")
            && firstModel.CanExecute("update-directory-revision") && firstModel.CanExecute("reverse-merge-directory"),
            "Revision update and reverse merge are available for tracked local targets");
        Check(grid.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "update-revision"))
            && grid.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "reverse-merge")), "List context menus expose both revision workflows");

        using (var timer = AnswerInputs("常用目录")) Invoke(window, "AddFavorite", true);
        Check(settings.Favorites is [{ Name: "常用目录", RelativeDirectory: "nested", IsLocal: true }], "Favorite captures its working-copy root and current subdirectory");
        var favorites = (MenuItem)window.FindName("FavoritesMenu");
        favorites.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, favorites));
        var favoriteMenu = favorites.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "常用目录"));
        using (var timer = AnswerInputs("项目收藏")) ((MenuItem)favoriteMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(settings.Favorites.Single().Name == "项目收藏", "Favorites can be renamed through the real menu workflow");
        await Open(second);
        ((MenuItem)favoriteMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => ReferenceEquals(window.DataContext, firstModel) && !firstModel.IsReading);
        Check(firstModel.CurrentRelative == "nested" && tabs.Items.Count == 3, "Opening a favorite activates its existing tab and saved subdirectory");

        using (var timer = DialogTimer(() =>
        {
            var dialog = Application.Current.Windows.OfType<WorkingCopyLinksWindow>().FirstOrDefault();
            if (dialog == null) return;
            Descendants<DataGrid>(dialog).Single().SelectedItem = dialog.Links.Single(link => link.LocalPath == first);
            render(dialog, Path.Combine(output, "working-copy-links.png"), 960, 520);
            Descendants<Button>(dialog).Single(button => Equals(button.Content, "打开 URL")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        })) Invoke(window, "WorkingCopyLinks_Click", window, new RoutedEventArgs());
        await Until(() => ReferenceEquals(window.DataContext, online) && !online.IsReading);
        Check(tabs.Items.Count == 3, "The association list opens its corresponding repository URL in the existing online tab");
        await Open(first);
        using (var timer = DialogTimer(() =>
        {
            var dialog = Application.Current.Windows.OfType<WorkingCopyLinksWindow>().FirstOrDefault();
            if (dialog == null) return;
            Descendants<DataGrid>(dialog).Single().SelectedItem = dialog.Links.Single(link => link.LocalPath == second);
            Descendants<Button>(dialog).Single(button => Equals(button.Content, "移除记录")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog.Close();
        })) Invoke(window, "WorkingCopyLinks_Click", window, new RoutedEventArgs());
        Check(settings.WorkingCopyLinks.Count == 1 && Directory.Exists(second), "Removing an association preserves its working-copy files");

        render(window, Path.Combine(output, "multi-session.png"), 1360, 780);
        window.Width = 1000; window.Height = 620; window.UpdateLayout();
        render(window, Path.Combine(output, "multi-session-narrow.png"), 1000, 620);
        window.Width = 1360; window.Height = 780; window.UpdateLayout();
        var rollbackPath = Path.Combine(first, "nested", "shared.txt");
        var original = File.ReadAllText(rollbackPath);
        var previewSeen = false;
        var fileHistoryScoped = false;
        using (var timer = DialogTimer(() =>
        {
            foreach (var dialog in Application.Current.Windows.OfType<Window>().ToArray())
            {
                if (dialog is RevisionPickerWindow picker)
                {
                    var history = Descendants<DataGrid>(picker).Single();
                    var revision = history.Items.OfType<HistoryEntry>().FirstOrDefault(item => item.Revision == 2);
                    if (revision == null) continue;
                    fileHistoryScoped = history.Items.OfType<HistoryEntry>().Select(item => item.Revision).SequenceEqual([2L, 1L]);
                    history.SelectedItem = revision;
                    Descendants<Button>(picker).Single(button => button.Name == "AcceptRevision").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                if (dialog is ReverseMergePreviewWindow preview)
                {
                    previewSeen = true;
                    render(preview, Path.Combine(output, "reverse-merge-preview.png"), 820, 560);
                    preview.DialogResult = false;
                }
            }
        }))
        {
            await firstModel.RunAsync("回滚指定提交", token => new ClientActions(window, firstModel).ExecuteAsync("reverse-merge", [selected], token));
        }
        Check(previewSeen && File.ReadAllText(rollbackPath) == original && firstModel.StatusMessage.StartsWith("已取消"),
            "The actual rollback dialog previews its change and cancellation does not mutate the working copy");
        Check(fileHistoryScoped, "The rollback picker excludes unrelated repository and parent-directory changes for a selected file");

        await VerifyHistoryScope("update-revision", [selected], [2L, 1L]);
        await VerifyHistoryScope("update-directory-revision", [], [4L, 2L, 1L]);
        await VerifyHistoryScope("reverse-merge-directory", [], [4L, 2L, 1L]);
        await firstModel.BrowseDirectoryAsync("");
        var folder = firstModel.Entries.Single(entry => entry.Name == "nested");
        var outside = firstModel.Entries.Single(entry => entry.Name == "outside.txt");
        await VerifyHistoryScope("update-revision", [folder], [4L, 2L, 1L]);
        await VerifyHistoryScope("update-revision", [folder, outside], [5L, 4L, 3L, 2L, 1L]);
        await firstModel.BrowseDirectoryAsync("nested");

        Invoke(window, "SaveWorkspace");
        var json = File.ReadAllText(Path.Combine(settings.StorageDirectory, "settings.json"));
        Check(!json.Contains("memory-only-secret") && !json.Contains("first-user"), "Persisted tabs and favorites exclude session credentials");
        Check(Directory.GetFiles(Path.Combine(settings.CacheDirectory, "Sessions"), "browsing-session.json", SearchOption.AllDirectories).Length == 3,
            "Each local or online session has its own persistent browsing cache");
        window.Close();

        var restoredSettings = UserSettings.Load(settings.StorageDirectory);
        var restoredWindow = new MainWindow(restoredSettings);
        await (Task)Invoke(restoredWindow, "RestoreWorkspaceAsync")!;
        var restored = (MainViewModel)restoredWindow.DataContext;
        var restoredTabs = (ListBox)restoredWindow.FindName("SessionTabs");
        Check(restoredTabs.Items.Count == 3 && restored.Session?.LocalRoot == first && restored.CurrentRelative == "nested"
            && restored.Runner.Password == null && restoredSettings.Favorites.Single().Name == "项目收藏" && restoredSettings.WorkingCopyLinks.Count == 1,
            "Restart restores open tabs, the active repository, navigation and favorites without persisting credentials");
        await (Task)Invoke(restoredWindow, "OpenLocationAsync", second)!;
        Check(((MainViewModel)restoredWindow.DataContext).CurrentRelative == "nested", "Inactive restored tabs load their own saved navigation when selected");
        Invoke(restoredWindow, "CloseActiveSession");
        await Until(() => restoredTabs.Items.Count == 2 && !((MainViewModel)restoredWindow.DataContext).IsReading);
        Check(Directory.Exists(second) && File.Exists(Path.Combine(second, "nested", "shared.txt")), "Closing a tab preserves its working-copy files");
        Invoke(restoredWindow, "CloseActiveSession");
        await Until(() => restoredTabs.Items.Count == 1 && !((MainViewModel)restoredWindow.DataContext).IsReading);
        Invoke(restoredWindow, "CloseActiveSession");
        await Until(() => restoredTabs.Items.Count == 1 && !((MainViewModel)restoredWindow.DataContext).HasSession);
        restoredWindow.Close();
        var empty = UserSettings.Load(settings.StorageDirectory);
        Check(empty.OpenTabs.Count == 0 && empty.LastLocation == "", "Closing the final tab leaves an empty workspace on next startup");
        await VerifySlowSessionsAsync(Path.Combine(root, "slow-sessions"));

        async Task Open(string location) => await (Task)Invoke(window, "OpenLocationAsync", location)!;
        async Task VerifyHistoryScope(string operation, IReadOnlyList<BrowserEntry> selection, long[] expected)
        {
            long[]? actual = null;
            var time = Stopwatch.StartNew();
            using (var timer = DialogTimer(() =>
            {
                var picker = Application.Current.Windows.OfType<RevisionPickerWindow>().FirstOrDefault();
                if (picker == null) return;
                var history = Descendants<DataGrid>(picker).Single();
                if (history.Items.Count == 0 && time.Elapsed.TotalSeconds < 20) return;
                actual = history.Items.OfType<HistoryEntry>().Select(entry => entry.Revision).ToArray();
                picker.DialogResult = false;
            }))
            {
                await firstModel.RunAsync(operation, token => new ClientActions(window, firstModel).ExecuteAsync(operation, selection, token));
            }
            Check(actual != null && actual.SequenceEqual(expected) && firstModel.StatusMessage.StartsWith("已取消"),
                $"{operation} with {selection.Count} selected targets lists only affected revisions and can be cancelled");
        }
        async Task<(string, string)> Fixture(string name)
        {
            var repo = Path.Combine(root, name + "-repository");
            var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("create"); start.ArgumentList.Add(repo);
            using var admin = Process.Start(start)!;
            var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
            var url = new Uri(repo + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
            var local = Path.Combine(root, name + "-working-copy");
            await operations.CheckoutAsync(url, local, false, CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(local, "nested"));
            var file = Path.Combine(local, "nested", "shared.txt"); File.WriteAllText(file, name + " initial\n");
            await operations.AddAsync([Path.Combine(local, "nested")], CancellationToken.None);
            await operations.CommitAsync([local], "initial", CancellationToken.None);
            File.AppendAllText(file, "second revision\n");
            await operations.CommitAsync([local], "second", CancellationToken.None);
            var outsideFile = Path.Combine(local, "outside.txt"); File.WriteAllText(outsideFile, "outside\n");
            await operations.AddAsync([outsideFile], CancellationToken.None);
            await operations.CommitAsync([local], "unrelated file", CancellationToken.None); // r3
            await operations.UpdateAsync(local, true, CancellationToken.None);
            await runner.RunAsync(["propset", "custom:test", "directory only", SvnRunner.Target(Path.Combine(local, "nested"))]);
            await operations.CommitAsync([local], "directory properties", CancellationToken.None); // r4
            File.AppendAllText(outsideFile, "outside again\n");
            await operations.CommitAsync([local], "unrelated update", CancellationToken.None); // r5
            var unselected = Path.Combine(local, "unselected.txt"); File.WriteAllText(unselected, "unselected\n");
            await operations.AddAsync([unselected], CancellationToken.None);
            await operations.CommitAsync([local], "unselected sibling", CancellationToken.None); // r6
            await operations.UpdateAsync(local, true, CancellationToken.None);
            return (local, url);
        }
    }

    private static async Task VerifySlowSessionsAsync(string output)
    {
        var repository = new BrowsingTests.ControlledRepository(output);
        var window = new MainWindow(new UserSettings(output) { CloseBehavior = WindowCloseBehavior.Exit }, repository);
        var pause = new BrowsingTests.ReadPause(ignoreCancellation: true);
        repository.OpenPause = pause;
        var first = (Task)Invoke(window, "OpenLocationAsync", "https://example.invalid/slow")!;
        await pause.Started;
        await (Task)Invoke(window, "OpenLocationAsync", "https://example.invalid/fast")!;
        var fastModel = (MainViewModel)window.DataContext;
        var fastEntries = fastModel.Entries.ToArray();
        pause.Release(); await first;
        Check(ReferenceEquals(window.DataContext, fastModel) && fastModel.Session?.Url == "https://example.invalid/fast"
            && fastModel.Entries.SequenceEqual(fastEntries), "A late result from an inactive slow session cannot replace the visible repository or rows");
        pause = new BrowsingTests.ReadPause(ignoreCancellation: true);
        repository.OpenPause = pause;
        var closing = (Task)Invoke(window, "OpenLocationAsync", "https://example.invalid/closing")!;
        await pause.Started;
        Invoke(window, "CloseActiveSession");
        await Until(() => ReferenceEquals(window.DataContext, fastModel));
        pause.Release(); await closing;
        Check(((ListBox)window.FindName("SessionTabs")).Items.Count == 2 && ReferenceEquals(window.DataContext, fastModel),
            "Closing an initializing session cancels it, and its late result cannot resurrect the tab");
        window.Close();

        var saved = new UserSettings(Path.Combine(output, "restore")) { CloseBehavior = WindowCloseBehavior.Exit,
            OpenTabs = [new("https://example.invalid/restoring", false, "", [])] };
        repository = new BrowsingTests.ControlledRepository(output);
        pause = new BrowsingTests.ReadPause(ignoreCancellation: true); repository.OpenPause = pause;
        window = new MainWindow(saved, repository);
        var restoration = (Task)Invoke(window, "RestoreWorkspaceAsync")!;
        await pause.Started;
        Check(((ListBox)window.FindName("SessionTabs")).Items.Count == 1,
            "Startup removes its temporary blank tab before waiting for the saved repository");
        await (Task)Invoke(window, "OpenLocationAsync", "https://example.invalid/new-during-restore")!;
        var newModel = window.DataContext;
        pause.Release(); await restoration;
        Check(((ListBox)window.FindName("SessionTabs")).Items.Count == 2 && ReferenceEquals(window.DataContext, newModel),
            "Finishing startup cannot remove or dispose a session opened while restoration was pending");
        window.Close();
    }

    private static object? Invoke(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, arguments);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("Session UI did not settle"); await Task.Delay(20); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class TimerScope(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static TimerScope DialogTimer(Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => action(); timer.Start(); return new(timer);
    }
    private static TimerScope AnswerInputs(string value) => DialogTimer(() =>
    {
        var dialog = Application.Current.Windows.OfType<InputDialog>().FirstOrDefault();
        if (dialog == null) return;
        Descendants<TextBox>(dialog).First().Text = value; dialog.DialogResult = true;
    });
}
