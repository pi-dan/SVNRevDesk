using System.Windows;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.Views;

public partial class CloseWindowDialog : Window
{
    public WindowCloseBehavior SelectedBehavior => ExitOption.IsChecked == true
        ? WindowCloseBehavior.Exit : WindowCloseBehavior.MinimizeToTray;
    public bool RememberChoice => RememberChoiceBox.IsChecked == true;

    public CloseWindowDialog(Window? owner)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
