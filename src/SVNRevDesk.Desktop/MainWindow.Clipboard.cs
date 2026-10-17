using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private bool preparingClipboard;

    private IReadOnlyList<BrowserEntry> ClipboardSelection(string? treeDirectory = null) => treeDirectory != null
        ? model.GetDirectoryEntry(treeDirectory) is { } entry ? [entry] : []
        : (model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems).Cast<BrowserEntry>().ToArray();

    private bool CanCopyFiles(IReadOnlyList<BrowserEntry> selection) => model.CanOperate && !preparingClipboard
        && selection.Count > 0 && selection.All(entry => entry.ExistsLocally || entry.ServerRevision != null);

    private bool CanCutFiles(IReadOnlyList<BrowserEntry> selection) => !preparingClipboard && model.CanOperate
        && BrowserActionAvailability.CanExecute("move-to", model.Session, selection);

    private void UpdateClipboardMenu(ContextMenu menu)
    {
        var tree = menu.PlacementTarget == DirectoryTree;
        var relative = newItemDirectory ?? model.CurrentRelative;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is "file-copy") item.IsEnabled = (!tree || newItemDirectory != null)
                && CanCopyFiles(ClipboardSelection(tree ? newItemDirectory : null));
            if (item.Tag is "file-cut") item.IsEnabled = (!tree || newItemDirectory != null)
                && CanCutFiles(ClipboardSelection(tree ? newItemDirectory : null));
            if (item.Tag is "file-paste")
            {
                item.IsEnabled = !preparingClipboard && (!tree || newItemDirectory != null)
                    && model.CanCreateInDirectory(relative) && FileClipboard.ContainsFiles();
                item.ToolTip = model.CanCreateInDirectory(relative)
                    ? "粘贴到：" + RepositoryService.GetLocalPath(model.Session!, relative)
                    : "请关联本地工作副本，并选择已下载的本地目录。";
            }
        }
    }

    private async void ClipboardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu) return;
        var tree = menu.PlacementTarget == DirectoryTree;
        if (item.Tag is "file-copy") await CopyFilesAsync(ClipboardSelection(tree ? newItemDirectory : null));
        else if (item.Tag is "file-cut") await CutFilesAsync(ClipboardSelection(tree ? newItemDirectory : null));
        else if (item.Tag is "file-paste") await PasteFilesAsync(newItemDirectory ?? model.CurrentRelative);
    }

    private bool IsFileClipboardKey(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (modifiers != ModifierKeys.Control || key is not (Key.C or Key.X or Key.V)
            || FindParent<TextBoxBase>(source) != null || FindParent<PasswordBox>(source) != null
            || FindParent<ComboBox>(source) != null) return false;
        // Navigation buttons and view changes can leave focus outside the file list.
        if (key == Key.V) return true;
        return FindParent<DataGrid>(source) == FileGrid || FindParent<ListBox>(source) == FileCards
            || FindParent<TreeView>(source) == DirectoryTree;
    }

    private void FileBrowser_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RecordFileDragStart(sender, e)) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindParent<DataGridRow>(source) != null || FindParent<ListBoxItem>(source) != null
            || FindParent<TreeViewItem>(source) != null
            || FindParent<ButtonBase>(source) != null || FindParent<ScrollBar>(source) != null
            || FindParent<TextBoxBase>(source) != null) return;
        // Empty lists have no row to receive focus, so move it away from the address/filter box.
        if (sender is Control browser)
        {
            FocusManager.SetFocusedElement(this, browser);
            browser.Focus();
        }
    }

    private async Task HandleClipboardKeyAsync(Key key, DependencyObject? source)
    {
        var tree = FindParent<TreeView>(source) == DirectoryTree;
        var relative = tree && DirectoryTree.SelectedItem is DirectoryNode { IsPlaceholder: false } node
            ? node.RelativePath : model.CurrentRelative;
        if (key == Key.C) await CopyFilesAsync(ClipboardSelection(tree ? relative : null));
        else if (key == Key.X) await CutFilesAsync(ClipboardSelection(tree ? relative : null));
        else await PasteFilesAsync(relative);
    }

    private async Task CutFilesAsync(IReadOnlyList<BrowserEntry> selection)
    {
        if (!CanCutFiles(selection)) return;
        preparingClipboard = true;
        try
        {
            foreach (var entry in selection) LocalFileTransfer.ValidatePath(entry.LocalPath!);
            await FileClipboard.WriteCutFilesAsync(selection.Select(entry => entry.LocalPath!).ToArray());
            model.StatusMessage = $"已剪切 {selection.Count} 项，请在本软件的目标目录中粘贴；尚未移动或提交。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "剪切未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { preparingClipboard = false; }
    }

    private async Task CopyFilesAsync(IReadOnlyList<BrowserEntry> selection)
    {
        if (!CanCopyFiles(selection)) return;
        preparingClipboard = true;
        try
        {
            var session = model.Session;
            if (selection.All(entry => entry.ExistsLocally && !entry.IsDirectory))
            {
                foreach (var entry in selection) LocalFileTransfer.ValidatePath(entry.LocalPath!);
                await FileClipboard.WriteFilesAsync(selection.Select(entry => entry.LocalPath!).ToArray());
            }
            else
            {
                // Preserve snapshots still referenced by the Windows clipboard.
                await ClipboardFileCache.CleanupAsync(model.Settings.TemporaryDirectory, FileClipboard.ReadFiles());
                if (session != model.Session || !model.CanOperate) return;
                await HandleAsync("复制文件", async token =>
                {
                    var paths = await model.PrepareClipboardFilesAsync(selection, token);
                    token.ThrowIfCancellationRequested();
                    await FileClipboard.WriteFilesAsync(paths, selection.Select(entry => entry.LocalPath ?? "").ToArray());
                });
                if (model.StatusMessage != "复制文件完成") return;
            }
            model.StatusMessage = $"已复制 {selection.Count} 个项目，可在资源管理器中粘贴。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "复制未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { preparingClipboard = false; }
    }

    private Task PasteFilesAsync(string relative) => PasteFileDataAsync(relative, null, fromDrop: false);

    private async Task PasteFileDataAsync(string relative, IDataObject? data, bool fromDrop)
    {
        if (preparingClipboard || !model.CanCreateInDirectory(relative)) return;
        preparingClipboard = true;
        try
        {
            if (!fromDrop) data = Clipboard.GetDataObject();
            var sources = FileClipboard.ReadFiles(data);
            var origins = FileClipboard.ReadOriginalPaths(data);
            if (sources.Count == 0) return;
            // A drop always copies. Reporting Copy to Explorer must never remove its sources.
            if (!fromDrop && FileClipboard.IsMove(data))
            {
                await PasteCutFilesAsync(relative, sources, data!, FileClipboard.SequenceNumber);
                return;
            }
            var session = model.Session!;
            var directory = RepositoryService.GetLocalPath(session, relative)!;
            model.StatusMessage = fromDrop ? "正在检查拖入文件…" : "正在检查粘贴文件…";
            var plan = await Task.Run(() => LocalFileTransfer.Prepare(sources, session.LocalRoot!, directory, originalPaths: origins));
            if (session != model.Session || !model.CanCreateInDirectory(relative)) return;
            var replace = false;
            var backupBeforeOverwrite = true;
            if (plan.Conflicts.Count > 0)
            {
                var names = plan.Conflicts.Select(item => Path.GetRelativePath(directory, item.Destination)).ToArray();
                var choice = new OperationDialogs(this, model).ResolveFileConflicts(fromDrop ? "拖入时存在同名文件" : "粘贴时存在同名文件", directory, names,
                    "请选择此次复制的处理方式。文件夹会合并，目标中其他文件保留；新文件不会自动加入版本控制或提交。");
                if (choice == null) { model.StatusMessage = fromDrop ? "已取消拖入。" : "已取消粘贴。"; return; }
                replace = choice.ReplaceExisting;
                backupBeforeOverwrite = choice.BackupBeforeOverwrite;
            }
            if (session != model.Session || !model.CanCreateInDirectory(relative)) return;
            await HandleAsync(fromDrop ? "拖入文件" : "粘贴文件", token => model.PasteLocalFilesAsync(relative, plan, replace, token, backupBeforeOverwrite));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, fromDrop ? "拖入未完成" : "粘贴未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { preparingClipboard = false; }
    }

    private async Task PasteCutFilesAsync(string relative, IReadOnlyList<string> sources, IDataObject data, uint sequence)
    {
        var session = model.Session!;
        var directory = RepositoryService.GetLocalPath(session, relative)!;
        try
        {
            await HandleAsync("移动文件", async token =>
            {
                var plan = await Task.Run(() => model.Operations.PrepareFileMoveAsync(sources, directory, session.LocalRoot!, token), token);
                if (session != model.Session) return;
                if (plan.Items.All(item => item.IsSameLocation)) { model.AddLog("所选项目已在目标目录，无需移动。"); return; }
                var options = new OperationDialogs(this, model).ResolveMoveConflicts(plan);
                if (options != null) await model.MoveLocalFilesAsync(plan, options, token);
            });
        }
        finally
        {
            // Failed or cancelled batches retain their remaining cut sources for a later paste.
            var remaining = sources.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
            if (remaining.Length != sources.Count) await FileClipboard.CompleteMoveAsync(data, sequence, remaining);
        }
    }
}
