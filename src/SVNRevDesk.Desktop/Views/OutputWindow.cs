using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SVNRevDesk.Desktop.Views;

public sealed class OutputWindow : Window
{
    public OutputWindow(Window owner, string title, string text)
    {
        Owner = owner;
        Title = title;
        Width = 960;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextAlignment = TextAlignment.Left,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 13,
            Margin = new Thickness(12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }
}
