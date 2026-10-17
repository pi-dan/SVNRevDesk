using System.Windows;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.Desktop.Views;

public partial class CommitWindow : Window
{
    public CommitViewModel Model { get; }

    public CommitWindow(Window? owner, string localRoot, IReadOnlyList<CommitChange> changes, string? repositoryUrl = null)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        Model = new CommitViewModel(localRoot, changes, repositoryUrl);
        DataContext = Model;
        Loaded += (_, _) => MessageBox.Focus();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Model.SelectAll(true);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Model.SelectAll(false);
    private void SelectVersioned_Click(object sender, RoutedEventArgs e) => Model.SelectVersioned();
    private void SelectUnversioned_Click(object sender, RoutedEventArgs e) => Model.SelectUnversioned();
    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (Model.CanSubmit) DialogResult = true;
    }
}
