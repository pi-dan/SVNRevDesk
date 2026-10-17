using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.Views;

public sealed class WorkingCopyLinksWindow : Window
{
    public ObservableCollection<WorkingCopyLink> Links { get; }
    public WorkingCopyLink? SelectedLink { get; private set; }
    public bool OpenOnline { get; private set; }

    public WorkingCopyLinksWindow(Window owner, IEnumerable<WorkingCopyLink> links)
    {
        Owner = owner; Title = "工作副本与 URL 关联";
        Width = 960; Height = 520; MinWidth = 680; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Links = new(links);
        var panel = new DockPanel();
        var explanation = new TextBlock { Text = "打开本地工作副本、检出或关联成功后，自动保存已验证的路径与仓库 URL。选择记录可打开本地或对应 URL；移除记录保留本地文件。实际打开时会重新检查 SVN 信息，路径被移动或切换分支后以当前元数据为准。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var grid = new DataGrid { ItemsSource = Links, AutoGenerateColumns = false, IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false, CanUserDeleteRows = false };
        var cellStyle = new Style(typeof(TextBlock));
        cellStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cellStyle.Setters.Add(new Setter(ToolTipProperty, new Binding("Text") { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }));
        grid.Columns.Add(new DataGridTextColumn { Header = "本地工作副本", Binding = new Binding(nameof(WorkingCopyLink.LocalPath)), ElementStyle = cellStyle, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "仓库 URL", Binding = new Binding(nameof(WorkingCopyLink.Url)), ElementStyle = cellStyle, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var local = new Button { Content = "打开本地", IsEnabled = false };
        var online = new Button { Content = "打开 URL", IsEnabled = false };
        var remove = new Button { Content = "移除记录", IsEnabled = false };
        local.Click += (_, _) => Open(false); online.Click += (_, _) => Open(true);
        remove.Click += (_, _) => { if (grid.SelectedItem is WorkingCopyLink link) Links.Remove(link); };
        grid.SelectionChanged += (_, _) => { local.IsEnabled = online.IsEnabled = remove.IsEnabled = grid.SelectedItem is WorkingCopyLink; };
        grid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(grid, source) is DataGridRow) Open(false);
        };
        buttons.Children.Add(local); buttons.Children.Add(online); buttons.Children.Add(remove);
        buttons.Children.Add(new Button { Content = "关闭", IsCancel = true });
        panel.Children.Add(grid); Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = panel };

        void Open(bool useOnline)
        {
            if (grid.SelectedItem is not WorkingCopyLink link) return;
            SelectedLink = link; OpenOnline = useOnline; DialogResult = true;
        }
    }
}
