using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static partial class FileManagementUiTests
{
    private static async Task SearchResultActions(Window owner, string output)
    {
        var first = new BrowserEntry("item-a.txt", "docs/item-a.txt", "https://svn.example.test/docs/item-a.txt", null,
            false, FileState.Unchecked, null, null, 1, 12, "alice", DateTimeOffset.Now, null, "", "");
        var second = first with { Name = "item-b.txt", RelativePath = "docs/item-b.txt", Url = "https://svn.example.test/docs/item-b.txt" };
        var folder = first with { Name = "item-folder", RelativePath = "docs/item-folder", IsDirectory = true };
        RepositorySearchWindow Create() => new(owner, "https://svn.example.test/docs", "", async (_, _, _, progress, token) =>
        {
            progress.Report(new("本地索引", 0, [first, second, folder]));
            await Task.Delay(30, token);
            return new RepositorySearchResult(1, 0, 3, false);
        }, CancellationToken.None);
        var calls = new List<(string Operation, BrowserEntry Entry)>();
        var fail = false; var delay = false; CancellationToken actionToken = default;
        var search = Create();
        search.ExecuteResultAction = async (operation, entry, token) =>
        {
            calls.Add((operation, entry)); actionToken = token;
            if (fail) throw new IOException("open failure fixture");
            await Task.Delay(delay ? 30000 : 15, token);
        };
        await Drive(search, async () =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "item"; Click(search, "StartSearch");
            var grid = Find<DataGrid>(search, "SearchResults"); await Until(() => grid.Items.Count == 3);
            grid.SelectedItem = first; DoubleClickResult(grid, first);
            await Until(() => calls.Count == 1 && Find<Button>(search, "OpenResult").IsEnabled);
            Check(calls[0] == ("open", first) && search.IsVisible && search.SelectedResult == null,
                "Double-click opens the selected file and keeps the search window and results available");
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid)!, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Until(() => calls.Count == 2 && Find<Button>(search, "OpenResult").IsEnabled);
            Check(calls[1] == ("open", first), "Enter on a search result uses the same open action");
            RightClickResult(grid, second);
            var menu = grid.ContextMenu!; menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            MenuItem Item(string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
            Check(Equals(grid.SelectedItem, second) && Item("OpenSearchResult").IsEnabled && Item("LocateSearchResult").IsEnabled
                && !Item("ShowSearchResultInExplorer").IsEnabled && !Item("CopySearchLocalPath").IsEnabled && Item("CopySearchUrl").IsEnabled,
                "Right-click selects its own row and disables local-only actions for online results");
            Item("OpenSearchResult").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => calls.Count == 3 && Find<Button>(search, "OpenResult").IsEnabled);
            Check(calls[2] == ("open", second), "Context-menu Open acts on the right-clicked result rather than the previous selection");
            fail = true; Click(search, "OpenResult"); await Until(() => Find<TextBlock>(search, "SearchStatus").Text.Contains("open failure fixture"));
            Check(search.IsVisible && Find<Button>(search, "OpenResult").IsEnabled && grid.Items.Count == 3,
                "An open failure retains results and allows retry without closing search");
            fail = false; delay = true; Click(search, "OpenResult"); await Until(() => calls.Count == 5);
            DoubleClickResult(grid, second); Click(search, "OpenResult");
            Check(calls.Count == 5 && !Find<TextBox>(search, "SearchQuery").IsEnabled, "An outstanding open cannot be launched twice or retargeted by a new search");
            Click(search, "StopSearch"); await Until(() => Find<Button>(search, "OpenResult").IsEnabled);
            Check(actionToken.IsCancellationRequested && Find<TextBlock>(search, "SearchStatus").Text.Contains("已停止"), "Stop cancels the outstanding result action");
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = grid });
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Check(grid.SelectedItem == null && menu.Items.OfType<MenuItem>().All(item => !item.IsEnabled), "Right-clicking empty result space cannot operate on a stale selection");
            grid.SelectedItem = folder; DoubleClickResult(grid, folder);
        });
        Check(search.SelectedResult == folder && search.SelectedAction == SearchResultAction.Open, "Folder double-click requests opening the folder, separately from locating its parent");
        search = Create();
        await Drive(search, async () =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "item"; Click(search, "StartSearch");
            var grid = Find<DataGrid>(search, "SearchResults"); await Until(() => grid.Items.Count == 3);
            RightClickResult(grid, first); grid.ContextMenu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            grid.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "LocateSearchResult").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        });
        Check(search.SelectedResult == first && search.SelectedAction == SearchResultAction.Locate, "Context-menu Locate returns the exact target with a distinct navigation action");
        search = Create();
        search.ExecuteResultAction = async (_, _, token) => { actionToken = token; await Task.Delay(30000, token); };
        actionToken = default;
        await Drive(search, async () =>
        {
            Find<TextBox>(search, "SearchQuery").Text = "item"; Click(search, "StartSearch");
            var grid = Find<DataGrid>(search, "SearchResults"); await Until(() => grid.Items.Count == 3);
            grid.SelectedItem = first; Click(search, "OpenResult"); await Until(() => actionToken.CanBeCanceled); search.Close();
        });
        Check(actionToken.IsCancellationRequested && search.SelectedResult == null, "Closing search cancels an in-flight open without requesting navigation");
        await Task.Delay(30);
    }

    private static void DoubleClickResult(DataGrid grid, BrowserEntry entry)
    {
        grid.ScrollIntoView(entry); grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(entry);
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
    }

    private static void RightClickResult(DataGrid grid, BrowserEntry entry)
    {
        grid.ScrollIntoView(entry); grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(entry);
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = row });
    }

    private static async Task RealSearchOpening(MainWindow owner, MainViewModel model, SvnOperations operations, string wc, string output)
    {
        var launched = new List<string>();
        var actions = new ClientActions(owner, model, launched.Add);
        var file = (await IndexMatches(model.GetSearchIndex(), "ready-report-a")).Single();
        var commands = 0; void Count(string _) => commands++;
        model.Runner.CommandCompleted += Count;
        await model.RunAsync("Open local search result", token => actions.OpenSearchResultAsync(file, token));
        model.Runner.CommandCompleted -= Count;
        Check(launched.Single() == file.LocalPath && commands == 0, "An existing local search file opens directly without SVN or changing the browser location");

        using var online = new MainViewModel(new UserSettings(Path.Combine(output, "online-open-settings")));
        await online.RunAsync("Open online fixture", token => online.OpenAsync(model.Session!.Url, false, token));
        var oldRevision = online.Snapshot!.Revision;
        var remoteDirectory = Path.Combine(wc, "online-target"); Directory.CreateDirectory(remoteDirectory);
        var remoteFile = Path.Combine(remoteDirectory, "new-open.txt"); File.WriteAllText(remoteFile, "newer than main directory snapshot");
        await operations.AddAsync([remoteDirectory], CancellationToken.None); await operations.CommitAsync([remoteDirectory], "new indexed file", CancellationToken.None);
        var index = online.GetSearchIndex(); await index.RefreshAsync();
        var entry = (await IndexMatches(index, "online-target/new-open")).Single();
        var onlineActions = new ClientActions(owner, online, launched.Add);
        await online.RunAsync("Open online search result", token => onlineActions.OpenSearchResultAsync(entry with { Url = model.Session!.Url + "/stale-url" }, token));
        Check(File.ReadAllText(launched.Last()) == "newer than main directory snapshot" && (File.GetAttributes(launched.Last()) & FileAttributes.ReadOnly) != 0
            && online.CurrentRelative == "" && online.Snapshot.Revision == oldRevision,
            "Online search revalidates the result URL and opens a read-only file at its own fresh revision without navigating the main browser");
        var opened = launched.Count;
        await operations.DeleteAsync([remoteFile], CancellationToken.None); await operations.CommitAsync([remoteFile], "remove indexed file", CancellationToken.None);
        var rejected = false;
        try { await online.RunAsync("Open stale result", token => onlineActions.OpenSearchResultAsync(entry, token)); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && launched.Count == opened, "A deleted cached search result is rejected before launching an unrelated or stale file");

        remoteFile = Path.Combine(remoteDirectory, "view-open.txt"); File.WriteAllText(remoteFile, "sparse view content");
        await operations.AddAsync([remoteFile], CancellationToken.None); await operations.CommitAsync([remoteFile], "sparse open file", CancellationToken.None);
        var sparsePath = Path.Combine(output, "open-sparse"); await operations.CheckoutAsync(model.Session!.Url, sparsePath, true, CancellationToken.None);
        using var sparse = new MainViewModel(new UserSettings(Path.Combine(output, "sparse-open-settings")));
        await sparse.RunAsync("Open sparse fixture", token => sparse.OpenAsync(sparsePath, true, token));
        await sparse.GetSearchIndex().RefreshAsync();
        entry = (await IndexMatches(sparse.GetSearchIndex(), "online-target/view-open")).Single();
        var sparseActions = new ClientActions(owner, sparse, launched.Add);
        var choice = DriveNext<ChoiceDialog>(owner, dialog =>
        {
            Descendants<Button>(dialog).Single(button => Equals(button.Content, "只查看服务器版本（临时文件）")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });
        await sparse.RunAsync("View sparse search result", token => sparseActions.OpenSearchResultAsync(entry, token)); await choice;
        Check(File.ReadAllText(launched.Last()) == "sparse view content" && !File.Exists(entry.LocalPath)
            && !Directory.Exists(Path.Combine(sparsePath, "online-target")), "Undownloaded sparse results retain the existing version chooser and can be viewed without downloading into the working copy");
    }
}
