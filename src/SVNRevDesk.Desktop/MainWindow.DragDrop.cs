using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private FileDragGesture? fileDrag;

    private sealed class FileDragGesture(Control browser, BrowserEntry entry, RepositorySession? session, Point start, bool deferSelection)
    {
        public Control Browser { get; } = browser;
        public BrowserEntry Entry { get; } = entry;
        public RepositorySession? Session { get; } = session;
        public Point Start { get; } = start;
        public bool DeferSelection { get; } = deferSelection;
        public bool CapturedBrowser { get; set; }
        public bool Started { get; set; }
        public bool Preparing { get; set; }
        public bool Cancelled { get; set; }
        public CancellationTokenSource? PreparationCancellation { get; set; }
    }

    private BrowserEntry? FileDragEntry(object sender, DependencyObject? source)
    {
        if (FindParent<ButtonBase>(source) != null || FindParent<ScrollBar>(source) != null
            || FindParent<DataGridColumnHeader>(source) != null || FindParent<TextBoxBase>(source) != null) return null;
        if (sender == DirectoryTree)
            return FindParent<TreeViewItem>(source)?.DataContext is DirectoryNode { IsPlaceholder: false } node
                ? model.GetDirectoryEntry(node.RelativePath) : null;
        return sender == FileGrid ? FindParent<DataGridRow>(source)?.Item as BrowserEntry
            : sender == FileCards ? FindParent<ListBoxItem>(source)?.DataContext as BrowserEntry : null;
    }

    private IReadOnlyList<BrowserEntry> FileDragSelection(Control browser) =>
        (browser == FileGrid ? FileGrid.SelectedItems : FileCards.SelectedItems).Cast<BrowserEntry>().ToArray();

    private bool RecordFileDragStart(object sender, MouseButtonEventArgs e)
    {
        if (fileDrag is { Started: true }) return false;
        if (fileDrag is { CapturedBrowser: true } previous && Mouse.Captured == previous.Browser) Mouse.Capture(null);
        fileDrag = null;
        if (e.ClickCount != 1 || sender is not Control browser
            || FileDragEntry(sender, e.OriginalSource as DependencyObject) is not { } entry || !CanCopyFiles([entry])) return false;
        var selection = browser == DirectoryTree ? new[] { entry } : FileDragSelection(browser);
        var defer = Keyboard.Modifiers == ModifierKeys.None && selection.Count > 1 && selection.Contains(entry);
        fileDrag = new(browser, entry, model.Session, e.GetPosition(browser), defer);
        if (!defer) return false;
        // Keep a multi-selection intact until we know whether this is a drag or a normal click.
        browser.Focus();
        fileDrag.CapturedBrowser = Mouse.Capture(browser, CaptureMode.SubTree);
        e.Handled = true;
        return true;
    }

    private bool ShouldStartFileDrag(object sender, Point position, MouseButtonState leftButton) =>
        fileDrag is { Started: false, Cancelled: false } gesture && gesture.Browser == sender
        && gesture.Session == model.Session && leftButton == MouseButtonState.Pressed && model.CanOperate && !preparingClipboard
        && (Math.Abs(position.X - gesture.Start.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(position.Y - gesture.Start.Y) >= SystemParameters.MinimumVerticalDragDistance);

    private async void FileBrowser_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Control browser || !ShouldStartFileDrag(sender, e.GetPosition(browser), e.LeftButton)) return;
        var gesture = fileDrag!;
        var selection = browser == DirectoryTree ? new[] { gesture.Entry } : FileDragSelection(browser);
        if (!selection.Contains(gesture.Entry) || !CanCopyFiles(selection))
        {
            fileDrag = null;
            if (gesture.CapturedBrowser && Mouse.Captured == browser) Mouse.Capture(null);
            return;
        }
        gesture.Started = true;
        gesture.Preparing = true;
        e.Handled = true;
        preparingClipboard = true;
        using var cancellation = new CancellationTokenSource();
        gesture.PreparationCancellation = cancellation;
        try
        {
            // The window stays enabled while preparation disables the file controls. Capture release even outside it.
            if (!Mouse.Capture(this, CaptureMode.SubTree)) return;
            var data = await PrepareFileDragDataAsync(selection, cancellation.Token);
            gesture.Preparing = false;
            if (gesture.Cancelled || Mouse.LeftButton != MouseButtonState.Pressed)
            { model.StatusMessage = "已取消拖出。"; return; }
            if (data == null) return;
            if (Mouse.Captured == this) Mouse.Capture(null);
            var effect = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
            model.StatusMessage = effect == DragDropEffects.Copy
                ? $"已拖出 {selection.Count} 项（复制），源文件保留。" : "已取消拖出。";
        }
        catch (OperationCanceledException) { model.StatusMessage = "已取消拖出。"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "拖出未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            gesture.Preparing = false;
            fileDrag = null;
            if (Mouse.Captured == this) Mouse.Capture(null);
            preparingClipboard = false;
        }
    }

    private async Task<DataObject?> PrepareFileDragDataAsync(IReadOnlyList<BrowserEntry> selection, CancellationToken token)
    {
        var session = model.Session;
        DataObject? data = null;
        var origins = selection.Select(entry => entry.LocalPath ?? "").ToArray();
        if (selection.All(entry => entry.ExistsLocally && !entry.IsDirectory))
            return FileClipboard.CreateData(await model.PrepareClipboardFilesAsync(selection, token), origins);
        model.StatusMessage = "正在准备拖出内容，请保持按住鼠标；松开或按 Esc 取消…";
        await ClipboardFileCache.CleanupAsync(model.Settings.TemporaryDirectory, FileClipboard.ReadFiles());
        token.ThrowIfCancellationRequested();
        if (session != model.Session || !model.CanOperate) return null;
        await HandleAsync("准备拖出文件", async operationToken =>
        {
            model.StatusMessage = "正在准备拖出内容，请保持按住鼠标；松开或按 Esc 取消…";
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operationToken);
            var paths = await model.PrepareClipboardFilesAsync(selection, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            data = FileClipboard.CreateData(paths, origins);
        });
        return data;
    }

    private void Window_FileDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (fileDrag is not { } gesture) return;
        if (gesture.Started)
        {
            if (gesture.Preparing)
            {
                gesture.Cancelled = true;
                gesture.PreparationCancellation?.Cancel();
            }
            return;
        }
        fileDrag = null;
        if (gesture.CapturedBrowser && Mouse.Captured == gesture.Browser) Mouse.Capture(null);
        if (!gesture.DeferSelection || FileDragEntry(gesture.Browser, e.OriginalSource as DependencyObject) != gesture.Entry) return;
        var items = gesture.Browser == FileGrid ? FileGrid.SelectedItems : FileCards.SelectedItems;
        items.Clear();
        items.Add(gesture.Entry);
        e.Handled = true;
    }

    private void Window_FileDragLostCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured != this && fileDrag is { Started: true, Preparing: true } gesture)
        {
            gesture.Cancelled = true;
            gesture.PreparationCancellation?.Cancel();
        }
    }

    private string? FileDropDirectory(object sender, DependencyObject? source)
    {
        if (FindParent<ScrollBar>(source) != null || FindParent<DataGridColumnHeader>(source) != null) return null;
        if (sender == DirectoryTree)
            return FindParent<TreeViewItem>(source)?.DataContext is DirectoryNode { IsPlaceholder: false } node
                ? node.RelativePath : null;
        if (sender != FileGrid && sender != FileCards) return null;
        var entry = sender == FileGrid ? FindParent<DataGridRow>(source)?.Item as BrowserEntry
            : FindParent<ListBoxItem>(source)?.DataContext as BrowserEntry;
        return entry is { IsDirectory: true } ? entry.RelativePath : model.CurrentRelative;
    }

    private string? AllowedFileDropDirectory(object sender, DragEventArgs e)
    {
        if (preparingClipboard || (e.AllowedEffects & DragDropEffects.Copy) == 0
            || !e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var relative = FileDropDirectory(sender, e.OriginalSource as DependencyObject);
        return relative != null && model.CanCreateInDirectory(relative) ? relative : null;
    }

    private void FileBrowser_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // Handle preview events so cell text controls cannot consume Explorer's file data.
        e.Effects = AllowedFileDropDirectory(sender, e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void FileBrowser_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        var relative = AllowedFileDropDirectory(sender, e);
        if (relative == null) return;
        e.Effects = DragDropEffects.Copy;
        // The shared paste flow reads the OLE data before its first await; it never writes the clipboard.
        await PasteFileDataAsync(relative, e.Data, fromDrop: true);
    }
}
