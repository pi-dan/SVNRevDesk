using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Desktop.Views;

public enum SearchResultAction { Open, Locate }

public sealed partial class RepositorySearchWindow
{
    private bool performingAction, searchAfterAction;
    private CancellationTokenSource? resultAction;

    private void UpdateControls()
    {
        var enabled = !performingAction && (index != null || running == null);
        start.IsEnabled = query.IsEnabled = scope.IsEnabled = ignored.IsEnabled = enabled;
        grid.IsEnabled = !performingAction;
        var entry = grid.SelectedItem as BrowserEntry;
        openResult.IsEnabled = !performingAction && entry != null && (entry.IsDirectory || ExecuteResultAction != null);
        locateResult.IsEnabled = !performingAction && entry != null;
        if (refreshIndex != null) refreshIndex.IsEnabled = rebuildIndex!.IsEnabled = !performingAction && indexRefresh == null;
        cancel.IsEnabled = running != null || indexRefresh != null || performingAction;
    }

    private void NavigateResult(BrowserEntry entry, SearchResultAction action)
    {
        if (closed || performingAction) return;
        SelectedResult = entry; SelectedAction = action; DialogResult = true;
    }

    private Task OpenResultAsync(BrowserEntry entry)
    {
        if (entry.IsDirectory) { NavigateResult(entry, SearchResultAction.Open); return Task.CompletedTask; }
        return RunResultActionAsync("open", entry);
    }

    private async Task RunResultActionAsync(string operation, BrowserEntry entry)
    {
        if (closed || performingAction || ExecuteResultAction == null) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        resultAction = source; performingAction = true; searchAfterAction = false;
        debounce.Stop(); running?.Cancel(); UpdateControls();
        status.Text = operation == "open" ? $"正在打开：{entry.Name}…" : $"正在显示：{entry.Name}…";
        try
        {
            await ExecuteResultAction(operation, entry, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (!closed) status.Text = $"操作完成：{entry.Name}。";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { if (!closed) status.Text = "操作已停止，保留搜索结果。"; }
        catch (Exception ex) { if (!closed) { status.Text = "操作未完成：" + ex.Message; status.ToolTip = ex.Message; } }
        finally
        {
            resultAction = null; performingAction = false;
            if (!closed) { UpdateControls(); if (searchAfterAction) ScheduleSearch(); }
        }
    }

    private void ConfigureResultMenu()
    {
        var menu = new ContextMenu { Name = "SearchResultMenu" };
        BrowserEntry? target = null;
        var open = Item("OpenSearchResult", "打开", async entry => await OpenResultAsync(entry));
        var locate = Item("LocateSearchResult", "定位到所在目录", entry => { NavigateResult(entry, SearchResultAction.Locate); return Task.CompletedTask; });
        var explorer = Item("ShowSearchResultInExplorer", "在资源管理器中显示", entry => RunResultActionAsync("show-in-explorer", entry));
        menu.Items.Add(new Separator());
        Item("CopySearchRelativePath", "复制相对路径", entry => CopyTextAsync(entry.RelativePath));
        var localPath = Item("CopySearchLocalPath", "复制本地路径", entry => CopyTextAsync(entry.LocalPath!));
        Item("CopySearchUrl", "复制仓库地址", entry => CopyTextAsync(entry.Url));
        menu.Opened += (_, _) =>
        {
            target = grid.SelectedItem as BrowserEntry;
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = target != null && !performingAction;
            open.IsEnabled &= target?.IsDirectory == true || ExecuteResultAction != null;
            explorer.IsEnabled &= target?.LocalPath != null && ExecuteResultAction != null;
            localPath.IsEnabled &= target?.LocalPath != null;
            locate.IsEnabled &= target != null;
        };
        grid.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (performingAction) return;
            if (ItemsControl.ContainerFromElement(grid, args.OriginalSource as DependencyObject) is DataGridRow { Item: BrowserEntry entry } row)
            { grid.SelectedItem = entry; row.Focus(); }
            else grid.UnselectAll();
        };
        grid.ContextMenu = menu;

        MenuItem Item(string name, string header, Func<BrowserEntry, Task> action)
        {
            var item = new MenuItem { Name = name, Header = header };
            item.Click += async (_, _) => { if (!closed && !performingAction && target is { } entry) await action(entry); };
            menu.Items.Add(item); return item;
        }
    }

    private Task CopyTextAsync(string text)
    {
        try { Clipboard.SetText(text); status.Text = "已复制：" + text; }
        catch (Exception ex) { status.Text = "复制未完成：" + ex.Message; status.ToolTip = ex.Message; }
        return Task.CompletedTask;
    }
}
