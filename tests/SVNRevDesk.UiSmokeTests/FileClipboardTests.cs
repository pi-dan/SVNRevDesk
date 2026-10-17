using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class FileClipboardTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "clipboard-fixture");
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
        var local = Path.Combine(root, "中文 @ 工作副本");
        var source = Path.Combine(root, "资源管理器来源");
        Directory.CreateDirectory(source);
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        await operations.CheckoutAsync(url, local, false, CancellationToken.None);
        File.WriteAllText(Path.Combine(local, "tracked.txt"), "server contents");
        Directory.CreateDirectory(Path.Combine(local, "已有目录"));
        File.WriteAllText(Path.Combine(local, "已有目录", "child.txt"), "server child");
        await operations.AddAsync([Path.Combine(local, "tracked.txt"), Path.Combine(local, "已有目录")], CancellationToken.None);
        await operations.CommitAsync([local], "Clipboard fixture", CancellationToken.None);

        var service = new CountingRepository(new RepositoryService(runner));
        var settings = new UserSettings(Path.Combine(root, "settings")) { CloseBehavior = WindowCloseBehavior.Exit };
        var window = new MainWindow(settings, service);
        using var model = (MainViewModel)window.DataContext;
        await model.OpenAsync(local, true, CancellationToken.None);
        await model.BrowseDirectoryAsync("已有目录");
        await model.BrowseDirectoryAsync("");
        var checkedAt = model.DirectoryCacheText;
        var revision = model.Snapshot!.Revision;
        var lastFull = model.LastFullRefreshAt;
        var originalReads = service.Reads;
        service.DisallowServerReads = true;
        var grid = (DataGrid)window.FindName("FileGrid");
        var cards = (ListBox)window.FindName("FileCards");
        var tree = (TreeView)window.FindName("DirectoryTree");
        var keyFilter = typeof(MainWindow).GetMethod("IsFileClipboardKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var browser in new DependencyObject[] { grid, cards, tree })
            Check((bool)keyFilter.Invoke(window, [Key.C, ModifierKeys.Control, browser])!
                && (bool)keyFilter.Invoke(window, [Key.V, ModifierKeys.Control, browser])!, "Ctrl+C/V are routed from each browser surface");
        Check(!(bool)keyFilter.Invoke(window, [Key.C, ModifierKeys.Control, window.FindName("NameFilterBox")])!
            && !(bool)keyFilter.Invoke(window, [Key.V, ModifierKeys.Control | ModifierKeys.Shift, grid])!, "Text inputs and other modifier combinations retain their normal handling");
        Check((bool)keyFilter.Invoke(window, [Key.V, ModifierKeys.Control, window])!
            && (bool)keyFilter.Invoke(window, [Key.V, ModifierKeys.Control, window.FindName("UpdateSelectionButton")])!,
            "Pasting remains available after navigation or when an empty list has no focused file row");
        foreach (var input in new DependencyObject[] { (TextBox)window.FindName("NameFilterBox"), (ComboBox)window.FindName("AddressBox") })
            Check(!(bool)keyFilter.Invoke(window, [Key.V, ModifierKeys.Control, input])!, "Text and selection inputs do not trigger file paste");
        var originalView = model.ViewMode;
        model.FilterText = "__no_clipboard_fixture_match__";
        foreach (var browser in new Control[] { grid, cards })
        {
            model.ViewMode = browser == cards ? MainViewModel.CardViewName : originalView;
            render(window, Path.Combine(output, browser == cards ? "paste-empty-cards.png" : "paste-empty-list.png"), 1360, 780);
            FocusManager.SetFocusedElement(window, (IInputElement)window.FindName("NameFilterBox"));
            browser.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = browser });
            Check(FocusManager.GetFocusedElement(window) == browser, "Clicking a blank file surface transfers focus away from text inputs");
        }
        model.FilterText = "";
        model.ViewMode = originalView;

        var newFile = Path.Combine(source, "新增 @ 文件.txt");
        var changed = Path.Combine(source, "tracked.txt");
        File.WriteAllText(newFile, "pasted new contents");
        File.WriteAllText(changed, "pasted replacement");
        var sourceFolder = Path.Combine(source, "新目录");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "子目录"));
        Directory.CreateDirectory(Path.Combine(sourceFolder, ".svn"));
        File.WriteAllText(Path.Combine(sourceFolder, "子目录", "new-child.txt"), "nested new content");
        File.WriteAllText(Path.Combine(sourceFolder, ".svn", "wc.db"), "must not copy");
        var explorerData = FileClipboard.CreateData([newFile, changed, sourceFolder]);
        Check(FileClipboard.ReadFiles(explorerData).SequenceEqual(new[] { newFile, changed, sourceFolder })
            && explorerData.GetData("Preferred DropEffect") is MemoryStream effect && effect.ToArray().SequenceEqual(new byte[] { 1, 0, 0, 0 }),
            "Clipboard uses Explorer FileDrop and explicit copy effect");
        var plan = LocalFileTransfer.Prepare(FileClipboard.ReadFiles(explorerData), local, local);
        await model.RunAsync("粘贴文件", ct => model.PasteLocalFilesAsync("", plan, true, ct));
        Check(File.Exists(newFile) && File.Exists(changed) && File.Exists(Path.Combine(sourceFolder, "子目录", "new-child.txt")), "Copying preserves the Explorer sources");
        Check(model.Entries.Single(entry => entry.Name == "tracked.txt").State == FileState.LocalModified
            && model.Entries.Single(entry => entry.Name == "新增 @ 文件.txt").State == FileState.LocalAdded
            && model.Snapshot!.Statuses[Path.Combine(local, "新增 @ 文件.txt")].Item == "unversioned", "Paste immediately displays replacements as modified and new files as unversioned");
        Check(service.Reads == originalReads && model.DirectoryCacheText == checkedAt && model.LastFullRefreshAt == lastFull
            && model.Snapshot.Revision == revision, "Paste reads only local status and preserves server checks and cache timestamps");
        Check(!model.IsBusy && ((FrameworkElement)window.FindName("OperationProgress")).Visibility == Visibility.Collapsed
            && grid.SelectedItems.Count == 1, "Paste completes progress and selects a pasted item");
        Check(Directory.EnumerateFiles(settings.BackupDirectory, "tracked.txt", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path) == "server contents"), "Replacing a tracked file retains a portable backup");
        var backupCount = Directory.EnumerateFiles(settings.BackupDirectory, "*", SearchOption.AllDirectories).Count();
        File.WriteAllText(changed, "replacement without backup");
        var directPlan = LocalFileTransfer.Prepare([changed], local, local);
        await model.RunAsync("粘贴文件", ct => model.PasteLocalFilesAsync("", directPlan, true, ct, backupBeforeOverwrite: false));
        Check(File.ReadAllText(Path.Combine(local, "tracked.txt")) == "replacement without backup"
            && Directory.EnumerateFiles(settings.BackupDirectory, "*", SearchOption.AllDirectories).Count() == backupCount
            && service.Reads == originalReads, "Copy overwrite can explicitly skip backups while preserving local-only refresh");
        render(window, Path.Combine(output, "clipboard-after-paste.png"), 1360, 780);
        render(window, Path.Combine(output, "clipboard-after-paste-narrow.png"), 1000, 620);
        await model.BrowseDirectoryAsync("新目录/子目录");
        Check(model.Entries.Single().Name == "new-child.txt" && model.Entries.Single().State == FileState.LocalAdded
            && service.Reads == originalReads, "New local directory trees are browsable without an invalid server request");

        var breadcrumb = (TextBlock)window.FindName("CurrentRelativePath");
        var links = breadcrumb.Inlines.OfType<Hyperlink>().ToArray();
        Check(links.Length == 2 && (string)links[0].Tag == "" && (string)links[1].Tag == "新目录"
            && new TextRange(links[1].ContentStart, links[1].ContentEnd).Text == "新目录"
            && breadcrumb.Inlines.LastInline is Run { Text: "子目录" }, "The path exposes each ancestor as a link and marks the current directory as text");
        render(window, Path.Combine(output, "breadcrumb-navigation.png"), 1360, 780);
        render(window, Path.Combine(output, "breadcrumb-navigation-narrow.png"), 1000, 620);
        await model.RunAsync("Breadcrumb busy check", _ =>
        {
            links[0].RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Check(!links[0].IsEnabled && model.CurrentRelative == "新目录/子目录", "Breadcrumb navigation is disabled during an exclusive operation");
            return Task.CompletedTask;
        });
        links[1].RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Check(model.CurrentRelative == "新目录" && model.FindNode("新目录")!.IsSelected && service.Reads == originalReads,
            "Clicking an ancestor immediately navigates to its cached directory and updates the tree without querying the server");
        breadcrumb.Inlines.OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Check(model.CurrentRelative == "" && model.FindNode("")!.IsSelected && !breadcrumb.Inlines.OfType<Hyperlink>().Any()
            && service.Reads == originalReads, "Clicking the root returns to the opened root and rebuilds the path");

        await model.BrowseDirectoryAsync("");
        IReadOnlyList<string> exported = [];
        var selection = model.Entries.Where(entry => entry.Name is "tracked.txt" or "新目录").ToArray();
        await model.RunAsync("复制文件", async ct => exported = await model.PrepareClipboardFilesAsync(selection, ct));
        Check(exported.Any(path => path == Path.Combine(local, "tracked.txt"))
            && exported.Where(Directory.Exists).All(path => !Directory.Exists(Path.Combine(path, ".svn")))
            && File.ReadAllText(Path.Combine(exported.Single(Directory.Exists), "子目录", "new-child.txt")) == "nested new content",
            "Copying out uses current local contents and clean directory snapshots");
        Check(service.Reads == originalReads, "Local copying does not query server state");
        var snapshotData = FileClipboard.CreateData(exported, selection.Select(entry => entry.LocalPath!).ToArray());
        var repeatedPlan = LocalFileTransfer.Prepare(FileClipboard.ReadFiles(snapshotData), local, local,
            originalPaths: FileClipboard.ReadOriginalPaths(snapshotData));
        Check(repeatedPlan.TopLevelPaths.All(path => Path.GetFileName(path).Contains("副本", StringComparison.Ordinal)),
            "Pasting snapshots back into their source directory creates copies for folders as well as files");
        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(model.Entries.Single(entry => entry.Name == "tracked.txt"));
        grid.ContextMenu.PlacementTarget = grid;
        grid.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        var copyItem = grid.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "file-copy");
        var pasteItem = grid.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "file-paste");
        Check(copyItem.IsEnabled && copyItem.InputGestureText == "Ctrl+C" && pasteItem.InputGestureText == "Ctrl+V",
            "Context menus expose file copying and shortcut hints");
        model.ViewMode = MainViewModel.CardViewName;
        render(window, Path.Combine(output, "clipboard-cards.png"), 1360, 780);

        await FileDragDropTests.RunAsync(window, source, output, render);
        await FileDragOutTests.RunAsync(window, output, render);
        Check(service.Reads == originalReads && model.LastFullRefreshAt == lastFull && model.Snapshot.Revision == revision
            && model.DirectoryCacheText == checkedAt, "Drag-and-drop preserves server checks, revisions and cache timestamps");

        service.DisallowServerReads = false;
        var onlineWindow = new MainWindow(new UserSettings(Path.Combine(root, "online-settings")), service);
        using var online = (MainViewModel)onlineWindow.DataContext;
        await online.OpenAsync(url, false, CancellationToken.None);
        service.DisallowServerReads = true;
        var onlineReads = service.Reads;
        await online.RunAsync("Copy online files", async ct => exported = await online.PrepareClipboardFilesAsync(online.Entries.ToArray(), ct));
        Check(exported.Count == 2 && exported.Any(path => File.Exists(path) && File.ReadAllText(path) == "server contents")
            && exported.Any(path => Directory.Exists(path) && File.Exists(Path.Combine(path, "child.txt"))), "Online copying downloads the displayed server revision for files and directories");
        Check(!online.CanCreateInDirectory("") && service.Reads == onlineReads, "Online paste requires a local working copy and copying performs no extra state refresh");
        await FileDragOutTests.VerifyOnlineAsync(onlineWindow);
        Check(service.Reads == onlineReads, "Outgoing online drag does not query server status or listings again");

        await VerifyCacheCleanupAsync(root);
        await FileMoveUiTests.RunAsync(output, render);
        Console.WriteLine("PASS: Explorer file formats/copy effect, shortcuts/text isolation, local paste states/backup/no server refresh, cached new trees, local and online copies, menus and clipboard cache retention.");
    }

    private static async Task VerifyCacheCleanupAsync(string root)
    {
        var temp = Path.Combine(root, "cache-cleanup");
        var cache = Path.Combine(temp, "Clipboard");
        var unused = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        var current = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        var unknown = Path.Combine(cache, "user-files");
        foreach (var path in new[] { unused, current, unknown })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "item.txt"), "retain if protected");
            Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-9));
        }
        await ClipboardFileCache.CleanupAsync(temp, [Path.Combine(current, "item.txt")]);
        Check(!Directory.Exists(unused) && Directory.Exists(current) && Directory.Exists(unknown),
            "Old clipboard snapshots are cleaned while live clipboard paths and unknown files are retained");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class CountingRepository(IRepositoryService inner) : IRepositoryService
    {
        public int Reads { get; private set; }
        public bool DisallowServerReads { get; set; }
        private void Count() { if (DisallowServerReads) throw new InvalidOperationException("Unexpected server read"); Reads++; }
        public Task<RepositorySession> OpenWorkingCopyAsync(string directory, CancellationToken token) => inner.OpenWorkingCopyAsync(directory, token);
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
        public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token)
        { Count(); return inner.RefreshAsync(session, token); }
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
        { Count(); return inner.RefreshDirectoryAsync(session, scope, token); }
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relative, CancellationToken token)
        { Count(); return inner.ReadDirectoryAsync(session, snapshot, relative, token); }
    }
}
