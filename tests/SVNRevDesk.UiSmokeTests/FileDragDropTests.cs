using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class FileDragDropTests
{
    public static async Task RunAsync(MainWindow window, string source, string output, Action<Window, string, int, int> render)
    {
        var model = (MainViewModel)window.DataContext;
        var grid = (DataGrid)window.FindName("FileGrid");
        var cards = (ListBox)window.FindName("FileCards");
        var tree = (TreeView)window.FindName("DirectoryTree");
        var local = model.Session!.LocalRoot!;
        var originalView = model.ViewMode;
        var clipboardSequence = SVNRevDesk.Desktop.Services.FileClipboard.SequenceNumber;
        var file = Path.Combine(source, "拖入 中文 @ 文件.txt");
        File.WriteAllText(file, "Explorer drop contents");
        var data = new DataObject(DataFormats.FileDrop, new[] { file });
        data.SetData("Preferred DropEffect", new MemoryStream([2, 0, 0, 0]));
        Check(grid.AllowDrop && cards.AllowDrop && tree.AllowDrop, "All file browsers register as OLE drop targets");
        model.ViewMode = MainViewModel.DetailsViewName;
        render(window, Path.Combine(output, "drag-drop-list.png"), 1360, 780);
        foreach (var surface in new Control[] { grid, cards })
        {
            Check(Raise(surface, surface, data, DragDrop.PreviewDragEnterEvent).Effects == DragDropEffects.Copy,
                "Explorer files advertise Copy over the blank browser surface");
            Check(Raise(surface, surface, new DataObject(DataFormats.UnicodeText, file), DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
                "Text data does not become a file transfer");
            Check(Raise(surface, surface, data, DragDrop.PreviewDragOverEvent, DragDropEffects.Move).Effects == DragDropEffects.None,
                "A source allowing only Move cannot be copied or accidentally deleted");
        }
        Check(Raise(tree, tree, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
            "Tree whitespace does not choose a stale or selected directory");
        await model.RunAsync("Busy drop check", _ =>
        {
            Check(Raise(grid, grid, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
                "An ongoing operation rejects incoming drops");
            return Task.CompletedTask;
        });
        var selected = grid.SelectedItems.Cast<BrowserEntry>().Select(entry => entry.RelativePath).ToArray();
        var folder = model.Entries.Single(entry => entry.Name == "已有目录");
        var folderRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(folder);
        Check(folderRow != null, "Folder row is realized");
        Check(Raise(grid, folderRow!, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.Copy
            && model.CurrentRelative == "" && grid.SelectedItems.Cast<BrowserEntry>().Select(entry => entry.RelativePath).SequenceEqual(selected),
            "Hovering a folder offers Copy without navigation or selection changes");
        await DropAsync(grid, folderRow!, data, window);
        Check(File.ReadAllText(Path.Combine(local, "已有目录", Path.GetFileName(file))) == "Explorer drop contents"
            && File.Exists(file) && !File.Exists(Path.Combine(local, Path.GetFileName(file)))
            && model.CurrentRelative == "已有目录" && model.Entries.Single(entry => entry.Name == Path.GetFileName(file)).State == FileState.LocalAdded,
            "Dropping on a folder copies into that folder, preserves the source and immediately refreshes its local state");

        await model.BrowseDirectoryAsync("");
        model.ViewMode = MainViewModel.CardViewName;
        render(window, Path.Combine(output, "drag-drop-cards.png"), 1360, 780);
        var fileCard = (ListBoxItem)cards.ItemContainerGenerator.ContainerFromItem(model.Entries.Single(entry => entry.Name == "tracked.txt"));
        await DropAsync(cards, fileCard, data, window);
        Check(File.Exists(Path.Combine(local, Path.GetFileName(file))) && model.CurrentRelative == "",
            "A file card targets the current directory rather than replacing the hovered file");

        var emptyFolder = Path.Combine(source, "拖入空文件夹");
        Directory.CreateDirectory(emptyFolder);
        var secondFile = Path.Combine(source, "多选拖入.txt");
        File.WriteAllText(secondFile, "second selected file");
        model.FilterText = "__empty_drop_surface__";
        render(window, Path.Combine(output, "drag-drop-empty.png"), 1360, 780);
        await DropAsync(cards, cards, new DataObject(DataFormats.FileDrop, new[] { emptyFolder, secondFile }), window);
        Check(Directory.Exists(Path.Combine(local, Path.GetFileName(emptyFolder)))
            && File.ReadAllText(Path.Combine(local, Path.GetFileName(secondFile))) == "second selected file" && model.FilterText == "",
            "Blank filtered browsers accept mixed multi-selection and reveal the copied result");

        var treeFile = Path.Combine(source, "目录树拖入.txt");
        File.WriteAllText(treeFile, "tree drop contents");
        tree.UpdateLayout();
        var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
        rootItem.IsExpanded = true;
        rootItem.UpdateLayout();
        var folderNode = model.FindNode("已有目录")!;
        var treeItem = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromItem(folderNode);
        Check(treeItem != null, "Tree folder is realized");
        await DropAsync(tree, treeItem!, new DataObject(DataFormats.FileDrop, new[] { treeFile }), window);
        Check(File.ReadAllText(Path.Combine(local, "已有目录", Path.GetFileName(treeFile))) == "tree drop contents"
            && File.Exists(treeFile), "Dropping on an unselected tree node uses that node as the destination");
        Check(SVNRevDesk.Desktop.Services.FileClipboard.SequenceNumber == clipboardSequence,
            "Receiving dropped files leaves the Windows clipboard unchanged");
        await model.BrowseDirectoryAsync("");
        await VerifyConflictsAsync(window, cards, file, local);
        await VerifyInternalMoveAsync(window, grid, local, Path.GetFileName(secondFile));
        model.ViewMode = originalView;
        Check(!model.IsBusy && ((FrameworkElement)window.FindName("OperationProgress")).Visibility == Visibility.Collapsed,
            "Drop completion ends progress and releases the transfer guard");

        var emptyWindow = new MainWindow(new SVNRevDesk.Desktop.Services.UserSettings(Path.Combine(output, "drop-empty-settings")));
        using var emptyModel = (MainViewModel)emptyWindow.DataContext;
        var emptyGrid = (DataGrid)emptyWindow.FindName("FileGrid");
        Check(Raise(emptyGrid, emptyGrid, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
            "Files cannot be dropped before a local working copy is opened");
        Console.WriteLine("PASS: Explorer copy drops and internal folder moves, list/card/tree destinations, invalid targets, conflict choices, clipboard preservation and local refresh.");
    }

    private static async Task VerifyInternalMoveAsync(MainWindow window, DataGrid grid, string local, string name)
    {
        var model = (MainViewModel)window.DataContext;
        model.ViewMode = MainViewModel.DetailsViewName;
        var source = Path.Combine(local, name);
        grid.UpdateLayout();
        var fileRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Entries.Single(entry => entry.Name == name));
        var folderRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Entries.Single(entry => entry.Name == "已有目录"));
        Check(fileRow != null && folderRow != null, "Internal move source and folder target are realized");
        var tokenField = typeof(MainWindow).GetField("internalDragToken", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sourcesField = typeof(MainWindow).GetField("internalDragSources", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessionField = typeof(MainWindow).GetField("internalDragSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var token = Guid.NewGuid().ToString("N");
        var data = SVNRevDesk.Desktop.Services.FileClipboard.CreateData([source]);
        data.SetData("SVNRevDesk.InternalFileMove", token);
        tokenField.SetValue(window, token);
        sourcesField.SetValue(window, new[] { source });
        sessionField.SetValue(window, model.Session);
        try
        {
            Check(Raise(grid, grid, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None
                && Raise(grid, fileRow!, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.None,
                "Internal drags cannot move onto whitespace or a file row");
            Check(Raise(grid, folderRow!, data, DragDrop.PreviewDragOverEvent).Effects == DragDropEffects.Move,
                "A folder row advertises Move for an internal drag");
            await DropAsync(grid, folderRow!, data, window, DragDropEffects.Move);
            Check(!File.Exists(source) && File.ReadAllText(Path.Combine(local, "已有目录", name)) == "second selected file"
                && model.CurrentRelative == "" && model.Entries.All(entry => entry.Name != name),
                "Dropping inside the browser moves the source while keeping the current directory open");
            await model.BrowseDirectoryAsync("已有目录");
            Check(model.Entries.Single(entry => entry.Name == name).State == FileState.LocalAdded,
                "The destination listing is refreshed without a server read");
            await model.BrowseDirectoryAsync("");
        }
        finally
        {
            tokenField.SetValue(window, null);
            sourcesField.SetValue(window, null);
            sessionField.SetValue(window, null);
        }
    }

    private static async Task VerifyConflictsAsync(MainWindow window, Control surface, string file, string local)
    {
        var model = (MainViewModel)window.DataContext;
        var destination = Path.Combine(local, Path.GetFileName(file));
        new WindowInteropHelper(window).EnsureHandle();
        foreach (var choice in new[] { "skip", "cancel", "overwrite" })
        {
            var before = File.ReadAllText(destination);
            File.WriteAllText(file, "new drop contents: " + choice);
            var answered = false;
            Exception? failure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<FileConflictWindow>()
                    .SingleOrDefault(item => item.Owner == window && item.IsVisible);
                if (dialog == null) return;
                timer.Stop();
                answered = true;
                try
                {
                    Check(dialog.Title == "拖入时存在同名文件" && !dialog.BackupBeforeOverwrite
                        && !model.IsOperationProgressVisible && File.ReadAllText(destination) == before,
                        "The drop conflict dialog waits before writing and leaves backup unchecked by default");
                    if (choice == "overwrite") ((CheckBox)dialog.FindName("BackupBeforeOverwriteBox")).IsChecked = true;
                }
                catch (Exception ex) { failure = ex; }
                if (failure != null || choice == "cancel") dialog.DialogResult = false;
                else ((Button)dialog.FindName(choice == "skip" ? "SkipButton" : "OverwriteButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            try { await DropAsync(surface, surface, new DataObject(DataFormats.FileDrop, new[] { file }), window); }
            finally { timer.Stop(); }
            if (failure != null) throw failure;
            Check(answered && File.Exists(file) && File.ReadAllText(destination) == (choice == "overwrite" ? File.ReadAllText(file) : before),
                "Drop overwrite, skip and cancellation preserve the expected source and destination contents");
            if (choice == "overwrite")
                Check(Directory.EnumerateFiles(model.Settings.BackupDirectory, Path.GetFileName(file), SearchOption.AllDirectories)
                    .Any(path => File.ReadAllText(path) == before), "A confirmed drop overwrite backs up the previous destination contents");
        }
    }

    private static DragEventArgs Raise(Control surface, DependencyObject source, IDataObject data, RoutedEvent routedEvent,
        DragDropEffects allowed = DragDropEffects.Copy | DragDropEffects.Move)
    {
        // WPF creates these args internally during OLE drag-and-drop. Exercise its real routed handlers offscreen.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [data, DragDropKeyStates.LeftMouseButton, allowed, source, new Point()], null)!;
        args.RoutedEvent = routedEvent;
        args.Source = source;
        surface.RaiseEvent(args);
        Check(args.Handled, "The preview handler consumes file drag events before child controls");
        return args;
    }

    private static async Task DropAsync(Control surface, DependencyObject source, IDataObject data, MainWindow window,
        DragDropEffects expected = DragDropEffects.Copy)
    {
        Check(Raise(surface, source, data, DragDrop.PreviewDropEvent).Effects == expected,
            "Accepted drops report the actual operation to the source application");
        var guard = typeof(MainWindow).GetField("preparingClipboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((bool)guard.GetValue(window)! && DateTime.UtcNow < deadline) await Task.Delay(25);
        Check(!(bool)guard.GetValue(window)!, "The dropped transfer completes");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
