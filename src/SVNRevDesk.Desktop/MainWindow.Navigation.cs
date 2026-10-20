using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Controls;
using SVNRevDesk.Desktop.Views;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private void CopyCurrentDirectoryAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!model.HasSession) return;
        try
        {
            var path = model.CurrentLocalPath;
            if (path == null || !Directory.Exists(path))
            {
                var message = path == null
                    ? "当前文件夹尚未映射到本地磁盘，请先关联本地工作副本或检出到本地。"
                    : "当前文件夹的本地目录不存在或无法访问，请先下载该目录或检查磁盘连接。";
                model.StatusMessage = message;
                MessageBox.Show(this, message, "复制本地路径", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Clipboard.SetText(path);
            model.StatusMessage = "已复制当前目录的本地路径";
        }
        catch (Exception ex)
        {
            model.StatusMessage = "复制地址未完成，请重试。";
            model.AddLog("复制当前目录地址失败：" + ex.Message);
        }
    }

    private void OpenCurrentDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (model.CurrentLocalPath is not { } path) return;
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("当前目录尚未下载、本地目录已删除或磁盘不可用。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            model.StatusMessage = "已在资源管理器中打开当前目录";
        }
        catch (Exception ex)
        {
            model.StatusMessage = "打开当前目录未完成：" + ex.Message;
            model.AddLog(model.StatusMessage);
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanOperate || model.Session == null) return;
        await HandleAsync("跨目录搜索", async token =>
        {
            var selectedAction = SearchResultAction.Locate;
            var entry = model.WaitForUserInput(() =>
            {
                var window = new RepositorySearchWindow(this, model.Session.Url, model.CurrentRelative, model.GetSearchIndex(), token);
                var searchActions = new ClientActions(window, model);
                window.ExecuteResultAction = async (operation, target, stop) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop);
                    try
                    {
                        if (operation == "open") await searchActions.OpenSearchResultAsync(target, linked.Token);
                        else await searchActions.ExecuteAsync(operation, [target], linked.Token);
                    }
                    catch (Exception ex) { model.AddLog(ex.Message); throw; }
                };
                if (window.ShowDialog() != true) return null;
                selectedAction = window.SelectedAction;
                return window.SelectedResult;
            });
            if (entry == null) return;
            var slash = entry.RelativePath.LastIndexOf('/');
            await model.NavigateAsync(slash < 0 ? "" : entry.RelativePath[..slash], token);
            model.FilterText = ""; model.DifferencesOnly = false; model.SelectedState = "全部状态";
            if (!model.Entries.Any(item => item.RelativePath == entry.RelativePath))
                await model.RefreshCurrentDirectoryAsync(token);
            var actual = model.Entries.FirstOrDefault(item => item.RelativePath == entry.RelativePath)
                ?? throw new InvalidOperationException("此项目已变化或删除，请重新搜索。");
            if (selectedAction == SearchResultAction.Open && actual.IsDirectory)
            {
                await model.NavigateAsync(actual.RelativePath, token);
                return;
            }
            if (model.IsCardView) { FileCards.SelectedItem = actual; FileCards.ScrollIntoView(actual); }
            else { FileGrid.SelectedItem = actual; FileGrid.ScrollIntoView(actual); }
        });
    }

    private void UpdateBreadcrumbs()
    {
        CurrentRelativePath.Inlines.Clear();
        if (!model.HasSession)
        {
            CurrentRelativePath.Inlines.Add(new Run(model.CurrentRelativePathText));
            return;
        }
        AddBreadcrumb("根目录", "");
        var relative = "";
        foreach (var name in model.CurrentRelative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            relative = relative.Length == 0 ? name : relative + "/" + name;
            CurrentRelativePath.Inlines.Add(new Run(" / ") { Foreground = (System.Windows.Media.Brush)FindResource("MutedText") });
            AddBreadcrumb(name, relative);
        }
    }

    private void AddBreadcrumb(string name, string relative)
    {
        if (relative == model.CurrentRelative)
        {
            CurrentRelativePath.Inlines.Add(new Run(name));
            return;
        }
        var link = new Hyperlink(new Run(name))
        {
            Tag = relative,
            Style = (Style)FindResource("BreadcrumbLinkStyle"),
            ToolTip = "跳转到：" + (relative.Length == 0 ? "根目录" : "根目录 / " + relative.Replace("/", " / "))
        };
        link.SetBinding(Hyperlink.IsEnabledProperty, new Binding(nameof(MainViewModel.CanOperate)) { Source = model });
        link.Click += Breadcrumb_Click;
        CurrentRelativePath.Inlines.Add(link);
    }

    private async void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string relative } || !model.CanOperate || !model.HasSession
            || relative == model.CurrentRelative) return;
        await model.BrowseDirectoryAsync(relative);
    }
}
