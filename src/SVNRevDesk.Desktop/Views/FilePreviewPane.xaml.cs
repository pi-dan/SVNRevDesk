using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.Desktop.Views;

public partial class FilePreviewPane : UserControl
{
    public FilePreviewPane() => InitializeComponent();
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is FilePreviewViewModel model) model.IsEnabled = false;
    }
}
