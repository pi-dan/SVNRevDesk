using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class DirectoryActionTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "directory-actions");
        Directory.CreateDirectory(root);
        var repositoryPath = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start)!)
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var local = Path.Combine(root, "中文 @ 工作副本");
        await operations.CheckoutAsync(url, local, false, CancellationToken.None);
        foreach (var name in new[] { "displayed", "target", "deleted-directory" })
        {
            Directory.CreateDirectory(Path.Combine(local, name));
            File.WriteAllText(Path.Combine(local, name, "child.txt"), name + " original");
        }
        File.WriteAllText(Path.Combine(local, "delete-file.txt"), "delete me");
        File.WriteAllText(Path.Combine(local, "missing.txt"), "missing me");
        await operations.AddAsync(Directory.EnumerateFileSystemEntries(local).Where(path => Path.GetFileName(path) != ".svn"), CancellationToken.None);
        await operations.CommitAsync([local], "Directory actions fixture", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(local, "new-local"));
        File.WriteAllText(Path.Combine(local, "new-local", "new.txt"), "unversioned");
        var service = new CountingRepository(new RepositoryService(runner));
        var settings = new UserSettings(Path.Combine(root, "settings")) { CloseBehavior = WindowCloseBehavior.Exit };
        var window = new MainWindow(settings, service);
        using var model = (MainViewModel)window.DataContext;
        await model.OpenAsync(local, true, CancellationToken.None);
        await model.BrowseDirectoryAsync("displayed");
        var grid = (DataGrid)window.FindName("FileGrid");
        grid.SelectedItems.Add(model.Entries.Single());
        render(window, Path.Combine(output, "directory-actions-before.png"), 1360, 780);
        var tree = (TreeView)window.FindName("DirectoryTree");
        var reads = service.Reads;
        var menu = OpenMenu("target");
        Check(model.CurrentRelative == "displayed" && model.FindNode("target")!.IsSelected && service.Reads == reads,
            "Tree context click highlights an unvisited target without changing the displayed rows or reading the server");
        foreach (var operation in new[] { "update", "commit", "delete", "revert", "move", "log", "properties", "set-property", "export", "compare-server", "compare-base", "tree-refresh" })
            Check(Item(menu, operation).IsEnabled, "Tree menu enables " + operation + " for a versioned directory");
        CloseMenu(menu);
        Check(model.FindNode("displayed")!.IsSelected && model.CurrentRelative == "displayed", "Closing the context menu restores the normal browsing selection");

        menu = OpenMenu("new-local");
        Check(Item(menu, "add").IsEnabled && !Item(menu, "export").IsEnabled && model.GetDirectoryEntry("new-local")!.ServerRevision == null,
            "Tree menus use actual unversioned metadata rather than inventing a server revision");
        CloseMenu(menu);
        Item(menu, "add").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => !model.IsBusy && model.StatusMessage == "SVN 操作完成");
        var statuses = await operations.ReadLocalDirectoryStatusAsync(local, local, CancellationToken.None);
        Check(statuses.Single(status => status.Path == Path.Combine(local, "new-local")).Item == "added"
            && File.ReadAllText(Path.Combine(local, "displayed", "child.txt")) == "displayed original"
            && model.CurrentRelative == "displayed", "The actual tree Add command affects the pointed directory and preserves the right-hand selection");

        menu = OpenMenu("");
        Check(!Item(menu, "delete").IsEnabled && !Item(menu, "move").IsEnabled
            && Item(menu, "update").IsEnabled && Item(menu, "commit").IsEnabled,
            "The opened root supports update/commit but cannot be deleted or moved");
        CloseMenu(menu);

        var peer = Path.Combine(root, "peer");
        await operations.CheckoutAsync(url, peer, false, CancellationToken.None);
        File.WriteAllText(Path.Combine(peer, "target", "child.txt"), "target server update");
        File.WriteAllText(Path.Combine(peer, "displayed", "child.txt"), "displayed server update");
        await operations.CommitAsync([peer], "Remote directory updates", CancellationToken.None);
        await model.RefreshAllInBackgroundAsync();
        menu = OpenMenu("target");
        CloseMenu(menu);
        service.Scopes.Clear();
        Item(menu, "update").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => !model.IsBusy && model.StatusMessage == "更新所选完成");
        Check(File.ReadAllText(Path.Combine(local, "target", "child.txt")) == "target server update"
            && File.ReadAllText(Path.Combine(local, "displayed", "child.txt")) == "displayed original"
            && model.CurrentRelative == "displayed", "The actual tree Update command updates only the pointed subtree");
        Check(service.Scopes.Contains(new DirectoryRefresh("target", true))
            && service.Scopes.All(scope => scope.RelativePath is "" or "target"), "Tree operations refresh only their target subtree and its containing list");
        menu = OpenMenu("target");
        CloseMenu(menu);
        service.Scopes.Clear();
        Item(menu, "tree-refresh").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => !model.IsReading && service.Scopes.Count > 0);
        Check(service.Scopes.SequenceEqual([new DirectoryRefresh("target")]) && model.CurrentRelative == "displayed",
            "Refresh this directory checks the pointed directory without changing the browsing location");

        await operations.DeleteAsync([Path.Combine(local, "delete-file.txt"), Path.Combine(local, "deleted-directory")], CancellationToken.None);
        File.Delete(Path.Combine(local, "missing.txt"));
        await model.RefreshPathsAsync([Path.Combine(local, "deleted-directory"), Path.Combine(local, "delete-file.txt"), Path.Combine(local, "missing.txt")], CancellationToken.None);
        await model.BrowseDirectoryAsync("");
        Check(model.Entries.Single(entry => entry.Name == "delete-file.txt").State == FileState.LocalDeleted
            && model.Entries.Single(entry => entry.Name == "deleted-directory").State == FileState.LocalDeleted
            && model.Entries.Single(entry => entry.Name == "missing.txt").State == FileState.Missing,
            "The file grid distinguishes scheduled file/directory deletions from a missing file");
        Check(model.FindNode("")!.Summary.Contains("本地已删除", StringComparison.Ordinal)
            && model.StateFilters.Contains("本地已删除"), "Tree summaries and the state filter include local deletion");
        render(window, Path.Combine(output, "local-deleted-list.png"), 1360, 780);
        var deletedRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Entries.Single(entry => entry.Name == "delete-file.txt"));
        var badge = FindBadge(deletedRow, (Style)window.FindResource("StateBadge"))!;
        Check(((SolidColorBrush)badge.Background).Color == Color.FromRgb(255, 235, 229), "Local deletion has its own state color");
        model.SelectedState = "本地已删除";
        Check(model.VisibleEntryCount == 2, "Local-deletion filtering selects scheduled file and directory deletions");
        model.SelectedState = "全部状态";
        menu = OpenMenu("deleted-directory");
        Check(Item(menu, "commit").IsEnabled && Item(menu, "revert").IsEnabled && !Item(menu, "delete").IsEnabled
            && !Item(menu, "move").IsEnabled, "Deleted tree nodes retain submit/revert while preventing repeat deletion or movement");
        CloseMenu(menu);
        model.ViewMode = MainViewModel.CardViewName;
        render(window, Path.Combine(output, "local-deleted-cards.png"), 1360, 780);
        await operations.RevertAsync([Path.Combine(local, "deleted-directory")], CancellationToken.None);
        await model.RefreshPathsAsync([Path.Combine(local, "deleted-directory")], CancellationToken.None);
        Check(model.GetDirectoryEntry("deleted-directory")!.State == FileState.Equal
            && File.Exists(Path.Combine(local, "deleted-directory", "child.txt")), "Reverting restores the deleted directory and its normal state");
        model.SaveSession();
        using var restored = new MainViewModel(UserSettings.Load(settings.StorageDirectory), service);
        await restored.RestoreLastSessionAsync(CancellationToken.None);
        Check(restored.Entries.Single(entry => entry.Name == "delete-file.txt").State == FileState.LocalDeleted,
            "The new state survives session persistence and reopening");

        var onlineWindow = new MainWindow(new UserSettings(Path.Combine(root, "online-settings")) { CloseBehavior = WindowCloseBehavior.Exit }, service);
        using var online = (MainViewModel)onlineWindow.DataContext;
        await online.OpenAsync(url, false, CancellationToken.None);
        render(onlineWindow, Path.Combine(output, "online-tree-actions.png"), 1000, 620);
        var onlineTree = (TreeView)onlineWindow.FindName("DirectoryTree");
        var onlineRoot = (TreeViewItem)onlineTree.ItemContainerGenerator.ContainerFromItem(online.Roots.Single());
        RightClick(onlineTree, onlineRoot);
        onlineTree.ContextMenu.PlacementTarget = onlineTree;
        onlineTree.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(Item(onlineTree.ContextMenu, "export").IsEnabled && Item(onlineTree.ContextMenu, "log").IsEnabled
            && !Item(onlineTree.ContextMenu, "commit").IsEnabled && !Item(onlineTree.ContextMenu, "delete").IsEnabled,
            "Online tree menus expose server reads while disabling local writes");
        Console.WriteLine("PASS: scheduled deletion labels/colors/filter/summaries/persistence, tree context targeting without reads, actual Add/Update/scoped refresh, root/deleted/online availability and selection restoration.");

        ContextMenu OpenMenu(string relative)
        {
            var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(model.Roots.Single());
            var item = relative.Length == 0 ? rootItem : (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromItem(model.FindNode(relative));
            RightClick(tree, item);
            tree.ContextMenu.PlacementTarget = tree;
            tree.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return tree.ContextMenu;
        }
    }

    private static void RightClick(UIElement control, DependencyObject source) => control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = source });
    private static MenuItem Item(ContextMenu menu, string operation) => menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, operation));
    private static void CloseMenu(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
    private static Border? FindBadge(DependencyObject parent, Style style)
    {
        if (parent is Border border && border.Style == style) return border;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindBadge(VisualTreeHelper.GetChild(parent, i), style) is { } found) return found;
        return null;
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Check(condition(), "Tree action did not complete within the test timeout");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class CountingRepository(IRepositoryService inner) : IRepositoryService
    {
        public int Reads { get; private set; }
        public List<DirectoryRefresh> Scopes { get; } = [];
        public Task<RepositorySession> OpenWorkingCopyAsync(string path, CancellationToken token) => inner.OpenWorkingCopyAsync(path, token);
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
        public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token) { Reads++; return inner.RefreshAsync(session, token); }
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
        { Reads++; Scopes.Add(scope); return inner.RefreshDirectoryAsync(session, scope, token); }
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relative, CancellationToken token)
        { Reads++; return inner.ReadDirectoryAsync(session, snapshot, relative, token); }
    }
}
