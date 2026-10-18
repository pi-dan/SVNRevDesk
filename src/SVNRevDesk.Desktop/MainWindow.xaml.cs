using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop;

public partial class MainWindow : Window
{
    private MainViewModel model;
    private ClientActions actions;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private FileSystemWatcher? localWatcher;
    private DateTimeOffset lastAutomaticRefresh = DateTimeOffset.Now;
    private DateTimeOffset? localChangeAt;
    private readonly HashSet<string> changedLocalPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool synchronizingSelection;
    private string displayedDirectory = "";
    private HashSet<string> selectedPaths = [];
    private bool windowLoaded;

    public MainWindow() : this(UserSettings.Load()) { }

    public MainWindow(UserSettings settings, IRepositoryService? repository = null)
    {
        InitializeComponent();
        model = new MainViewModel(settings, repository);
        backupManager = new BackupManager(settings.StorageDirectory);
        backupCleanupTimer.Tick += (_, _) => StartAutomaticBackupCleanup();
        actions = new ClientActions(this, model);
        if (settings.LoadNotice != null) model.AddLog(settings.LoadNotice);
        DataContext = model;
        sessionRepository = repository;
        AttachSessionEvents();
        InitializeSessions();
        refreshTimer.Tick += RefreshTimer_Tick;
        UpdatePreviewLayout();
        UpdateBreadcrumbs();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { ShowTrayIcon(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ArgumentException)
        {
            model.AddLog($"无法显示系统托盘图标：{ex.Message}");
        }
        // Restoring a hidden window must not reload the repository or reset its selection.
        if (windowLoaded) return;
        windowLoaded = true;
        refreshTimer.Start();
        backupCleanupTimer.Start();
        StartAutomaticBackupCleanup();
        var argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (argument != null) await OpenLocationAsync(argument);
        else await RestoreWorkspaceAsync();
        if (model.LastFullRefreshAt > lastAutomaticRefresh) lastAutomaticRefresh = model.LastFullRefreshAt;
    }

    private async Task HandleAsync(string description, Func<CancellationToken, Task> operation, bool showError = true,
        Func<bool>? confirm = null)
    {
        try
        {
            // Waiting for confirmation is not part of the operation or its elapsed time.
            if (confirm != null && !confirm()) return;
            await model.RunAsync(description, operation);
            if (model.LastFullRefreshAt > lastAutomaticRefresh) lastAutomaticRefresh = model.LastFullRefreshAt;
            UpdateWatcher();
        }
        catch (Exception ex)
        {
            if (showError) MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OpenLocationAsync(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || !CanChangeSession) return;
        try
        {
            var parsed = RepositoryLocation.Parse(location);
            await OpenTabAsync(parsed.Location, parsed.IsLocal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, "打开仓库", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void OpenLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "打开 SVN 工作副本目录" };
        if (dialog.ShowDialog(this) == true) await OpenLocationAsync(dialog.FolderName);
    }

    private async void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        var values = InputDialog.Ask(this, "打开仓库地址", "支持 HTTPS、HTTP、SVN、SVN+SSH 和本地 file URL。认证失败时请使用“本次会话登录”。",
            new InputField("仓库目录 URL", model.Session?.Url ?? "https://"));
        if (values != null) await OpenLocationAsync(values[0]);
    }

    private async void OpenAddress_Click(object sender, RoutedEventArgs e) => await OpenLocationAsync(model.Address);
    private async void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !model.CanOperate) return;
        e.Handled = true;
        await OpenLocationAsync(model.Address);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F && model.CanOperate && model.HasSession)
        { e.Handled = true; Search_Click(this, new RoutedEventArgs()); return; }
        if ((Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) && e.Key == Key.Tab)
        {
            e.Handled = true;
            CycleSession(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        { e.Handled = true; CloseActiveSession(); return; }
        if (e.Key == Key.Escape && fileDrag is { Started: true, Preparing: true } gesture)
        {
            gesture.Cancelled = true;
            gesture.PreparationCancellation?.Cancel();
            e.Handled = true;
            return;
        }
        if (IsFileClipboardKey(e.Key, Keyboard.Modifiers, e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            await HandleClipboardKeyAsync(e.Key, e.OriginalSource as DependencyObject);
            return;
        }
        if (e.Key == Key.F5 && model.CanRefresh)
        {
            e.Handled = true;
            await RefreshInBackgroundAsync();
        }
        else if (e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt && model.CanGoParent)
        {
            e.Handled = true;
            await model.BrowseDirectoryAsync(model.CurrentRelative.Contains('/')
                ? model.CurrentRelative[..model.CurrentRelative.LastIndexOf('/')] : "");
        }
        else if ((FileGrid.IsKeyboardFocusWithin || FileCards.IsKeyboardFocusWithin) && model.CanOperate)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && model.CanExecute("open"))
            {
                e.Handled = true;
                Operation_Click(new Button { Tag = "open" }, new RoutedEventArgs());
            }
            else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && model.CanCompareSelection)
            {
                e.Handled = true;
                Operation_Click(new Button { Tag = "compare-server" }, new RoutedEventArgs());
            }
        }
    }

    private void BrowserContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.PlacementTarget == FileGrid || menu.PlacementTarget == FileCards)
        {
            var commitItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag is "commit" or "commit-directory");
            var hasSelection = (model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems).Count > 0;
            // A blank-area right-click clears selection; its commit target is the displayed directory.
            commitItem.Tag = hasSelection ? "commit" : "commit-directory";
            commitItem.Header = hasSelection ? "提交所选…" : "提交当前目录…";
        }
        if (menu.PlacementTarget == DirectoryTree) UpdateDirectoryMenu(menu);
        else UpdateMenuAvailability(menu.Items);
        UpdateClipboardMenu(menu);
        PopulateNewItemMenu(menu);
    }

    private void MainMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menu) UpdateMenuAvailability(menu.Items);
    }

    private void UpdateMenuAvailability(ItemCollection items)
    {
        UpdateIgnoreMenu(items, (model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems).Cast<BrowserEntry>().ToList());
        foreach (var item in items.OfType<MenuItem>())
            if (item.Tag is string operation && operation is not ("new-item" or "file-copy" or "file-cut" or "file-paste")) item.IsEnabled = model.CanExecute(operation);
    }

    private static void UpdateIgnoreMenu(ItemCollection items, IReadOnlyList<BrowserEntry> selection)
    {
        foreach (var item in items.OfType<MenuItem>().Where(item => item.Tag is "ignore" or "unignore"))
        {
            var ignored = selection.Count == 1 && selection[0].State == FileState.Ignored;
            item.Tag = ignored ? "unignore" : "ignore";
            item.Header = ignored ? "取消本机忽略" : "忽略（仅本机）";
            item.ToolTip = ignored ? selection[0].IsLocallyIgnored
                ? "仅移除所选路径的本机规则；若继承自上级目录，请在上级目录取消。不修改 SVN 属性。"
                : "此项目由 SVN 共享属性或全局规则忽略，本机取消操作无法覆盖这些规则。"
                : "按完整路径仅在当前电脑的本软件中忽略；保留文件，不修改 SVN 属性，不同步给其他用户。";
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshInBackgroundAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => model.Cancel();

    private async void Operation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string operation || model.IsBusy) return;
        var selected = (model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems).Cast<BrowserEntry>().ToList();
        await ExecuteOperationAsync(operation, selected);
    }

    private async Task ExecuteOperationAsync(string operation, IReadOnlyList<BrowserEntry> selected, string? exportRevision = null)
    {
        if (!model.CanOperate || !BrowserActionAvailability.CanExecute(operation, model.Session, selected)) return;
        if (operation == "open" && selected.Count == 1 && selected[0].IsDirectory)
        {
            await model.BrowseDirectoryAsync(selected[0].RelativePath);
            return;
        }
        if (operation == "show-in-explorer")
        {
            try { await actions.ExecuteAsync(operation, selected, CancellationToken.None); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
            return;
        }
        if (operation == "open" && selected.Count == 1 && TryOpenLocalFile(selected[0])) return;
        var description = operation switch
        {
            "update" => "更新所选", "update-directory" => "更新当前目录", "checkout" => "检出到本地",
            "update-revision" or "update-directory-revision" => "更新到指定修订号",
            "reverse-merge" or "reverse-merge-directory" => "回滚指定提交",
            "delete" => "删除所选",
            "ignore" => "忽略所选", "unignore" => "取消忽略",
            "move" or "move-to" => "移动文件",
            "batch-rename" => "批量重命名",
            "log" => "查看提交历史",
            "commit" or "commit-directory" => "提交修改", "show-in-explorer" => "在资源管理器中显示",
            "compare-server" or "compare-base" or "compare-history" => "文件比较", "export" => "导出所选", _ => "SVN 操作"
        };
        await HandleAsync(description, token => actions.ExecuteAsync(operation, selected, token, exportRevision),
            confirm: operation == "delete" ? () => actions.ConfirmDelete(selected) : null);
    }

    private async void DirectoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (switchingSession || model.IsBusy || model.IsUpdatingTree || selectingDirectoryForMenu || e.NewValue is not DirectoryNode { IsPlaceholder: false } node
            || node.RelativePath == model.CurrentRelative) return;
        await model.BrowseDirectoryAsync(node.RelativePath);
    }

    private async void DirectoryTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (switchingSession || model.IsBusy || model.IsUpdatingTree || e.OriginalSource is not TreeViewItem { DataContext: DirectoryNode node } || node.Loaded) return;
        await model.ExpandDirectoryAsync(node);
    }

    private async void Parent_Click(object sender, RoutedEventArgs e)
    {
        if (model.CurrentRelative.Length == 0) return;
        var separator = model.CurrentRelative.LastIndexOf('/');
        var parent = separator < 0 ? "" : model.CurrentRelative[..separator];
        await model.BrowseDirectoryAsync(parent);
    }

    private async void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (model.IsBusy || row?.Item is not BrowserEntry entry) return;
        if (entry.IsDirectory) { await model.BrowseDirectoryAsync(entry.RelativePath); return; }
        if (!TryOpenLocalFile(entry))
            await HandleAsync("打开项目", token => actions.ExecuteAsync("open", [entry], token));
    }

    private void FileGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        newItemDirectory = row?.Item is BrowserEntry { IsDirectory: true } folder ? folder.RelativePath : model.CurrentRelative;
        if (row == null) FileGrid.SelectedItems.Clear();
        if (row != null && !row.IsSelected) { FileGrid.SelectedItems.Clear(); row.IsSelected = true; }
    }

    private async void FileCards_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (model.IsBusy || item?.DataContext is not BrowserEntry entry) return;
        if (entry.IsDirectory) { await model.BrowseDirectoryAsync(entry.RelativePath); return; }
        if (!TryOpenLocalFile(entry))
            await HandleAsync("打开项目", token => actions.ExecuteAsync("open", [entry], token));
    }

    private bool TryOpenLocalFile(BrowserEntry entry)
    {
        try { return actions.TryOpenLocalFile(entry); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开文件失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
    }

    private void FileCards_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        newItemDirectory = item?.DataContext is BrowserEntry { IsDirectory: true } folder ? folder.RelativePath : model.CurrentRelative;
        if (item == null) FileCards.SelectedItems.Clear();
        if (item != null && !item.IsSelected) { FileCards.SelectedItems.Clear(); item.IsSelected = true; }
    }

    private void FileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (synchronizingSelection) return;
        synchronizingSelection = true;
        try
        {
            // Both views refer to the same entries; changing the view must not change the operation target.
            var source = sender == FileGrid ? FileGrid.SelectedItems : FileCards.SelectedItems;
            var destination = sender == FileGrid ? FileCards.SelectedItems : FileGrid.SelectedItems;
            destination.Clear();
            foreach (var item in source) destination.Add(item);
            model.SetSelection(source.Cast<BrowserEntry>().ToList());
        }
        finally { synchronizingSelection = false; }
    }

    private void CaptureFileSelection()
    {
        synchronizingSelection = true;
        selectedPaths = displayedDirectory == model.CurrentRelative
            ? (model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems).Cast<BrowserEntry>()
                .Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    private void RestoreFileSelection()
    {
        try
        {
            foreach (var entry in model.Entries.Where(entry => selectedPaths.Contains(entry.RelativePath)))
            {
                FileGrid.SelectedItems.Add(entry);
                FileCards.SelectedItems.Add(entry);
            }
            displayedDirectory = model.CurrentRelative;
            model.SetSelection((model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems).Cast<BrowserEntry>().ToList());
        }
        finally { synchronizingSelection = false; }
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T result) return result;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var values = InputDialog.Ask(this, "本次会话登录", "账号密码仅保留在本次进程内；密码不写入配置、日志或命令行。留空密码可使用 SVN 已有认证缓存。服务器证书需有效或已由 SVN 信任。",
            new InputField("用户名", model.Runner.Username ?? ""), new InputField("密码", Password: true));
        if (values == null) return;
        if (values[1].Contains('\r') || values[1].Contains('\n'))
        {
            MessageBox.Show(this, "密码不能包含换行符。", "登录"); return;
        }
        model.Runner.Username = values[0];
        model.Runner.Password = values[1].Length == 0 ? null : values[1];
        model.StatusMessage = "本次会话凭据已设置，请重新打开仓库或刷新。";
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(this, model.Settings, backupManager, model.AddLog);
        if (dialog.ShowDialog() != true) return;
        var cleanupChanged = model.Settings.AutoBackupCleanup != dialog.AutoBackupCleanup
            || model.Settings.BackupRetentionDays != dialog.BackupRetentionDays;
        model.Settings.SvnExecutable = dialog.SvnExecutable;
        model.Settings.ExternalDiffExecutable = dialog.ExternalDiffExecutable;
        model.Settings.RefreshSeconds = dialog.RefreshSeconds;
        model.Settings.AutoBackupCleanup = dialog.AutoBackupCleanup;
        model.Settings.BackupRetentionDays = dialog.BackupRetentionDays;
        model.Settings.CloseBehavior = dialog.CloseBehavior;
        foreach (var tab in repositoryTabs)
        {
            tab.Model.NotifySettingsChanged();
            tab.Model.Runner.Executable = model.Settings.SvnExecutable;
        }
        try { SaveWorkspace(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存设置失败"); return; }
        if (cleanupChanged)
        {
            automaticBackupCancellation?.Cancel();
            await automaticBackupCleanup;
            if (backupLifetime.IsCancellationRequested) return;
            backupCleanupTimer.Stop();
            backupCleanupTimer.Start();
            StartAutomaticBackupCleanup();
        }
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsActive || model.IsBusy || model.IsReading || model.Session == null) return;
        if (model.LastFullRefreshAt > lastAutomaticRefresh) lastAutomaticRefresh = model.LastFullRefreshAt;
        var now = DateTimeOffset.Now;
        var localChanged = localChangeAt is { } changed && (now - changed).TotalSeconds >= 1;
        var serverDue = model.AutoRefresh && (now - lastAutomaticRefresh).TotalSeconds >= model.Settings.RefreshSeconds;
        if (!localChanged && !serverDue) return;
        var paths = changedLocalPaths.ToList();
        localChangeAt = null;
        changedLocalPaths.Clear();
        if (serverDue)
        {
            lastAutomaticRefresh = now;
            await model.RefreshAllInBackgroundAsync();
        }
        else if (paths.Count > 0) await model.RefreshPathsInBackgroundAsync(paths);
        else await RefreshInBackgroundAsync();
    }

    private async Task RefreshInBackgroundAsync()
    {
        await model.RefreshInBackgroundAsync();
    }

    private void UpdateWatcher()
    {
        var path = model.Session?.LocalRoot;
        if (localWatcher?.Path == path) return;
        localWatcher?.Dispose(); localWatcher = null;
        changedLocalPaths.Clear(); localChangeAt = null;
        creationChanges.Clear();
        if (path == null || !Directory.Exists(path)) return;
        localWatcher = new FileSystemWatcher(path) { IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        localWatcher.Changed += LocalFile_Changed; localWatcher.Created += LocalFile_Changed;
        localWatcher.Deleted += LocalFile_Changed; localWatcher.Renamed += LocalFile_Changed;
        localWatcher.Error += (watcher, _) => Dispatcher.BeginInvoke(() => { if (watcher == localWatcher) localChangeAt = DateTimeOffset.Now; });
        localWatcher.EnableRaisingEvents = true;
    }

    private void LocalFile_Changed(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".svn", StringComparison.OrdinalIgnoreCase))) return;
        if (creationChanges.ShouldIgnore(e.FullPath)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (sender != localWatcher) return;
            if (model.Session?.LocalRoot is not { } root
                || !RepositoryService.IsDescendant(e.FullPath, root)) return;
            changedLocalPaths.Add(e.FullPath);
            if (e is RenamedEventArgs renamed && RepositoryService.IsDescendant(renamed.OldFullPath, root))
                changedLocalPaths.Add(renamed.OldFullPath);
            localChangeAt = DateTimeOffset.Now;
        });
    }

}
