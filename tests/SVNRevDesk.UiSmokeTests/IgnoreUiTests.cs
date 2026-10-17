using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class IgnoreUiTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "ignore-fixture");
        Directory.CreateDirectory(root);
        var repositoryPath = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create"); start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start)!)
        {
            var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var local = Path.Combine(root, "中文 @ 工作副本");
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await operations.CheckoutAsync(url, local, false, CancellationToken.None);
        var tracked = Path.Combine(local, "已提交.txt"); File.WriteAllText(tracked, "tracked");
        Directory.CreateDirectory(Path.Combine(local, "其他目录"));
        await operations.AddAsync([tracked, Path.Combine(local, "其他目录")], CancellationToken.None);
        await operations.CommitAsync([local], "fixture", CancellationToken.None);
        var originalProperties = await operations.PropertiesAsync(local, CancellationToken.None);
        var folder = Path.Combine(local, "缓存目录"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "child.txt"), "child");
        Directory.CreateDirectory(Path.Combine(folder, "子目录"));
        var file = Path.Combine(local, "临时 [1] @ 文件.txt"); File.WriteAllText(file, "preserved");
        var added = Path.Combine(local, "已添加.txt"); File.WriteAllText(added, "added");
        await operations.AddAsync([added], CancellationToken.None);
        var service = new CountingRepository(new RepositoryService(runner));
        var settings = new UserSettings(Path.Combine(output, "ignore-settings")) { CloseBehavior = WindowCloseBehavior.Exit, AutoRefresh = false };
        var window = new MainWindow(settings, service);
        using var model = (MainViewModel)window.DataContext;
        await model.OpenAsync(local, true, CancellationToken.None);
        var grid = (DataGrid)window.FindName("FileGrid");
        var cards = (ListBox)window.FindName("FileCards");
        var tree = (TreeView)window.FindName("DirectoryTree");
        render(window, Path.Combine(output, "ignore-before.png"), 1360, 780);
        var menu = GridMenu("已提交.txt");
        Check(!Item(menu, "ignore").IsEnabled, "Tracked files disable ignore");
        menu = GridMenu("已添加.txt");
        Check(!Item(menu, "ignore").IsEnabled, "Scheduled additions disable ignore");
        var reads = service.Reads;
        var checkedAt = model.Snapshot!.CheckedAt;
        var revision = model.Snapshot.Revision;
        var refreshAt = model.LastFullRefreshAt;
        menu = GridMenu(Path.GetFileName(file));
        Check(Item(menu, "ignore").IsEnabled && (string)Item(menu, "ignore").Header == "忽略（仅本机）", "Unversioned file exposes local-only ignore");
        Item(menu, "ignore").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => !model.IsBusy);
        Check(model.StatusMessage == "忽略所选完成" && model.Entries.Single(entry => entry.LocalPath == file).State == FileState.Ignored
            && grid.SelectedItems.Cast<BrowserEntry>().Single().LocalPath == file && File.ReadAllText(file) == "preserved",
            "Actual file menu ignore keeps the item visible and selected while preserving its bytes");
        Check(service.Reads == reads && model.Snapshot.CheckedAt == checkedAt && model.Snapshot.Revision == revision
            && model.LastFullRefreshAt == refreshAt, "Ignore refreshes local metadata without server reads or changing server check times");
        Check(model.EntriesView.Cast<BrowserEntry>().Any(entry => entry.LocalPath == file && entry.State == FileState.Ignored),
            "Ignored items remain visible immediately without a display toggle");
        Check(File.Exists(settings.LocalIgnoreConfigurationPath)
            && await operations.PropertiesAsync(local, CancellationToken.None) == originalProperties,
            "The actual UI writes local configuration and leaves SVN properties unchanged");
        Check(new UserSettings().LocalIgnoreConfigurationPath == LocalIgnoreStore.DefaultPath,
            "Production local-ignore storage uses the Windows local user profile independently of portable settings");
        model.SelectedState = "已忽略";
        Check(model.VisibleEntryCount == 1 && !model.Entries.Single(entry => entry.LocalPath == file).HasDifferences,
            "Ignored-state filtering works without counting ignored files as differences");
        model.SelectedState = "全部状态";
        model.ViewMode = MainViewModel.CardViewName;
        render(window, Path.Combine(output, "ignore-cards.png"), 1360, 780);
        cards.SelectedItems.Clear(); cards.SelectedItems.Add(model.Entries.Single(entry => entry.LocalPath == file));
        menu = cards.ContextMenu; menu.PlacementTarget = cards; menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(Item(menu, "unignore").IsEnabled && (string)Item(menu, "unignore").Header == "取消本机忽略", "Card menu switches to local unignore for an ignored file");
        Item(menu, "unignore").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => !model.IsBusy);
        Check(model.StatusMessage == "取消忽略完成" && model.Entries.Single(entry => entry.LocalPath == file).IsUnversioned,
            "The actual card unignore restores unversioned status");
        model.ViewMode = MainViewModel.DetailsViewName;
        await model.BrowseDirectoryAsync("缓存目录");
        await model.BrowseDirectoryAsync("其他目录");
        render(window, Path.Combine(output, "ignore-tree-before.png"), 1360, 780);
        reads = service.Reads;
        menu = TreeMenu("缓存目录");
        Check(model.CurrentRelative == "其他目录" && Item(menu, "ignore").IsEnabled && service.Reads == reads,
            "Tree menu targets its clicked node without navigating or querying SVN");
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        Item(menu, "ignore").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => !model.IsBusy);
        Check(model.StatusMessage == "忽略所选完成" && model.CurrentRelative == "其他目录"
            && model.GetDirectoryEntry("缓存目录")!.State == FileState.Ignored && model.FindNode("缓存目录")!.IsIgnored && service.Reads == reads,
            "Actual tree ignore keeps its node visible with an ignored badge while preserving the displayed location");
        await model.BrowseDirectoryAsync("缓存目录");
        Check(model.Entries.All(entry => entry.State == FileState.Ignored) && model.VisibleEntryCount == 2
            && model.FindNode("缓存目录/子目录")!.IsIgnored,
            "Files and nested directory badges inherit ignored state and remain visible");
        menu = GridMenu("child.txt");
        Check(!Item(menu, "unignore").IsEnabled, "Inherited local ignores cannot be cancelled at a child");
        await model.BrowseDirectoryAsync("");
        render(window, Path.Combine(output, "ignore-list.png"), 1360, 780);
        menu = TreeMenu("缓存目录");
        Check(Item(menu, "unignore").IsEnabled, "Shown ignored tree node exposes unignore");
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        Item(menu, "unignore").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => !model.IsBusy);
        Check(model.StatusMessage == "取消忽略完成" && model.GetDirectoryEntry("缓存目录")!.State == FileState.LocalAdded,
            "Actual tree unignore restores the directory");
        await model.BrowseDirectoryAsync("缓存目录");
        Check(model.Entries.All(entry => entry.State == FileState.LocalAdded) && !model.FindNode("缓存目录/子目录")!.IsIgnored,
            "Previously cached descendants recover from ignored state and clear their badges");
        Check(service.Reads == reads, "Cached browsing, folder ignore and unignore never query the server");
        menu = GridMenu("child.txt");
        Check(Item(menu, "ignore").IsEnabled, "A file inside an unversioned parent exposes the local-ignore menu");
        menu = GridMenu("子目录");
        Check(Item(menu, "ignore").IsEnabled, "A directory inside an unversioned parent exposes the local-ignore menu");
        Item(menu, "ignore").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntil(() => !model.IsBusy);
        Check(model.FindNode("缓存目录/子目录")!.IsIgnored
            && model.Entries.Single(entry => entry.Name == "子目录").IsLocallyIgnored,
            "Ignoring a directory inside an unversioned parent marks both its row and tree node");
        await model.SetIgnoredAsync(model.Entries.Single(entry => entry.Name == "子目录"), false, CancellationToken.None);
        Check(!model.FindNode("缓存目录/子目录")!.IsIgnored, "Cancelling a nested directory ignore clears its tree badge");
        await model.BrowseDirectoryAsync("");
        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(model.Entries.First()); grid.SelectedItems.Add(model.Entries.Last());
        menu = grid.ContextMenu; menu.PlacementTarget = grid; menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(!Item(menu, "ignore").IsEnabled, "Multi-selection disables the single-item ignore action");
        menu = TreeMenu(""); Check(!Item(menu, "ignore").IsEnabled, "The opened root disables ignore");
        await model.SetIgnoredAsync(model.Entries.Single(entry => entry.LocalPath == file), true, CancellationToken.None);
        model.SaveSession();
        var settingsPath = Path.Combine(settings.StorageDirectory, "settings.json");
        var savedSettings = File.ReadAllText(settingsPath);
        File.WriteAllText(settingsPath, savedSettings.Insert(savedSettings.IndexOf('{') + 1, "\n\"ShowIgnored\": false,"));
        using var restored = new MainViewModel(UserSettings.Load(settings.StorageDirectory), service);
        await restored.RestoreLastSessionAsync(CancellationToken.None);
        Check(restored.EntriesView.Cast<BrowserEntry>().Single(entry => entry.LocalPath == file).State == FileState.Ignored,
            "Ignored state remains visible after reopening even with the old hidden-items preference");
        var sharedEntry = restored.Entries.Single(entry => entry.LocalPath == file) with { LocalIgnoreRulePath = null };
        Check(!BrowserActionAvailability.CanExecute("unignore", restored.Session, [sharedEntry]),
            "Shared SVN ignores expose no local cancellation action");
        var online = new RepositorySession(url, url, model.Session!.RepositoryId, null);
        Check(!BrowserActionAvailability.CanExecute("ignore", online, [model.Entries.Single(entry => entry.LocalPath == file)])
            && !BrowserActionAvailability.CanExecute("unignore", online, [model.Entries.Single(entry => entry.LocalPath == file)]), "Online-only sessions disable both write actions");
        render(window, Path.Combine(output, "ignore-narrow.png"), 1000, 620);
        Console.WriteLine("PASS: always-visible ignored files/folders, badges, file/card/tree unignore, inherited state, cached refresh, selection/root/online rules, legacy settings and unchanged server checks.");

        ContextMenu GridMenu(string name)
        {
            grid.SelectedItems.Clear(); grid.SelectedItems.Add(model.Entries.Single(entry => entry.Name == name));
            var result = grid.ContextMenu; result.PlacementTarget = grid; result.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return result;
        }
        ContextMenu TreeMenu(string relative)
        {
            window.UpdateLayout();
            var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(model.Roots.Single());
            var target = relative.Length == 0 ? rootItem : (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromItem(model.FindNode(relative));
            tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = target });
            tree.ContextMenu.PlacementTarget = tree; tree.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return tree.ContextMenu;
        }
    }
    private static MenuItem Item(ContextMenu menu, string tag) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, tag));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
    private static async Task WaitUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Check(done(), "Ignore action finished");
    }
    private sealed class CountingRepository(IRepositoryService inner) : IRepositoryService
    {
        public int Reads { get; private set; }
        public Task<RepositorySession> OpenWorkingCopyAsync(string path, CancellationToken token) => inner.OpenWorkingCopyAsync(path, token);
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
        public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token) { Reads++; return inner.RefreshAsync(session, token); }
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token) { Reads++; return inner.RefreshDirectoryAsync(session, scope, token); }
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relative, CancellationToken token) { Reads++; return inner.ReadDirectoryAsync(session, snapshot, relative, token); }
    }
}
