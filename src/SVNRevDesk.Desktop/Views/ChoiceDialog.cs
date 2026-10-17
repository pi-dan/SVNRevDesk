using System.Windows;
using System.Windows.Controls;

namespace SVNRevDesk.Desktop.Views;

public sealed class ChoiceDialog : Window
{
    public int SelectedChoice { get; private set; } = -1;
    public ChoiceDialog(Window owner, string title, string explanation, params string[] choices)
    {
        Owner = owner; Title = title; Width = 580; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) });
        for (var i = 0; i < choices.Length; i++)
        {
            var index = i;
            var button = new Button { Content = choices[i], HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 9, 12, 9) };
            button.Click += (_, _) => { SelectedChoice = index; DialogResult = true; };
            panel.Children.Add(button);
        }
        panel.Children.Add(new Button { Content = "取消", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 85 });
        Content = panel;
    }

    public static int Ask(Window owner, string title, string explanation, params string[] choices)
    {
        var dialog = new ChoiceDialog(owner, title, explanation, choices);
        return dialog.ShowDialog() == true ? dialog.SelectedChoice : -1;
    }
}
