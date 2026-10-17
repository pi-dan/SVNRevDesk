using System.Windows;
using System.Windows.Controls;

namespace SVNRevDesk.Desktop.Views;

public sealed record InputField(string Label, string Value = "", bool Multiline = false, bool Password = false);

// Small operation dialogs share layout, while their labels and validation stay at the call site.
public sealed class InputDialog : Window
{
    private readonly List<Control> inputs = [];
    public IReadOnlyList<string> Values => inputs.Select(input => input is PasswordBox password ? password.Password : ((TextBox)input).Text).ToList();

    public InputDialog(Window owner, string title, string explanation, params InputField[] fields)
    {
        Owner = owner;
        Title = title;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 8, 0, 5), FontWeight = FontWeights.SemiBold });
            Control input = field.Password ? new PasswordBox { Password = field.Value, Padding = new Thickness(7) }
                : new TextBox { Text = field.Value, AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Height = field.Multiline ? 100 : double.NaN, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            inputs.Add(input);
            panel.Children.Add(input);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 85 };
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 85 });
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => inputs.FirstOrDefault()?.Focus();
    }

    public static IReadOnlyList<string>? Ask(Window owner, string title, string explanation, params InputField[] fields)
    {
        var dialog = new InputDialog(owner, title, explanation, fields);
        return dialog.ShowDialog() == true ? dialog.Values : null;
    }
}
