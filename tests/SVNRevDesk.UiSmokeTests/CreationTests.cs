using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class CreationTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var fixture = Path.Combine(output, "creation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var repositoryPath = Path.Combine(fixture, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start) ?? throw new InvalidOperationException("Cannot start svnadmin"))
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var local = Path.Combine(fixture, "中文 @ 工作副本");
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var token = CancellationToken.None;
        await operations.CheckoutAsync(url, local, false, token);
        Directory.CreateDirectory(Path.Combine(local, "未访问目录"));
        File.WriteAllText(Path.Combine(local, "未访问目录", "原有.txt"), "server contents");
        File.WriteAllText(Path.Combine(local, "原有.txt"), "server contents");
        await operations.AddAsync([Path.Combine(local, "未访问目录"), Path.Combine(local, "原有.txt")], token);
        await operations.CommitAsync([local], "Creation UI fixture", token);

        var service = new CountingRepository(new RepositoryService(runner));
        var settings = new UserSettings(Path.Combine(fixture, "settings"));
        var window = new MainWindow(settings, service);
        using var model = (MainViewModel)window.DataContext;
        await model.RunAsync("Open creation fixture", ct => model.OpenAsync(local, true, ct));
        var grid = (DataGrid)window.FindName("FileGrid");
        var cards = (ListBox)window.FindName("FileCards");
        var tree = (TreeView)window.FindName("DirectoryTree");
        render(window, Path.Combine(output, "creation-before.png"), 1360, 780);
        Check(model.CanCreateInDirectory("") && model.CanCreateInDirectory("未访问目录")
            && !model.CanCreateInDirectory("missing") && !model.CanCreateInDirectory(".svn")
            && !model.CanCreateInDirectory("../escape"), "Creation availability requires an existing mapped directory outside metadata");

        var folder = model.Entries.Single(entry => entry.IsDirectory);
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(folder);
        RightClick(grid, row);
        var menu = OpenMenu(grid);
        Check(menu.IsEnabled && menu.ToolTip.ToString()!.EndsWith(Path.Combine(local, "未访问目录"), StringComparison.Ordinal),
            "Right-clicking a list folder creates inside that folder");
        Check(menu.Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(NewItemOptions.Standard.Select(item => item.Label)),
            "Creation menu contains only direct creation types, without template entries");
        RightClick(grid, grid);
        menu = OpenMenu(grid);
        Check(grid.SelectedItems.Count == 0 && menu.ToolTip.ToString()!.EndsWith(local, StringComparison.Ordinal),
            "Right-clicking blank list clears stale selection and targets current directory");
        model.ViewMode = MainViewModel.CardViewName;
        render(window, Path.Combine(output, "creation-before-cards.png"), 1360, 780);
        var card = (ListBoxItem)cards.ItemContainerGenerator.ContainerFromItem(folder);
        RightClick(cards, card);
        Check(OpenMenu(cards).ToolTip.ToString()!.EndsWith(Path.Combine(local, "未访问目录"), StringComparison.Ordinal),
            "Card folder menu uses the pointed folder");
        var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(model.Roots[0]);
        var folderItem = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromItem(model.FindNode("未访问目录"));
        RightClick(tree, folderItem);
        Check(OpenMenu(tree).IsEnabled && model.CurrentRelative == "" && service.DirectoryReads == 1,
            "Tree right-click enables creation without navigating or reading that server directory");

        var option = NewItemOptions.Standard.Single(item => item.DefaultName.EndsWith(".json", StringComparison.Ordinal));
        var dialog = new NewItemWindow(null, local, option);
        Check(dialog.AddToSvn && dialog.ItemName == option.DefaultName, "New-item dialog defaults SVN add on and provides a full filename");
        var nameBox = (TextBox)dialog.FindName("NameBox");
        nameBox.Text = "../bad.json";
        Check(!((Button)dialog.FindName("CreateButton")).IsEnabled, "Invalid names disable Create with inline feedback");
        nameBox.Text = "产品配置.json";
        render(dialog, Path.Combine(output, "new-item-dialog.png"), 580, 320);
        service.DisallowServerReads = true;
        foreach (var officeOption in NewItemOptions.Standard.Where(item => item.Kind is NewItemKind.WordDocument or NewItemKind.Spreadsheet or NewItemKind.Presentation))
        {
            await model.RunAsync("Generate Office", ct => model.CreateLocalItemAsync("", officeOption.Request(officeOption.DefaultName), true, ct));
            var createdOffice = model.Entries.Single(entry => entry.Name == officeOption.DefaultName);
            var officePath = Path.Combine(local, officeOption.DefaultName);
            using var package = ZipFile.OpenRead(officePath);
            var mainPart = Path.GetExtension(officePath) switch { ".docx" => "word/document.xml", ".xlsx" => "xl/workbook.xml", _ => "ppt/presentation.xml" };
            Check(package.GetEntry(mainPart) != null && model.Snapshot!.Statuses[officePath].Item == "added"
                && grid.SelectedItems.Cast<BrowserEntry>().Single().Name == officeOption.DefaultName,
                "Generated Office file is a valid package, scheduled and selected: " + officeOption.Label);
        }
        var otherOption = NewItemOptions.Standard.Single(item => item.Kind == NewItemKind.EmptyFile);
        var otherDialog = new NewItemWindow(null, local, otherOption);
        ((TextBox)otherDialog.FindName("NameBox")).Text = "输入后缀.docx";
        Check(((TextBlock)otherDialog.FindName("TypeDescription")).Text.Contains("空白 Office 文档", StringComparison.Ordinal),
            "Other-file dialog explains automatic Office generation for a recognized extension");
        await model.RunAsync("Create by extension", ct => model.CreateLocalItemAsync("", otherOption.Request(otherDialog.ItemName), false, ct));
        using (var package = ZipFile.OpenRead(Path.Combine(local, "输入后缀.docx")))
            Check(package.GetEntry("word/document.xml") != null, "Entering a docx extension in Other-file generates an actual Word document");
        var wordDialog = new NewItemWindow(null, local, NewItemOptions.Standard.Single(item => item.Kind == NewItemKind.WordDocument));
        ((TextBox)wordDialog.FindName("NameBox")).Text = "错误后缀.txt";
        Check(!((Button)wordDialog.FindName("CreateButton")).IsEnabled, "Office dialog validates that its filename matches the selected document type");
        var checkedAt = model.CurrentDirectorySnapshot!.CheckedAt;
        var revision = model.CurrentDirectorySnapshot.Revision;
        var lastFull = model.LastFullRefreshAt;
        model.FilterText = "not-visible";
        model.SelectedState = "一致";
        await model.RunAsync("Create JSON", ct => model.CreateLocalItemAsync("", option.Request(dialog.ItemName), true, ct));
        var created = model.Entries.Single(entry => entry.Name == "产品配置.json");
        Check(created.State == FileState.LocalAdded && model.Snapshot!.Statuses[created.LocalPath!].Item == "added"
            && grid.SelectedItems.Cast<BrowserEntry>().Single().RelativePath == created.RelativePath
            && cards.SelectedItems.Cast<BrowserEntry>().Single().RelativePath == created.RelativePath
            && model.EntriesView.Contains(created), "Created item is visible and selected in both views even with previous filters");
        Check(model.CurrentDirectorySnapshot.CheckedAt == checkedAt && model.CurrentDirectorySnapshot.Revision == revision
            && model.LastFullRefreshAt == lastFull && service.DirectoryReads == 1 && service.FullChecks == 1 && service.ScopedChecks == 0,
            "Creation publishes local state without server reads or changing server check timestamps");
        var original = model.Entries.Single(entry => entry.Name == "原有.txt");
        Check(original.State == FileState.Equal && original.ServerRevision == revision, "Creating an item retains cached server entries and revisions");

        await model.RunAsync("Create without add", ct => model.CreateLocalItemAsync("", new("不添加.test", NewItemKind.EmptyFile), false, ct));
        Check(model.Snapshot!.Statuses[Path.Combine(local, "不添加.test")].Item == "unversioned", "Unchecked SVN option leaves creation unversioned");
        var duplicateRejected = false;
        try { await model.RunAsync("Duplicate", ct => model.CreateLocalItemAsync("", new("原有.txt", NewItemKind.EmptyFile), false, ct)); }
        catch (IOException) { duplicateRejected = true; }
        Check(duplicateRejected && File.ReadAllText(Path.Combine(local, "原有.txt")) == "server contents", "Cached server names cannot be overwritten by creation");
        File.Delete(Path.Combine(local, "原有.txt"));
        duplicateRejected = false;
        try { await model.RunAsync("Missing remote duplicate", ct => model.CreateLocalItemAsync("", new("原有.txt", NewItemKind.EmptyFile), false, ct)); }
        catch (IOException) { duplicateRejected = true; }
        Check(duplicateRejected && !File.Exists(Path.Combine(local, "原有.txt")), "Server-cache name protection also works when the file is absent locally");
        await operations.SetPropertyAsync(local, "svn:ignore", "*.skip", token);
        await model.RunAsync("Create ignored file", ct => model.CreateLocalItemAsync("", new("忽略.skip", NewItemKind.EmptyFile), false, ct));
        Check(model.Entries.Single(entry => entry.Name == "忽略.skip").Detail.Contains("忽略", StringComparison.Ordinal)
            && grid.SelectedItems.Cast<BrowserEntry>().Single().Name == "忽略.skip",
            "A newly created ignored file remains visible and explains why it is not versioned");

        model.ViewMode = MainViewModel.DetailsViewName;
        render(window, Path.Combine(output, "creation-list.png"), 1360, 780);
        model.ViewMode = MainViewModel.CardViewName;
        render(window, Path.Combine(output, "creation-cards.png"), 1360, 780);
        await model.RunAsync("Create in unvisited folder", ct => model.CreateLocalItemAsync("未访问目录", new("子文件.txt", NewItemKind.Text), true, ct));
        Check(model.CurrentRelative == "未访问目录" && model.DirectoryCacheText.StartsWith("本地目录预览", StringComparison.Ordinal)
            && model.Entries.Single(entry => entry.Name == "原有.txt").State == FileState.Unchecked
            && model.Entries.Single(entry => entry.Name == "子文件.txt").State == FileState.LocalAdded,
            "Unvisited directory immediately shows local files without falsely reporting remote deletion");
        Check(model.FindNode("")!.Summary.Contains("本地新增", StringComparison.Ordinal), "Tree ancestors immediately show the new local difference");
        model.SaveSession();
        var saved = new BrowsingCacheStore(settings.CacheDirectory).Load(local, true)!;
        Check(!saved.Directories["未访问目录"].HasServerListing && saved.Directories[""].HasServerListing,
            "Disk cache distinguishes local previews from complete server lists");
        service.DisallowServerReads = false;
        await model.BrowseDirectoryAsync("");
        await model.BrowseDirectoryAsync("未访问目录");
        Check(service.ScopedChecks == 1 && service.DirectoryReads == 2
            && model.Entries.Single(entry => entry.Name == "原有.txt").State == FileState.Equal,
            "A later user visit completes a local preview with server information");

        service.DisallowServerReads = true;
        await model.RunAsync("Create folder", ct => model.CreateLocalItemAsync("未访问目录", new("新目录", NewItemKind.Folder), true, ct));
        await model.RunAsync("Create child", ct => model.CreateLocalItemAsync("未访问目录/新目录", new("child.txt", NewItemKind.Text), true, ct));
        Check(model.CurrentRelative == "未访问目录/新目录" && model.Entries.Single().State == FileState.LocalAdded,
            "A newly created folder accepts children immediately without server information");
        var head = (await new RepositoryService(runner).RefreshAsync(model.Session!, token)).Revision;
        Check(head == revision, "Creation never auto-commits");
        service.DisallowServerReads = false;
        await model.BrowseDirectoryAsync("");
        var deleteSelection = model.Entries.Where(entry => entry.Name is "输入后缀.docx" or "产品配置.json").ToList();
        Check(deleteSelection.Count == 2 && deleteSelection.All(entry => entry.State == FileState.LocalAdded),
            "Unversioned and scheduled Office/text creations are both displayed as local added");
        var checksBeforeDelete = service.ScopedChecks;
        var fullChecksBeforeDelete = service.FullChecks;
        var actions = new ClientActions(window, model);
        await model.RunAsync("Delete newly created files", ct => actions.ExecuteAsync("delete", deleteSelection, ct));
        Check(deleteSelection.All(entry => !File.Exists(entry.LocalPath))
            && !model.Entries.Any(entry => deleteSelection.Any(deleted => deleted.Name == entry.Name))
            && !grid.Items.Cast<BrowserEntry>().Any(entry => deleteSelection.Any(deleted => deleted.Name == entry.Name))
            && !cards.Items.Cast<BrowserEntry>().Any(entry => deleteSelection.Any(deleted => deleted.Name == entry.Name)),
            "Actual delete workflow removes both new-file kinds from disk and both WPF views");
        Check(service.ScopedChecks == checksBeforeDelete + 1 && service.FullChecks == fullChecksBeforeDelete
            && model.LastFullRefreshAt == lastFull
            && ((FrameworkElement)window.FindName("OperationProgress")).Visibility == Visibility.Collapsed
            && deleteSelection.All(entry => Directory.EnumerateFiles(settings.BackupDirectory, entry.Name, SearchOption.AllDirectories).Any()),
            "New-file deletion retains backups, refreshes only the containing directory and finishes its progress");
        render(window, Path.Combine(output, "creation-after-delete.png"), 1360, 780);
        var nested = Path.Combine(local, "嵌套工作副本");
        await operations.CheckoutAsync(url, nested, false, token);
        Check(!model.CanCreateInDirectory("嵌套工作副本"), "Nested working copies cannot be used as the current session's creation target");

        var watcher = new LocalCreationChanges();
        var watched = Path.Combine(local, "watcher.txt");
        watcher.Begin(watched);
        Check(watcher.ShouldIgnore(watched), "Watcher suppresses the active creation");
        File.WriteAllText(watched, "initial");
        watcher.Complete(watched);
        Check(watcher.ShouldIgnore(watched) && watcher.ShouldIgnore(Path.Combine(local, ".svnclient-new-test")),
            "Delayed creation and staging notifications do not query the server");
        File.AppendAllText(watched, "external edit");
        Check(!watcher.ShouldIgnore(watched), "A later real edit still triggers the normal refresh");
        await VerifyAddFailureAsync(fixture);
        using var online = new MainViewModel(new UserSettings(Path.Combine(fixture, "online-settings")), service);
        service.DisallowServerReads = false;
        await online.OpenAsync(url, false, token);
        Check(!online.CanCreateInDirectory(""), "Pure online browsing cannot create local items");
        Console.WriteLine("PASS: direct creation menus/targets without template entries, name validation, SVN add choice, Office generation, immediate selection, unvisited previews, no server reads/commits, cache flags, watcher suppression and retained items on add failure.");
    }

    private static async Task VerifyAddFailureAsync(string fixture)
    {
        var fakeRoot = Path.Combine(fixture, "plain-directory");
        Directory.CreateDirectory(fakeRoot);
        var window = new MainWindow(new UserSettings(Path.Combine(fixture, "failure-settings")), new BrowsingTests.ControlledRepository(fakeRoot));
        using var model = (MainViewModel)window.DataContext;
        await model.OpenAsync(fakeRoot, true, CancellationToken.None);
        var failed = false;
        try { await model.RunAsync("Add fails", ct => model.CreateLocalItemAsync("", new("保留.txt", NewItemKind.Text, "retain me"), true, ct)); }
        catch (InvalidOperationException ex) { failed = ex.Message.Contains("项目已创建并保留", StringComparison.Ordinal); }
        Check(failed && File.ReadAllText(Path.Combine(fakeRoot, "保留.txt")) == "retain me"
            && model.Entries.Any(entry => entry.Name == "保留.txt"), "SVN add failure retains and displays the successfully created file");
    }

    private static void RightClick(UIElement control, DependencyObject source) => control.RaiseEvent(
        new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = source });

    private static MenuItem OpenMenu(Control control)
    {
        control.ContextMenu.PlacementTarget = control;
        control.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        return control.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "new-item"));
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class CountingRepository(IRepositoryService inner) : IRepositoryService
    {
        public int FullChecks { get; private set; }
        public int ScopedChecks { get; private set; }
        public int DirectoryReads { get; private set; }
        public bool DisallowServerReads { get; set; }
        public Task<RepositorySession> OpenWorkingCopyAsync(string directory, CancellationToken token) => inner.OpenWorkingCopyAsync(directory, token);
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
        private void CheckRead() { if (DisallowServerReads) throw new InvalidOperationException("Creation must not read the server"); }
        public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token)
        { CheckRead(); FullChecks++; return inner.RefreshAsync(session, token); }
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
        { CheckRead(); ScopedChecks++; return inner.RefreshDirectoryAsync(session, scope, token); }
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relativePath, CancellationToken token)
        { CheckRead(); DirectoryReads++; return inner.ReadDirectoryAsync(session, snapshot, relativePath, token); }
    }
}
