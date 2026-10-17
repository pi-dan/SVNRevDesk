using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class FileDragOutTests
{
    public static async Task RunAsync(MainWindow window, string output, Action<Window, string, int, int> render)
    {
        var model = (MainViewModel)window.DataContext;
        var clipboardSequence = FileClipboard.SequenceNumber;
        var file = model.Entries.Single(entry => entry.Name == "tracked.txt");
        var folder = model.Entries.Single(entry => entry.Name == "已有目录");
        var fileContents = File.ReadAllText(file.LocalPath!);
        var local = await PrepareAsync(window, [file]);
        Check(FileClipboard.ReadFiles(local).SequenceEqual(new[] { file.LocalPath! }) && !FileClipboard.IsMove(local),
            "Local file drag payload uses current working files and advertises Copy");
        var mixed = await PrepareAsync(window, [file, folder]);
        var paths = FileClipboard.ReadFiles(mixed);
        var snapshot = paths.Single(Directory.Exists);
        Check(paths.Count == 2 && paths.Contains(file.LocalPath!) && snapshot != folder.LocalPath
            && File.ReadAllText(Path.Combine(snapshot, "child.txt")) == File.ReadAllText(Path.Combine(folder.LocalPath!, "child.txt"))
            && !Directory.Exists(Path.Combine(snapshot, ".svn")),
            "Multi-selection exports current local folder contents as a clean snapshot alongside local files");
        Check(FileClipboard.ReadOriginalPaths(mixed)[snapshot] == folder.LocalPath
            && Directory.Exists(folder.LocalPath) && File.ReadAllText(file.LocalPath!) == fileContents,
            "Outgoing drag retains its origins and leaves the working copy unchanged");

        var root = await PrepareAsync(window, [model.GetDirectoryEntry("")!]);
        var rootSnapshot = FileClipboard.ReadFiles(root).Single();
        Check(Directory.Exists(rootSnapshot) && !Directory.Exists(Path.Combine(rootSnapshot, ".svn"))
            && File.ReadAllText(Path.Combine(rootSnapshot, "tracked.txt")) == fileContents,
            "A tree root can be dragged as a snapshot without SVN metadata");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var stopped = false;
            try { await PrepareAsync(window, [folder], cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !model.IsBusy, "Cancelled preparation never produces a drag payload or leaves progress active");
        }
        await VerifyGesturesAsync(window, output, render, file, folder);

        Check(FileClipboard.SequenceNumber == clipboardSequence && File.ReadAllText(file.LocalPath!) == fileContents
            && Directory.Exists(snapshot) && !model.IsBusy,
            "Preparing outgoing drag preserves clipboard, sources and exported snapshot lifetime");
        Console.WriteLine("PASS: outgoing FileDrop data, local edits, mixed selection, folder/root snapshots without metadata, mouse thresholds/multi-selection/clicks, preparation cancellation and clipboard preservation.");
    }

    public static async Task VerifyOnlineAsync(MainWindow window)
    {
        var model = (MainViewModel)window.DataContext;
        var sequence = FileClipboard.SequenceNumber;
        var data = await PrepareAsync(window, model.Entries.ToArray());
        var paths = FileClipboard.ReadFiles(data);
        Check(paths.Count == 2 && paths.Any(path => File.Exists(path) && File.ReadAllText(path) == "server contents")
            && paths.Any(path => Directory.Exists(path) && File.ReadAllText(Path.Combine(path, "child.txt")) == "server child")
            && !FileClipboard.IsMove(data) && FileClipboard.SequenceNumber == sequence && !model.IsBusy,
            "Online drag downloads the displayed revisions into real FileDrop paths without touching the clipboard");
        Console.WriteLine("PASS: outgoing online file/folder preparation, cached revision download and Copy effect without extra status reads.");
    }

    private static async Task VerifyGesturesAsync(MainWindow window, string output, Action<Window, string, int, int> render,
        BrowserEntry file, BrowserEntry folder)
    {
        var model = (MainViewModel)window.DataContext;
        var grid = (DataGrid)window.FindName("FileGrid");
        var cards = (ListBox)window.FindName("FileCards");
        var tree = (TreeView)window.FindName("DirectoryTree");
        var originalView = model.ViewMode;
        var candidateField = typeof(MainWindow).GetField("fileDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var threshold = typeof(MainWindow).GetMethod("ShouldStartFileDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var surface in new Control[] { grid, cards })
        {
            model.ViewMode = surface == grid ? MainViewModel.DetailsViewName : MainViewModel.CardViewName;
            var items = surface == grid ? grid.SelectedItems : cards.SelectedItems;
            items.Clear(); items.Add(file); items.Add(folder);
            render(window, Path.Combine(output, surface == grid ? "drag-out-list.png" : "drag-out-cards.png"), 1360, 780);
            var container = surface == grid ? grid.ItemContainerGenerator.ContainerFromItem(file) : cards.ItemContainerGenerator.ContainerFromItem(file);
            Check(container != null, "Outgoing source container is realized");
            var down = RaiseMouse(surface, container!, UIElement.PreviewMouseLeftButtonDownEvent);
            var gesture = candidateField.GetValue(window)!;
            Check(down.Handled && gesture != null && items.Count == 2,
                "Pressing a selected item preserves multi-selection while a drag is possible");
            var start = (Point)gesture!.GetType().GetProperty("Start")!.GetValue(gesture)!;
            var moved = start + new Vector(SystemParameters.MinimumHorizontalDragDistance + 1, 0);
            Check(!(bool)threshold.Invoke(window, [surface, start, MouseButtonState.Pressed])!
                && (bool)threshold.Invoke(window, [surface, moved, MouseButtonState.Pressed])!
                && !(bool)threshold.Invoke(window, [surface, moved, MouseButtonState.Released])!,
                "A held left button and system drag distance are required; clicks and released gestures do not drag");
            await model.RunAsync("Drag gesture busy check", _ =>
            {
                Check(!(bool)threshold.Invoke(window, [surface, moved, MouseButtonState.Pressed])!, "Busy operations reject drag starts");
                return Task.CompletedTask;
            });
            // Offscreen content has no native route to its Window; deliver the window-level release explicitly.
            RaiseMouse(window, container!, UIElement.PreviewMouseLeftButtonUpEvent);
            Check(candidateField.GetValue(window) == null && items.Count == 1 && items.Contains(file),
                "Releasing without dragging retains ordinary single-click selection");
            RaiseMouse(surface, surface, UIElement.PreviewMouseLeftButtonDownEvent);
            Check(candidateField.GetValue(window) == null, "Blank surfaces cannot initiate a drag");
            RaiseMouse(surface, container!, UIElement.PreviewMouseLeftButtonDownEvent, clicks: 2);
            Check(candidateField.GetValue(window) == null, "Double-click remains available for opening items");
        }
        RaiseMouse(grid, new DataGridColumnHeader(), UIElement.PreviewMouseLeftButtonDownEvent);
        Check(candidateField.GetValue(window) == null, "Column headers cannot initiate file drags");
        RaiseMouse(grid, new ScrollBar(), UIElement.PreviewMouseLeftButtonDownEvent);
        Check(candidateField.GetValue(window) == null, "Scrollbars cannot initiate file drags");
        tree.UpdateLayout();
        var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
        RaiseMouse(tree, rootItem, UIElement.PreviewMouseLeftButtonDownEvent);
        var treeGesture = candidateField.GetValue(window)!;
        Check(((BrowserEntry)treeGesture.GetType().GetProperty("Entry")!.GetValue(treeGesture)!).RelativePath == "",
            "Tree drag uses the clicked directory independently of file selection");
        RaiseMouse(window, rootItem, UIElement.PreviewMouseLeftButtonUpEvent);
        foreach (var action in new[] { "release", "escape", "capture-loss" })
        {
            RaiseMouse(tree, rootItem, UIElement.PreviewMouseLeftButtonDownEvent);
            var preparing = candidateField.GetValue(window)!;
            var type = preparing.GetType();
            type.GetProperty("Started")!.SetValue(preparing, true);
            type.GetProperty("Preparing")!.SetValue(preparing, true);
            using var cancellation = new CancellationTokenSource();
            type.GetProperty("PreparationCancellation")!.SetValue(preparing, cancellation);
            if (action == "release") RaiseMouse(window, rootItem, UIElement.PreviewMouseLeftButtonUpEvent);
            else if (action == "capture-loss")
                window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.LostMouseCaptureEvent });
            else
            {
                var nativeSource = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, nativeSource!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(key);
                Check(key.Handled, "Escape cancels preparation before other shortcuts");
            }
            Check(cancellation.IsCancellationRequested && (bool)type.GetProperty("Cancelled")!.GetValue(preparing)!,
                "Release, Escape and capture loss cancel preparation instead of starting a later drag");
            type.GetProperty("Started")!.SetValue(preparing, false);
            type.GetProperty("Preparing")!.SetValue(preparing, false);
            RaiseMouse(window, rootItem, UIElement.PreviewMouseLeftButtonUpEvent);
        }
        model.ViewMode = originalView;
    }

    private static MouseButtonEventArgs RaiseMouse(Control surface, DependencyObject source, RoutedEvent routedEvent, int clicks = 1)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = routedEvent, Source = source };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, clicks);
        surface.RaiseEvent(args);
        return args;
    }

    private static Task<DataObject?> PrepareAsync(MainWindow window, IReadOnlyList<BrowserEntry> entries, CancellationToken token = default) =>
        (Task<DataObject?>)typeof(MainWindow).GetMethod("PrepareFileDragDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [entries, token])!;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
