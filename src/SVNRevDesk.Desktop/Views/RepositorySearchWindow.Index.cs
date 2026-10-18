using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Views;

public sealed partial class RepositorySearchWindow
{
    private readonly RepositorySearchIndex? index;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? indexRefresh;
    private TextBlock? indexStatus;
    private Button? refreshIndex, rebuildIndex;

    public RepositorySearchWindow(Window owner, string openedUrl, string currentRelative, RepositorySearchIndex index, CancellationToken token)
        : this(owner, openedUrl, currentRelative,
            (scope, query, ignored, progress, stop) => Task.Run(() => index.SearchAsync(scope, query, ignored, progress, stop), stop), token)
    {
        this.index = index;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        refreshIndex = new Button { Name = "RefreshIndex", Content = "刷新索引", ToolTip = "检查服务器变化并更新本地清单" };
        rebuildIndex = new Button { Name = "RebuildIndex", Content = "重建索引", ToolTip = "重新读取完整服务器清单并核对本地文件" };
        DockPanel.SetDock(rebuildIndex, Dock.Right); row.Children.Add(rebuildIndex);
        DockPanel.SetDock(refreshIndex, Dock.Right); row.Children.Add(refreshIndex);
        indexStatus = new TextBlock { Name = "IndexStatus", Text = "正在载入本地索引…", Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxHeight = 90 };
        row.Children.Add(indexStatus); header.Children.Add(row);
        status.Text = "输入名称或路径即可搜索本地索引。首次建立清单需要读取服务器；最多显示 5000 项。";
        refreshIndex.Click += async (_, _) => await RefreshIndexAsync(false);
        rebuildIndex.Click += async (_, _) => await RefreshIndexAsync(true);
        debounce.Tick += async (_, _) => { debounce.Stop(); if (query.Text.Trim().Length > 0) await SearchAsync(); };
        query.TextChanged += (_, _) => ScheduleSearch();
        scope.SelectionChanged += (_, _) => ScheduleSearch();
        ignored.Checked += (_, _) => ScheduleSearch(); ignored.Unchecked += (_, _) => ScheduleSearch();
        index.Changed += IndexChanged;
        Loaded += async (_, _) => await RefreshIndexAsync(false);
    }

    private void ScheduleSearch()
    {
        if (closed) return;
        running?.Cancel(); debounce.Stop();
        if (query.Text.Trim().Length == 0) { results.Clear(); status.Text = "输入名称或路径即可搜索本地索引。"; return; }
        debounce.Start();
    }

    private void IndexChanged()
    {
        if (closed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            ShowIndexStatus(); ScheduleSearch();
        });
    }

    private void ShowIndexStatus()
    {
        if (index == null || indexStatus == null) return;
        var value = index.Status;
        indexStatus.Text = value.HasIndex
            ? $"本地清单 {value.EntryCount} 项 · r{value.Revision}\n服务器核对：{Time(value.ServerCheckedAt)} · 本地核对：{Time(value.LocalCheckedAt)}"
            : "尚未建立索引。首次建立需连接服务器。";
        if (indexRefresh != null) indexStatus.Text += value.HasIndex ? "\n正在后台检查服务器，仍可搜索缓存。" : "\n正在建立索引…";
        if (value.NeedsRebuild) indexStatus.Text += "\n工作副本映射已变化，请刷新索引。";
        if (value.Error.Length > 0) indexStatus.Text += "\n使用缓存；" + value.Error;
        indexStatus.ToolTip = indexStatus.Text;
        static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "未核对";
    }

    private async Task RefreshIndexAsync(bool rebuild)
    {
        if (closed || index == null || indexRefresh != null) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        indexRefresh = source; refreshIndex!.IsEnabled = rebuildIndex!.IsEnabled = false; cancel.IsEnabled = true;
        ShowIndexStatus();
        try { await Task.Run(() => index.RefreshAsync(rebuild, token: source.Token), source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { if (!closed) status.Text = "索引更新已停止，保留上次清单。"; }
        catch (Exception ex) { if (!closed) { status.Text = "索引更新失败，可搜索已有缓存：" + ex.Message; status.ToolTip = ex.Message; } }
        finally
        {
            indexRefresh = null;
            if (!closed) { refreshIndex.IsEnabled = rebuildIndex.IsEnabled = true; cancel.IsEnabled = running != null; ShowIndexStatus(); }
        }
    }
}
