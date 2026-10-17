using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SVNRevDesk.Desktop.Views;

public sealed class ReverseMergePreviewWindow : Window
{
    public ReverseMergePreviewWindow(Window owner, string target, long revision, string output)
    {
        Owner = owner;
        Title = $"回滚提交 r{revision} · 反向合并预览";
        Width = 820; Height = 560; MinWidth = 580; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel();
        var explanation = new TextBlock { Text = $"目标：{target}\n\n撤销提交 r{revision} 在此范围内引入的变化；后续提交的其他变化由 SVN 尝试保留。当前仅为预览。执行后请检查差异、处理冲突并手动提交。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "执行反向合并", MinWidth = 120 };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, IsDefault = true, MinWidth = 85 });
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new TextBox { Text = string.IsNullOrWhiteSpace(output) ? "没有可合并的变化。" : output,
            IsReadOnly = true, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = panel };
    }
}
