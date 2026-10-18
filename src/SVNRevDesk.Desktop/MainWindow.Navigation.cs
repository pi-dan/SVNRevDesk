using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Controls;
using SVNRevDesk.Desktop.Views;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (!model.CanOperate || model.Session == null) return;
        await HandleAsync("跨目录搜索", async token =>
        {
            var entry = model.WaitForUserInput(() =>
            {
                var window = new RepositorySearchWindow(this, model.Session.Url, model.CurrentRelative, model.SearchAsync, token);
                return window.ShowDialog() == true ? window.SelectedResult : null;
            });
            if (entry == null) return;
            var slash = entry.RelativePath.LastIndexOf('/');
            await model.NavigateAsync(slash < 0 ? "" : entry.RelativePath[..slash], token);
            model.FilterText = ""; model.DifferencesOnly = false; model.SelectedState = "全部状态";
            var actual = model.Entries.FirstOrDefault(item => item.RelativePath == entry.RelativePath)
                ?? throw new InvalidOperationException("此项目已变化或删除，请重新搜索。");
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
