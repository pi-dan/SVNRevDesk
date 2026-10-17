using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class CommitWindowTests
{
    public static void Run(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "提交 @ 工作副本");
        var addedDirectory = Path.Combine(root, "新增目录");
        var changes = new[]
        {
            Change(root, "合同 / 产品方案.pdf", "modified"),
            Change(root, "说明.txt", "modified", properties: "modified"),
            Change(root, "资源 / 图标.png", "added"),
            Change(root, "移除文件.zip", "deleted"),
            Change(root, "目录属性", "normal", directory: true, properties: "modified"),
            Change(root, "新增目录", "added", directory: true),
            Change(root, "新增目录 / 子文件.txt", "added", parents: [addedDirectory]),
            Change(root, "未添加的新文件.txt", "unversioned"),
            Change(root, "缺失文件.txt", "missing"),
            Change(root, "冲突文件.txt", "conflicted")
        };
        var window = new CommitWindow(null, root, changes);
        var model = window.Model;
        Check(model.SelectedCount == 7 && model.CanSubmit && !model.Items.Single(item => item.Change.NeedsAddition).IsChecked,
            "Versioned changes default to checked; unversioned changes require selection but the commit message is optional");
        var message = (TextBox)window.FindName("MessageBox");
        render(window, Path.Combine(output, "commit-selection.png"), 940, 650);
        Check(((Button)window.FindName("SubmitButton")).IsEnabled && model.Message == "",
            "The actual commit button is enabled when checked changes have an empty message");
        message.Text = " \t\n";
        message.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Check(model.CanSubmit && ((Button)window.FindName("SubmitButton")).IsEnabled,
            "Whitespace-only messages do not disable commit");
        message.Text = "更新产品方案和说明\n修正目录属性";
        message.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        render(window, Path.Combine(output, "commit-selection.png"), 940, 650);
        Check(model.CanSubmit && ((Button)window.FindName("SubmitButton")).IsEnabled
            && message.VerticalContentAlignment == VerticalAlignment.Top, "Optional message binds correctly and starts at the top");
        var grid = (DataGrid)window.FindName("ChangesGrid");
        Check(grid.Columns.Count == 6 && grid.Items.Count == changes.Length,
            "Commit window displays checkboxes, paths, extensions, content/property status and local locks");
        var firstRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Items[0]);
        Check(FindVisual<Image>(firstRow)?.Source is DrawingImage, "Commit file icons render as vector images rather than resource text");
        var parent = model.Items.Single(item => item.Change.Path == addedDirectory);
        var child = model.Items.Single(item => item.Change.RequiredParents.Count > 0);
        grid.ScrollIntoView(parent);
        grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(parent);
        var check = FindVisual<CheckBox>(row)!;
        check.IsChecked = false;
        Check(!parent.IsChecked && !child.IsChecked && model.SelectedCount == 5,
            "Unchecking a new directory through its actual checkbox also unchecks dependent children");
        grid.ScrollIntoView(child);
        grid.UpdateLayout();
        var childRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(child);
        FindVisual<CheckBox>(childRow)!.IsChecked = true;
        Check(child.IsChecked && parent.IsChecked, "Checking a new child selects its required parent directory");
        var unversioned = model.Items.Single(item => item.Change.NeedsAddition);
        grid.ScrollIntoView(unversioned);
        grid.UpdateLayout();
        var newRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(unversioned);
        Check(FindVisual<CheckBox>(newRow)!.IsEnabled && unversioned.Change.Detail.Contains("自动添加", StringComparison.Ordinal),
            "Unversioned items have enabled checkboxes and explain automatic addition");
        FindVisual<CheckBox>(newRow)!.IsChecked = true;
        Check(model.SelectedChanges.Contains(unversioned.Change) && model.SelectedCount == 8,
            "Checking an unversioned file includes it in the actual commit selection");

        FindButton((DependencyObject)window.Content, "取消全选")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        message.Text = "";
        message.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Check(model.SelectedCount == 0 && !model.CanSubmit && !((Button)window.FindName("SubmitButton")).IsEnabled,
            "Clear-all button deselects every item and disables commit");
        FindButton((DependencyObject)window.Content, "全选")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.SelectedCount == 8 && model.SelectedChanges.All(change => change.CanCommit),
            "Select-all includes unversioned items and excludes blocked files");
        FindButton((DependencyObject)window.Content, "已版本控制")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.SelectedCount == 7 && model.SelectedChanges.All(change => !change.NeedsAddition), "Select versioned excludes new files");
        FindButton((DependencyObject)window.Content, "未版本控制")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.SelectedCount == 1 && model.SelectedChanges.Single().NeedsAddition, "Select unversioned includes only new files");
        model.ShowUnversioned = false;
        Check(grid.Items.Count == changes.Length - 1 && model.SelectedCount == 0, "Hiding new files unchecks them rather than adding hidden files");
        model.SelectAll(true);
        Check(model.SelectedCount == 7 && !unversioned.IsChecked, "Select-all respects hidden new files");
        model.ShowUnversioned = true;
        Check(grid.Items.Count == changes.Length && !unversioned.IsChecked, "Showing new files restores rows without selecting them automatically");
        model.SelectAll(true);
        model.Items[0].IsChecked = false;
        Check(model.SelectedChanges.Count == 7 && model.SelectedChanges.All(change => change.Path != changes[0].Path),
            "Unchecked rows are excluded from the final SVN selection");
        render(window, Path.Combine(output, "commit-selection-narrow.png"), 740, 500);
        Console.WriteLine("PASS: commit checkboxes, automatic-add selection, versioned/unversioned/all/none controls, hidden-item protection, parent dependencies, optional messages, file icons and normal/narrow WPF layout.");
    }

    private static CommitChange Change(string root, string relative, string item, bool directory = false,
        string properties = "none", IReadOnlyList<string>? parents = null)
    {
        relative = relative.Replace(" / ", "/");
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        return new(path, relative, directory,
            new WorkingStatus(path, item, properties, "", "", 3, 3, false, false, null, null), parents ?? []);
    }

    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private static Button? FindButton(DependencyObject parent, string content)
    {
        if (parent is Button button && Equals(button.Content, content)) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindButton(VisualTreeHelper.GetChild(parent, i), content) is { } child) return child;
        return null;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
