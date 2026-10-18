using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Views;

public sealed class RepositorySearchWindow : Window
{
    private readonly Func<string, string, bool, IProgress<RepositorySearchProgress>, CancellationToken, Task<RepositorySearchResult>> search;
    private readonly string currentRelative;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource? running;
    private readonly ObservableCollection<BrowserEntry> results = [];
    private readonly TextBox query;
    private readonly ComboBox scope;
    private readonly CheckBox ignored;
    private readonly Button start;
    private readonly Button cancel;
    private readonly TextBlock status;
    private readonly DataGrid grid;
    private bool closed;
    public BrowserEntry? SelectedResult { get; private set; }

    public RepositorySearchWindow(Window owner, string openedUrl, string currentRelative,
        Func<string, string, bool, IProgress<RepositorySearchProgress>, CancellationToken, Task<RepositorySearchResult>> search,
        CancellationToken token)
    {
        Owner = owner; Title = "跨目录搜索"; Width = 1040; Height = 650; MinWidth = 800; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.currentRelative = currentRelative; this.search = search;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panel = new DockPanel();
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = openedUrl, ToolTip = openedUrl, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.SlateGray });
        top.Children.Add(new TextBlock { Text = "按名称或相对路径查找文件和文件夹，包含尚未下载的仓库项目。双击结果可定位到所在目录。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        start = new Button { Name = "StartSearch", Content = "开始搜索", IsDefault = true }; DockPanel.SetDock(start, Dock.Right); row.Children.Add(start);
        cancel = new Button { Name = "StopSearch", Content = "停止", IsEnabled = false }; DockPanel.SetDock(cancel, Dock.Right); row.Children.Add(cancel);
        query = new TextBox { Name = "SearchQuery", ToolTip = "输入名称或路径的一部分，不区分大小写；不搜索文件内容，不使用通配符。" }; row.Children.Add(query); top.Children.Add(row);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        options.Children.Add(new TextBlock { Text = "范围：", VerticalAlignment = VerticalAlignment.Center });
        scope = new ComboBox { Name = "SearchScope", Width = 200, ItemsSource = new[] { "当前目录及子目录", "整个已打开范围" }, SelectedIndex = 0 };
        options.Children.Add(scope);
        var scopeLabel = new TextBlock { Text = currentRelative.Length == 0 ? "根目录" : currentRelative,
            ToolTip = currentRelative, MaxWidth = 340, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        scope.SelectionChanged += (_, _) => { scopeLabel.Text = scope.SelectedIndex == 1 || currentRelative.Length == 0 ? "根目录" : currentRelative; scopeLabel.ToolTip = scopeLabel.Text; };
        options.Children.Add(scopeLabel);
        ignored = new CheckBox { Name = "IncludeIgnored", Content = "包含忽略项", VerticalAlignment = VerticalAlignment.Center }; options.Children.Add(ignored); top.Children.Add(options);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        status = new TextBlock { Name = "SearchStatus", Text = "输入关键词后开始搜索。最多显示 5000 项；可随时停止并保留已有结果。",
            TextWrapping = TextWrapping.Wrap, MaxHeight = 80, Margin = new Thickness(0, 10, 0, 8) }; bottom.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var locate = new Button { Name = "LocateResult", Content = "定位到所在目录", IsEnabled = false };
        locate.Click += (_, _) => Locate(); buttons.Children.Add(locate); buttons.Children.Add(new Button { Content = "关闭", IsCancel = true }); bottom.Children.Add(buttons);
        grid = new DataGrid { Name = "SearchResults", ItemsSource = results, IsReadOnly = true, AutoGenerateColumns = false,
            CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        AddColumn("名称", nameof(BrowserEntry.Name), 210); AddColumn("相对路径", nameof(BrowserEntry.RelativePath), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn("类型", nameof(BrowserEntry.Kind), 75); AddColumn("状态", nameof(BrowserEntry.StateText), 130);
        grid.SelectionChanged += (_, _) => locate.IsEnabled = grid.SelectedItem is BrowserEntry;
        grid.MouseDoubleClick += (_, e) => { if (ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is DataGridRow) Locate(); };
        grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Locate(); } };
        panel.Children.Add(grid); Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = panel };
        start.Click += async (_, _) => await SearchAsync(); cancel.Click += (_, _) => running?.Cancel();
        Loaded += (_, _) => query.Focus();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };

        void AddColumn(string header, string property, DataGridLength width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = width, MinWidth = width.IsStar ? 240 : 40 });
    }

    private void Locate()
    {
        if (grid.SelectedItem is not BrowserEntry entry) return;
        SelectedResult = entry; DialogResult = true;
    }

    private async Task SearchAsync()
    {
        if (running != null || closed) return;
        if (query.Text.Trim().Length == 0) { status.Text = "请输入要查找的名称或路径。"; query.Focus(); return; }
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); running = source;
        start.IsEnabled = query.IsEnabled = scope.IsEnabled = ignored.IsEnabled = false; cancel.IsEnabled = true;
        results.Clear(); status.Text = "正在读取仓库范围和本地状态…";
        var progress = new Progress<RepositorySearchProgress>(value =>
        {
            if (closed || running != source || source.IsCancellationRequested) return;
            foreach (var entry in value.Matches) results.Add(entry);
            status.Text = $"已读取 {value.DirectoriesRead} 个目录，找到 {results.Count} 项。正在搜索：{value.Directory}";
        });
        try
        {
            var result = await search(scope.SelectedIndex == 0 ? currentRelative : "", query.Text.Trim(), ignored.IsChecked == true, progress, source.Token);
            if (closed) return;
            source.Token.ThrowIfCancellationRequested();
            status.Text = $"搜索完成：{result.MatchCount} 项，已读取 {result.DirectoriesRead} 个目录，仓库快照 r{result.Revision}。"
                + (result.LimitReached ? "已达到 5000 项上限，结果可能不完整，请缩小搜索范围。" : "");
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { if (!closed) status.Text = $"搜索已停止，保留 {results.Count} 项结果（未搜索完整范围）。"; }
        catch (Exception ex) { if (!closed) { status.Text = $"搜索未完成，保留 {results.Count} 项结果：{ex.Message}"; status.ToolTip = ex.Message; } }
        finally
        {
            running = null; source.Dispose();
            if (!closed) { start.IsEnabled = query.IsEnabled = scope.IsEnabled = ignored.IsEnabled = true; cancel.IsEnabled = false; }
        }
    }
}
