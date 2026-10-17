using System.Windows;
using Microsoft.Win32;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Services;

// All dialogs inside an operation pause its progress display and exclude input time from elapsed time.
public sealed class OperationDialogs(Window owner, MainViewModel model)
{
    public IReadOnlyList<string>? Ask(string title, string explanation, params InputField[] fields) =>
        model.WaitForUserInput(() => InputDialog.Ask(owner, title, explanation, fields), result => result == null);

    public int Choose(string title, string explanation, params string[] choices) =>
        model.WaitForUserInput(() => ChoiceDialog.Ask(owner, title, explanation, choices), result => result < 0);

    public bool Confirm(string title, string message) => model.WaitForUserInput(
        () => MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes, result => !result);

    public bool ChooseFolder(OpenFolderDialog dialog) =>
        model.WaitForUserInput(() => dialog.ShowDialog(owner) == true, result => !result);

    public bool ShowCommit(CommitWindow dialog) =>
        model.WaitForUserInput(() => dialog.ShowDialog() == true, result => !result);

    public FileConflictChoice? ResolveFileConflicts(string title, string directory, IReadOnlyList<string> names,
        string explanation, bool canOverwrite = true, bool canBackup = true) => model.WaitForUserInput(() =>
        {
            var dialog = new FileConflictWindow(owner, title, directory, names, explanation, canOverwrite, canBackup);
            return dialog.ShowDialog() == true ? dialog.Choice : null;
        }, result => result == null);

    public FileMoveOptions? ResolveMoveConflicts(FileMovePlan plan)
    {
        if (plan.Conflicts.Count == 0) return new();
        var names = plan.Conflicts.Select(item => System.IO.Path.GetFileName(item.Destination)
            + (!item.CanOverwrite ? " — " + item.OverwriteUnavailableReason : item.IsReverseMove
                ? " — 移回原位置，保留本地修改。" : item.DestinationSize == null
                    ? " — 本地文件已删除或缺失，可重新放入此位置。" : "")).ToArray();
        var choice = ResolveFileConflicts("移动时存在同名项目", plan.DestinationDirectory, names,
            "请选择此次移动的处理方式。同名文件可覆盖，已删除或缺失的文件可重新放入；仅备份实际存在的目标内容。同名目录、类型不同或尚需处理 SVN 状态的项目会跳过。移动不会自动提交。",
            plan.Conflicts.Any(item => item.CanOverwrite),
            plan.Conflicts.Any(item => item.CanOverwrite && item.DestinationSize.HasValue));
        return choice == null ? null : new(choice.ReplaceExisting ? FileMoveConflictBehavior.OverwriteFiles : FileMoveConflictBehavior.Skip,
            choice.BackupBeforeOverwrite);
    }

    public void Inform(string title, string message) => model.WaitForUserInput(
        () => MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information));
}
