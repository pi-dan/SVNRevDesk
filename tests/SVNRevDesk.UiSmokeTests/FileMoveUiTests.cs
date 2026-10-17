using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class FileMoveUiTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "move-fixture");
        Directory.CreateDirectory(root);
        var repository = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repository);
        using (var process = Process.Start(start)!)
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var local = Path.Combine(root, "中文 @ 工作副本");
        var source = Path.Combine(local, "源目录");
        var target = Path.Combine(local, "目标目录", "未访问子目录");
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        await operations.CheckoutAsync(url, local, false, CancellationToken.None);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(source, "受控子目录"));
        File.WriteAllText(Path.Combine(source, "受控子目录", "child.txt"), "server child");
        File.WriteAllText(Path.Combine(source, "受控文件.txt"), "server content");
        await operations.AddAsync([source, Path.Combine(local, "目标目录")], CancellationToken.None);
        await operations.CommitAsync([local], "UI move fixture", CancellationToken.None);
        File.AppendAllText(Path.Combine(source, "受控文件.txt"), " plus edits");
        File.AppendAllText(Path.Combine(source, "受控子目录", "child.txt"), " plus child edits");
        Directory.CreateDirectory(Path.Combine(source, "本地新增目录"));
        File.WriteAllText(Path.Combine(source, "本地新增目录", "new.txt"), "local new content");
        var service = new LocalOnlyRepository(new RepositoryService(runner));
        var settings = new UserSettings(Path.Combine(root, "settings")) { CloseBehavior = WindowCloseBehavior.Exit };
        var window = new MainWindow(settings, service);
        using var model = (MainViewModel)window.DataContext;
        await model.OpenAsync(local, true, CancellationToken.None);
        await model.BrowseDirectoryAsync("源目录");
        await model.BrowseDirectoryAsync("源目录/受控子目录");
        await model.BrowseDirectoryAsync("源目录/本地新增目录");
        await model.BrowseDirectoryAsync("源目录");
        var grid = (DataGrid)window.FindName("FileGrid");
        var cards = (ListBox)window.FindName("FileCards");
        var tree = (TreeView)window.FindName("DirectoryTree");
        render(window, Path.Combine(output, "move-before.png"), 1360, 780);
        var keyFilter = typeof(MainWindow).GetMethod("IsFileClipboardKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var browser in new DependencyObject[] { grid, cards, tree })
            Check((bool)keyFilter.Invoke(window, [Key.X, ModifierKeys.Control, browser])!, "Ctrl+X is recognized in all three browser surfaces");
        Check(!(bool)keyFilter.Invoke(window, [Key.X, ModifierKeys.Control, window.FindName("NameFilterBox")])!, "Ctrl+X in a text box keeps normal text-cut behavior");
        grid.SelectedItems.Add(model.Entries.Single(entry => entry.Name == "受控文件.txt"));
        grid.ContextMenu.PlacementTarget = grid;
        grid.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Check(grid.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "file-cut").IsEnabled
            && grid.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag is "move-to").IsEnabled, "File menus expose enabled Cut and Move to folder commands");
        var paths = model.Entries.Select(entry => entry.LocalPath!).ToArray();
        var data = FileClipboard.CreateCutData(paths);
        Check(FileClipboard.IsMove(data) && FileClipboard.ReadFiles(data).SequenceEqual(paths)
            && !data.GetDataPresent(DataFormats.FileDrop) && paths.All(path => File.Exists(path) || Directory.Exists(path)),
            "Internal cut uses original paths, makes no changes and prevents Explorer from bypassing SVN history");
        var lastFull = model.LastFullRefreshAt;
        var revision = model.Snapshot!.Revision;
        var checkedAt = model.DirectoryCacheText;
        var readCount = service.Reads;
        service.RejectReads = true;
        var plan = await model.Operations.PrepareFileMoveAsync(FileClipboard.ReadFiles(data), target, local, CancellationToken.None);
        await model.RunAsync("移动文件", token => model.MoveLocalFilesAsync(plan, false, token));
        Check(model.CurrentRelative == "目标目录/未访问子目录" && model.Entries.Count == 3
            && model.Entries.All(entry => entry.State == FileState.LocalAdded)
            && File.ReadAllText(Path.Combine(target, "受控文件.txt")) == "server content plus edits", "Moving into an unvisited directory navigates locally and displays all destination additions");
        Check(model.Snapshot.Statuses[Path.Combine(source, "受控文件.txt")].Item == "deleted"
            && !model.Snapshot.Statuses.ContainsKey(Path.Combine(source, "本地新增目录"))
            && !model.Snapshot.Statuses.ContainsKey(Path.Combine(source, "本地新增目录", "new.txt")), "Move refresh retains SVN deletions and removes vanished unversioned source statuses");
        Check(service.Reads == readCount && model.LastFullRefreshAt == lastFull && model.Snapshot.Revision == revision
            && !model.IsBusy && ((FrameworkElement)window.FindName("OperationProgress")).Visibility == Visibility.Collapsed,
            "Move refresh performs no server reads, preserves automatic refresh metadata and ends progress");
        Check(model.FindNode("目标目录/未访问子目录/受控子目录") != null
            && model.FindNode("源目录/本地新增目录") == null, "Both tree branches update and old local-only directory nodes disappear");
        render(window, Path.Combine(output, "move-destination.png"), 1360, 780);
        var changes = await model.Operations.ReadCommitChangesAsync([Path.Combine(target, "受控子目录")], local, CancellationToken.None);
        var commit = new CommitViewModel(local, changes);
        commit.SelectAll(false);
        var child = commit.Items.Single(item => item.Change.Path == Path.Combine(target, "受控子目录", "child.txt"));
        child.IsChecked = true;
        Check(commit.Items.Where(item => !item.Change.NeedsAddition).All(item => item.IsChecked), "Selecting a modified moved child also selects its new parent and original deletion transitively");
        commit.Items.Single(item => item.Change.Path == Path.Combine(source, "受控子目录")).IsChecked = false;
        Check(commit.SelectedCount == 0, "Unchecking either end of a directory move also unchecks its dependent children");
        await model.BrowseDirectoryAsync("目标目录/未访问子目录/受控子目录");
        Check(model.Entries.Single().State == FileState.LocalAdded, "Children of a newly moved directory are additions at their new server paths, rather than server deletions");
        await model.BrowseDirectoryAsync("源目录");
        Check(model.Entries.Count == 2 && model.Entries.All(entry => entry.State == FileState.LocalDeleted)
            && model.DirectoryCacheText == checkedAt, "The cached source directory immediately shows only the scheduled SVN deletions with its original check time");
        render(window, Path.Combine(output, "move-source.png"), 1360, 780);
        await model.BrowseDirectoryAsync("源目录/受控子目录");
        Check(model.Entries.Single().State == FileState.LocalDeleted, "Cached descendants of a moved directory remain browsable as scheduled deletions");

        var outside = Path.Combine(root, "explorer-cut.txt");
        File.WriteAllText(outside, "Explorer move");
        var explorer = FileClipboard.CreateData([outside], move: true);
        Check(FileClipboard.IsMove(explorer) && !FileClipboard.IsMove(FileClipboard.CreateData([outside])), "Explorer's preferred move effect is distinguished from copying");
        plan = await model.Operations.PrepareFileMoveAsync(FileClipboard.ReadFiles(explorer), source, local, CancellationToken.None);
        await model.RunAsync("移动文件", token => model.MoveLocalFilesAsync(plan, false, token));
        Check(!File.Exists(outside) && model.Entries.Single(entry => entry.Name == "explorer-cut.txt").State == FileState.LocalAdded
            && service.Reads == readCount, "Explorer cut sources move into the working copy as unversioned files using local refresh only");
        var rootEntry = model.GetDirectoryEntry("")!;
        Check(!BrowserActionAvailability.CanExecute("move-to", model.Session, [rootEntry]), "The opened root cannot be cut or moved");
        var deleted = model.Entries.First(entry => entry.State == FileState.LocalDeleted);
        Check(!BrowserActionAvailability.CanExecute("move-to", model.Session, [deleted]), "Pending deletions cannot be cut again");
        var overwriteSource = Path.Combine(source, "覆盖测试.txt");
        var overwriteTarget = Path.Combine(target, "覆盖测试.txt");
        File.WriteAllText(overwriteSource, "new move contents");
        File.WriteAllText(overwriteTarget, "old target contents");
        plan = await model.Operations.PrepareFileMoveAsync([overwriteSource], target, local, CancellationToken.None);
        await model.RunAsync("移动文件", async token =>
        {
            model.WaitForUserInput(() =>
            {
                var dialog = new FileConflictWindow(null, "移动时存在同名项目", target, ["覆盖测试.txt"],
                    "请选择此次移动的处理方式。同名文件可覆盖；目录会跳过，源项目保留。移动不会自动提交。");
                Check(!dialog.BackupBeforeOverwrite && ((Button)dialog.FindName("OverwriteButton")).IsEnabled
                    && ((Button)dialog.FindName("SkipButton")).IsEnabled && dialog.Choice == null,
                    "Conflict dialog offers overwrite, skip and cancellation with backup unchecked initially");
                Check(!model.IsOperationProgressVisible && ((FrameworkElement)window.FindName("OperationProgress")).Visibility == Visibility.Collapsed,
                    "The overwrite-choice dialog pauses progress before any replacement");
                ((CheckBox)dialog.FindName("BackupBeforeOverwriteBox")).IsChecked = true;
                Check(dialog.BackupBeforeOverwrite, "Backup remains available when explicitly selected");
                render(dialog, Path.Combine(output, "move-conflict-backup.png"), 620, 340);
                ((CheckBox)dialog.FindName("BackupBeforeOverwriteBox")).IsChecked = false;
                Check(!dialog.BackupBeforeOverwrite && dialog.Choice == null, "Disabling backup changes the choice without modifying files");
                render(dialog, Path.Combine(output, "move-conflict-direct.png"), 620, 340);
                Check(File.ReadAllText(overwriteSource) == "new move contents" && File.ReadAllText(overwriteTarget) == "old target contents",
                    "Viewing or cancelling overwrite choices leaves both files unchanged");
                return true;
            });
            await model.MoveLocalFilesAsync(plan, new FileMoveOptions(FileMoveConflictBehavior.OverwriteFiles, BackupBeforeOverwrite: false), token);
        });
        Check(!File.Exists(overwriteSource) && File.ReadAllText(overwriteTarget) == "new move contents"
            && model.Entries.Single(entry => entry.Name == "覆盖测试.txt").Size == new FileInfo(overwriteTarget).Length
            && service.Reads == readCount && !Directory.Exists(settings.BackupDirectory),
            "A direct overwrite immediately refreshes the target view, creates no backups and performs no server checks");
        var directoryOnly = new FileConflictWindow(null, "移动时存在同名项目", target, ["同名文件夹"], "同名文件夹会跳过。", canOverwrite: false);
        Check(!((Button)directoryOnly.FindName("OverwriteButton")).IsEnabled
            && !((CheckBox)directoryOnly.FindName("BackupBeforeOverwriteBox")).IsEnabled
            && ((Button)directoryOnly.FindName("SkipButton")).IsEnabled, "Directory-only conflicts clearly allow skipping and cancellation without an overwrite action");
        var moveBackSource = Path.Combine(target, "受控文件.txt");
        var originalFile = Path.Combine(source, "受控文件.txt");
        plan = await model.Operations.PrepareFileMoveAsync([moveBackSource], source, local, CancellationToken.None);
        Check(plan.Conflicts.Single().CanOverwrite && plan.Items.Single().IsReverseMove,
            "A listed local deletion permits moving the same file back to its original position");
        await model.RunAsync("移动文件", async token =>
        {
            model.WaitForUserInput(() =>
            {
                var dialog = new FileConflictWindow(null, "移动时存在同名项目", source,
                    ["受控文件.txt — 移回原位置，保留本地修改。"],
                    "已删除或缺失的文件可重新放入；仅备份实际存在的目标内容。移动不会自动提交。",
                    canOverwrite: plan.Items.Single().CanOverwrite, canBackup: false);
                Check(((Button)dialog.FindName("OverwriteButton")).IsEnabled && !dialog.BackupBeforeOverwrite
                    && !((CheckBox)dialog.FindName("BackupBeforeOverwriteBox")).IsEnabled
                    && !model.IsOperationProgressVisible,
                    "Deleted-target dialog enables overwrite, explains the move-back and needs no content backup or waiting progress");
                render(dialog, Path.Combine(output, "move-deleted-target.png"), 620, 340);
                return true;
            });
            await model.MoveLocalFilesAsync(plan, new FileMoveOptions(FileMoveConflictBehavior.OverwriteFiles), token);
        });
        Check(File.ReadAllText(originalFile) == "server content plus edits" && !File.Exists(moveBackSource)
            && model.Entries.Single(entry => entry.Name == "受控文件.txt").State == FileState.LocalModified
            && model.Snapshot!.Statuses[originalFile].MovedTo == null && model.Snapshot.Statuses[originalFile].MovedFrom == null
            && service.Reads == readCount && model.LastFullRefreshAt == lastFull
            && !Directory.Exists(settings.BackupDirectory),
            "Moving back refreshes the original path to modified with no move relationship, content backup or server read");
        CommitWindowTests.Run(output, render);
        Console.WriteLine("PASS: Cut formats and shortcuts, menu availability, SVN history-aware moves, both local tree/list refreshes, unvisited targets, missing source caches, Explorer incoming moves and no server checks.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class LocalOnlyRepository(IRepositoryService inner) : IRepositoryService
    {
        public int Reads { get; private set; }
        public bool RejectReads { get; set; }
        private void Read() { if (RejectReads) throw new InvalidOperationException("Unexpected server read during local move"); Reads++; }
        public Task<RepositorySession> OpenWorkingCopyAsync(string path, CancellationToken token) => inner.OpenWorkingCopyAsync(path, token);
        public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
        public Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token) { Read(); return inner.RefreshAsync(session, token); }
        public Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
        { Read(); return inner.RefreshDirectoryAsync(session, scope, token); }
        public Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relative, CancellationToken token)
        { Read(); return inner.ReadDirectoryAsync(session, snapshot, relative, token); }
    }
}
