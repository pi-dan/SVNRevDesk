using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private bool selectingDirectoryForMenu;

    private BrowserEntry? DirectoryMenuEntry() => newItemDirectory is { } relative ? model.GetDirectoryEntry(relative) : null;

    private void UpdateDirectoryMenu(ContextMenu menu)
    {
        var entry = DirectoryMenuEntry();
        IReadOnlyList<BrowserEntry> selection = entry != null ? [entry] : [];
        UpdateIgnoreMenu(menu.Items, selection);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not string operation || operation is "new-item" or "file-copy" or "file-cut" or "file-paste") continue;
            item.IsEnabled = entry != null && model.CanOperate && (operation == "tree-refresh"
                || BrowserActionAvailability.CanExecute(operation, model.Session, selection));
        }
    }

    private void DirectoryContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        // A context click highlights its target without changing the displayed directory. Restore
        // the browsing selection afterwards so a subsequent left click still navigates normally.
        selectingDirectoryForMenu = true;
        try { if (model.FindNode(model.CurrentRelative) is { } current) current.IsSelected = true; }
        finally { selectingDirectoryForMenu = false; }
    }

    private async void DirectoryOperation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string operation } || !model.CanOperate || DirectoryMenuEntry() is not { } entry) return;
        if (operation == "tree-refresh")
        {
            await model.RefreshDirectoryInBackgroundAsync(entry.RelativePath);
            return;
        }
        await ExecuteOperationAsync(operation, [entry], model.GetEntrySnapshotRevision(entry).ToString());
    }
}
