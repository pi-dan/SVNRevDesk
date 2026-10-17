using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
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
