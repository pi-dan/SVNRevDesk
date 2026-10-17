using System.Windows;

namespace SVNRevDesk.Desktop.Views;

public sealed record FileConflictChoice(bool ReplaceExisting, bool BackupBeforeOverwrite);

public partial class FileConflictWindow : Window
{
    public FileConflictChoice? Choice { get; private set; }
    public bool BackupBeforeOverwrite => BackupBeforeOverwriteBox.IsChecked == true;

    public FileConflictWindow(Window? owner, string title, string directory, IReadOnlyList<string> names,
        string explanation, bool canOverwrite = true, bool canBackup = true)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        Title = title;
        TargetPath.Text = "目标目录：" + directory;
        TargetPath.ToolTip = directory;
        FileNames.Text = string.Join("\n", names);
        Explanation.Text = explanation;
        OverwriteButton.IsEnabled = canOverwrite;
        BackupBeforeOverwriteBox.IsEnabled = canOverwrite && canBackup;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        Choice = new(true, BackupBeforeOverwrite);
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Choice = new(false, BackupBeforeOverwrite);
        DialogResult = true;
    }
}
