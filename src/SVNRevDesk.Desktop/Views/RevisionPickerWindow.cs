using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Desktop.Views;

public class RevisionPickerWindow : Window
{
    private readonly Func<long?, long?, CancellationToken, Task<HistoryPage>> loadPage;
    private readonly CancellationTokenSource lifetime;
    private readonly ObservableCollection<HistoryEntry> history = [];
    private readonly ObservableCollection<string> revisions = [];
    private readonly ComboBox revisionInput;
    private readonly TextBox search;
    private readonly DataGrid historyGrid;
    private readonly TextBox details;
    private readonly TextBlock status;
    private readonly TextBlock validation;
    private readonly Button more;
    private readonly long minimumRevision;
    private long? nextStart;
    private long? peg;
    private bool loading;
    private bool closed;
    private bool synchronizing;
    private readonly bool browseOnly;

    public long SelectedRevision { get; private set; }

    public RevisionPickerWindow(Window owner, string title, string explanation, string historyScope,
        string initialRevision, bool allowZero,
        Func<long?, long?, CancellationToken, Task<HistoryPage>> loadPage, CancellationToken token, bool browseOnly = false)
    {
        Owner = owner; Title = title; Width = 960; Height = 680; MinWidth = 700; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.loadPage = loadPage; this.browseOnly = browseOnly; minimumRevision = allowZero ? 0 : 1;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panel = new DockPanel();
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        top.Children.Add(new TextBlock { Text = "目标修订号（下拉选择或直接输入数字）", Margin = new Thickness(0, 0, 0, 5), Visibility = browseOnly ? Visibility.Collapsed : Visibility.Visible });
        revisionInput = new ComboBox { Name = "RevisionInput", IsEditable = true, IsTextSearchEnabled = false,
            ItemsSource = revisions, MaxDropDownHeight = 260, MinWidth = 210, HorizontalAlignment = HorizontalAlignment.Left,
            Text = long.TryParse(initialRevision, out _) ? initialRevision : "", Visibility = browseOnly ? Visibility.Collapsed : Visibility.Visible };
        top.Children.Add(revisionInput);
        validation = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6), Visibility = browseOnly ? Visibility.Collapsed : Visibility.Visible };
        top.Children.Add(validation);
        top.Children.Add(new TextBlock { Text = browseOnly ? "提交历史 · 选择记录查看完整说明" : "目标提交历史 · 点击记录可填入修订号", FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Name = "RevisionHistoryScope", Text = historyScope, ToolTip = historyScope, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.SlateGray, Margin = new Thickness(0, 3, 0, 5) });
        top.Children.Add(new TextBlock { Text = "仅列出影响目标文件或文件夹（含子项）的可见提交，多选时合并去重。修订号为仓库统一编号，可能不连续。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 8) });
        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        filterRow.Children.Add(new TextBlock { Text = "筛选已加载记录：", VerticalAlignment = VerticalAlignment.Center });
        search = new TextBox { Name = "HistorySearch", ToolTip = "按修订号、提交人、时间或说明筛选；未加载的记录请先加载更早历史。" };
        filterRow.Children.Add(search); top.Children.Add(filterRow);

        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var paging = new DockPanel { Margin = new Thickness(0, 8, 0, 5) };
        more = new Button { Name = "LoadEarlier", Content = "加载更早的 100 条", HorizontalAlignment = HorizontalAlignment.Right };
        more.Click += async (_, _) => await LoadMoreAsync();
        DockPanel.SetDock(more, Dock.Right); paging.Children.Add(more);
        status = new TextBlock { Name = "HistoryStatus", TextWrapping = TextWrapping.Wrap, MaxHeight = 60, VerticalAlignment = VerticalAlignment.Center };
        paging.Children.Add(status); bottom.Children.Add(paging);
        details = new TextBox { Name = "CommitDetails", IsReadOnly = true, AcceptsReturn = true, Height = 96,
            VerticalContentAlignment = VerticalAlignment.Top, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = "选择记录后查看完整提交说明。" };
        bottom.Children.Add(details);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var accept = new Button { Name = "AcceptRevision", Content = "下一步", IsDefault = true, MinWidth = 90 };
        accept.Click += (_, _) => Accept();
        if (!browseOnly) buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = browseOnly ? "关闭" : "取消", IsCancel = true, MinWidth = 85 });
        bottom.Children.Add(buttons);

        historyGrid = new DataGrid { Name = "RevisionHistory", ItemsSource = CollectionViewSource.GetDefaultView(history),
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow };
        AddColumn("修订号", nameof(HistoryEntry.Revision), 85);
        AddColumn("提交人", nameof(HistoryEntry.Author), 130);
        AddColumn("时间", nameof(HistoryEntry.DateText), 160);
        AddColumn("提交说明", nameof(HistoryEntry.Message), new DataGridLength(1, DataGridLengthUnitType.Star));
        panel.Children.Add(historyGrid);
        Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = panel };
        var view = CollectionViewSource.GetDefaultView(history);
        view.Filter = item => item is HistoryEntry entry && Matches(entry, search.Text.Trim());
        search.TextChanged += (_, _) => { view.Refresh(); if (browseOnly) SyncSelection(); UpdateStatus(); };
        historyGrid.SelectionChanged += (_, _) =>
        {
            if (synchronizing || historyGrid.SelectedItem is not HistoryEntry entry) return;
            if (!browseOnly) revisionInput.Text = entry.Revision.ToString(CultureInfo.InvariantCulture);
            ShowDetails(entry);
        };
        revisionInput.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (synchronizing) return;
            validation.Text = "";
            SyncSelection();
        }));
        Loaded += async (_, _) => { if (browseOnly) search.Focus(); else revisionInput.Focus(); await LoadMoreAsync(); };
        Closed += (_, _) => { closed = true; lifetime.Cancel(); if (!loading) lifetime.Dispose(); };

        void AddColumn(string header, string property, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            historyGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = width, MinWidth = width.IsStar ? 200 : 40, ElementStyle = style });
        }
    }

    private void Accept()
    {
        if (!long.TryParse(revisionInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < minimumRevision)
        {
            validation.Text = minimumRevision == 0 ? "请输入非负整数修订号，例如 123。" : "请输入正整数提交修订号，例如 123。";
            revisionInput.Focus(); return;
        }
        SelectedRevision = revision; DialogResult = true;
    }

    private async Task LoadMoreAsync()
    {
        if (loading || closed || peg != null && nextStart == null) return;
        loading = true; more.IsEnabled = false; status.Text = browseOnly ? "正在读取提交历史…" : "正在读取提交历史…（仍可手动输入修订号）";
        status.ToolTip = null;
        try
        {
            var page = await loadPage(nextStart, peg, lifetime.Token);
            if (closed || lifetime.IsCancellationRequested) return;
            foreach (var entry in page.Entries)
            {
                history.Add(entry);
                if (entry.Revision >= minimumRevision) revisions.Add(entry.Revision.ToString(CultureInfo.InvariantCulture));
            }
            peg = page.PegRevision; nextStart = page.NextStartRevision;
            more.Content = nextStart == null ? "已加载全部可见记录" : "加载更早的 100 条";
            SyncSelection(); UpdateStatus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (closed || lifetime.IsCancellationRequested) return;
            status.Text = (browseOnly ? "读取失败，可重试。" : "读取失败，可重试或手动输入修订号。") + ex.Message;
            status.ToolTip = ex.Message;
            more.Content = "重试读取历史";
        }
        finally
        {
            loading = false;
            if (closed) lifetime.Dispose();
            else more.IsEnabled = peg == null || nextStart != null;
        }
    }

    private void SyncSelection()
    {
        if (browseOnly)
        {
            if (historyGrid.SelectedItem is not HistoryEntry) historyGrid.SelectedItem = historyGrid.Items.Cast<HistoryEntry>().FirstOrDefault();
            if (historyGrid.SelectedItem is HistoryEntry selected) ShowDetails(selected);
            else details.Text = "选择记录后查看完整提交说明。";
            return;
        }
        synchronizing = true;
        try
        {
            var entry = long.TryParse(revisionInput.Text.Trim(), out var revision) ? history.FirstOrDefault(h => h.Revision == revision) : null;
            historyGrid.SelectedItem = entry;
            if (entry != null) ShowDetails(entry);
            else details.Text = "可直接输入未列出的修订号；SVN 会在下一步检查修订号是否存在。";
        }
        finally { synchronizing = false; }
    }

    private void ShowDetails(HistoryEntry entry) => details.Text = $"r{entry.Revision}  {entry.Author}  {entry.DateText}\n{entry.Message}";

    private void UpdateStatus()
    {
        status.Text = $"已加载 {history.Count} 条，筛选显示 {historyGrid.Items.Count} 条。"
            + (peg == null ? "" : $"查询截止修订号：r{peg}。")
            + (peg != null && nextStart == null ? "已到最早可见记录。" : "");
    }

    private static bool Matches(HistoryEntry entry, string query) => query.Length == 0
        || entry.Revision.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)
        || ("r" + entry.Revision.ToString(CultureInfo.InvariantCulture)).Equals(query, StringComparison.OrdinalIgnoreCase)
        || entry.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.DateText.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase);
}
