using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private string? newItemDirectory;
    private readonly LocalCreationChanges creationChanges = new();

    private void DirectoryTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        newItemDirectory = item?.DataContext is DirectoryNode { IsPlaceholder: false } node
            ? node.RelativePath : null;
        // Right-clicking does not navigate: an unvisited folder can be used without a server read.
        if (newItemDirectory != null && item != null)
        {
            selectingDirectoryForMenu = true;
            try { item.IsSelected = true; }
            finally { selectingDirectoryForMenu = false; }
        }
    }

    private void PopulateNewItemMenu(ContextMenu contextMenu)
    {
        var menu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "new-item"));
        var relative = newItemDirectory ?? model.CurrentRelative;
        menu.IsEnabled = !(contextMenu.PlacementTarget == DirectoryTree && newItemDirectory == null)
            && model.CanCreateInDirectory(relative);
        menu.ToolTip = menu.IsEnabled ? "创建位置：" + RepositoryService.GetLocalPath(model.Session!, relative)
            : "请关联本地工作副本，并选择已下载的本地目录。";
        menu.Items.Clear();
        foreach (var option in NewItemOptions.Standard)
        {
            if (option.Kind == NewItemKind.EmptyFile) menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem(option, relative));
        }
    }

    private MenuItem CreateMenuItem(NewItemOption option, string relative)
    {
        var item = new MenuItem { Header = option.Label };
        item.Click += async (_, _) =>
        {
            if (!model.CanCreateInDirectory(relative)) return;
            var session = model.Session!;
            var dialog = new NewItemWindow(this, RepositoryService.GetLocalPath(session, relative)!, option);
            if (dialog.ShowDialog() != true || session != model.Session || !model.CanCreateInDirectory(relative)) return;
            await HandleAsync("新建" + (option.Kind == NewItemKind.Folder ? "文件夹" : "文件"),
                token => model.CreateLocalItemAsync(relative, option.Request(dialog.ItemName), dialog.AddToSvn, token));
        };
        return item;
    }

    private void SelectCreatedEntry(string relative)
    {
        var entry = model.Entries.SingleOrDefault(item => item.RelativePath == relative);
        if (entry == null) return;
        FileGrid.SelectedItems.Clear();
        FileGrid.SelectedItems.Add(entry);
        FileGrid.ScrollIntoView(entry);
        FileCards.ScrollIntoView(entry);
    }
}
