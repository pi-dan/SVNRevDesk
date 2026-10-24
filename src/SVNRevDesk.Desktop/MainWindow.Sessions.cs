using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<RepositoryTab> repositoryTabs = [];
    private IRepositoryService? sessionRepository;
    private RepositoryTab activeTab = null!;
    private bool switchingSession;
    private bool CanChangeSession => !model.IsBusy && !preparingClipboard && fileDrag is not { Started: true };

    private sealed class RepositoryTab : ObservableObject
    {
        public MainViewModel Model { get; }
        public SavedRepositoryTab? Pending { get; set; }
        public string? RequestedLocation { get; set; }
        public bool RequestedIsLocal { get; set; }
        public Task? Initialization { get; set; }
        public HashSet<string> Selection { get; set; } = new(StringComparer.Ordinal);
        public string SelectionDirectory { get; set; } = "";
        public string? Location => Model.Session?.LocalRoot ?? Model.Session?.Url ?? RequestedLocation;
        public bool IsLocal => Model.Session is { } session ? session.LocalRoot != null : RequestedIsLocal;
        public string? Key => Location == null ? null : RepositoryLocation.Key(Location, IsLocal);
        public string Title => Location == null ? "新会话" : (IsLocal ? "本地 · " : "URL · ") + RepositoryLocation.DisplayName(Location, IsLocal);
        public string ToolTip => Location ?? "打开工作副本或仓库 URL";

        public RepositoryTab(MainViewModel model)
        {
            Model = model;
            model.PropertyChanged += ModelChanged;
        }

        private void ModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.HasSession)) RefreshTitle();
        }

        public void RefreshTitle() { Notify(nameof(Title)); Notify(nameof(ToolTip)); }
        public void Dispose() { Model.PropertyChanged -= ModelChanged; Model.Dispose(); }
        public SavedRepositoryTab? Capture() => Model.CaptureSession() ?? Pending ?? (Location == null ? null : new(Location, IsLocal, "", []));
    }

    private void InitializeSessions()
    {
        activeTab = new RepositoryTab(model);
        repositoryTabs.Add(activeTab);
        SessionTabs.ItemsSource = repositoryTabs;
        SessionTabs.SelectedItem = activeTab;
    }

    private void AttachSessionEvents()
    {
        model.Preview.PropertyChanged += SessionPreviewChanged;
        model.EntriesUpdating += CaptureFileSelection;
        model.EntriesUpdated += RestoreFileSelection;
        model.EntryCreated += SelectCreatedEntry;
        model.LocalItemCreationStarted += creationChanges.Begin;
        model.LocalItemCreationFinished += creationChanges.Complete;
        model.PropertyChanged += ActiveSessionChanged;
    }

    private void DetachSessionEvents()
    {
        model.Preview.PropertyChanged -= SessionPreviewChanged;
        model.EntriesUpdating -= CaptureFileSelection;
        model.EntriesUpdated -= RestoreFileSelection;
        model.EntryCreated -= SelectCreatedEntry;
        model.LocalItemCreationStarted -= creationChanges.Begin;
        model.LocalItemCreationFinished -= creationChanges.Complete;
        model.PropertyChanged -= ActiveSessionChanged;
    }

    private void SessionPreviewChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(FilePreviewViewModel.IsEnabled)) UpdatePreviewLayout(); }

    private void ActiveSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasSession)) UpdateWatcher();
        if (e.PropertyName == nameof(MainViewModel.CurrentRelativePathText)) UpdateBreadcrumbs();
    }

    private RepositoryTab CreateTab(string location, bool isLocal, SavedRepositoryTab? saved = null)
    {
        var viewModel = new MainViewModel(model.Settings, sessionRepository);
        viewModel.UseDedicatedSessionCache(location, isLocal);
        var tab = new RepositoryTab(viewModel) { RequestedLocation = location, RequestedIsLocal = isLocal, Pending = saved };
        repositoryTabs.Add(tab);
        return tab;
    }

    private async Task OpenTabAsync(string location, bool isLocal, string? relative = null)
    {
        var key = RepositoryLocation.Key(location, isLocal);
        var tab = repositoryTabs.FirstOrDefault(tab => tab.Key == key);
        if (tab == null && activeTab.Location == null)
        {
            tab = activeTab;
            tab.RequestedLocation = location; tab.RequestedIsLocal = isLocal;
            tab.Model.UseDedicatedSessionCache(location, isLocal);
            tab.RefreshTitle();
        }
        tab ??= CreateTab(location, isLocal);
        var opening = !tab.Model.HasSession;
        await ActivateTabAsync(tab);
        // Background reads retain caches and log failures. An explicit open also needs a visible
        // authentication flow, especially on first launch when no SVN credentials are cached.
        if (opening && tab == activeTab && tab.Model.OpenError is SvnException { IsAuthenticationError: true })
        {
            if (AskForSessionCredentials(tab.Model, "仓库需要身份认证。输入账号密码后将自动重试打开。"))
                await tab.Model.OpenInBackgroundAsync(location, isLocal);
        }
        if (opening && tab == activeTab && tab.Model.OpenError is { } error
            && error is not SvnException { IsAuthenticationError: true })
            MessageBox.Show(this, error.Message, "打开仓库失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (relative != null && tab == activeTab && model.HasSession) await model.BrowseDirectoryAsync(relative);
        PersistWorkspace();
    }

    private async Task ActivateTabAsync(RepositoryTab tab)
    {
        if (!CanChangeSession) return;
        var changed = tab != activeTab;
        if (tab != activeTab)
        {
            activeTab.Selection = (model.IsCardView ? FileCards.SelectedItems : FileGrid.SelectedItems)
                .Cast<BrowserEntry>().Select(entry => entry.RelativePath).ToHashSet(StringComparer.Ordinal);
            activeTab.SelectionDirectory = model.CurrentRelative;
            switchingSession = true; synchronizingSelection = true;
            try
            {
                DetachSessionEvents();
                model.Preview.Cancel();
                localWatcher?.Dispose(); localWatcher = null;
                changedLocalPaths.Clear(); localChangeAt = null; creationChanges.Clear();
                newItemDirectory = null;
                FileGrid.SelectedItems.Clear(); FileCards.SelectedItems.Clear();
                activeTab = tab; model = tab.Model; actions = new ClientActions(this, model);
                DataContext = model;
                AttachSessionEvents();
                displayedDirectory = model.CurrentRelative;
                selectedPaths = tab.SelectionDirectory == model.CurrentRelative ? tab.Selection : [];
                RestoreFileSelection();
                lastAutomaticRefresh = model.LastFullRefreshAt;
                UpdatePreviewLayout(); UpdateBreadcrumbs(); UpdateWatcher();
                SessionTabs.SelectedItem = tab;
            }
            finally { switchingSession = false; synchronizingSelection = false; }
        }
        if (tab.Initialization != null) { await tab.Initialization; return; }
        if (!tab.Model.HasSession && !tab.Model.IsReading && tab.Location != null)
        {
            var pending = tab.Pending;
            tab.Initialization = pending != null ? tab.Model.RestoreTabInBackgroundAsync(pending)
                : tab.Model.OpenInBackgroundAsync(tab.Location, tab.IsLocal);
            try { await tab.Initialization; if (tab.Model.HasSession) tab.Pending = null; }
            finally { tab.Initialization = null; }
        }
        else if (changed && tab.Model.HasSession && !tab.Model.IsReading) await tab.Model.RefreshInBackgroundAsync();
    }

    private async void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (switchingSession || SessionTabs.SelectedItem is not RepositoryTab tab || tab == activeTab) return;
        if (!CanChangeSession) { SessionTabs.SelectedItem = activeTab; return; }
        try { await ActivateTabAsync(tab); PersistWorkspace(); }
        catch (Exception ex) { model.AddLog(ex.Message); }
    }

    private async void CycleSession(int direction)
    {
        if (!CanChangeSession || repositoryTabs.Count < 2) return;
        var index = (repositoryTabs.IndexOf(activeTab) + direction + repositoryTabs.Count) % repositoryTabs.Count;
        await ActivateTabAsync(repositoryTabs[index]);
        TrySaveWorkspaceLayout();
    }

    private void CloseSession_Click(object sender, RoutedEventArgs e) => CloseActiveSession();

    private async void CloseActiveSession()
    {
        if (!CanChangeSession) return;
        var closing = activeTab;
        try { closing.Model.SaveSession(); }
        catch (IOException ex) { MessageBox.Show(this, ex.Message, "保存会话失败"); return; }
        var index = repositoryTabs.IndexOf(closing);
        // Select a replacement before removal so selection events cannot target a disposed model.
        var replacement = repositoryTabs.Count == 1 ? new RepositoryTab(new MainViewModel(model.Settings, sessionRepository))
            : repositoryTabs[index == 0 ? 1 : index - 1];
        if (repositoryTabs.Count == 1) repositoryTabs.Add(replacement);
        var activation = ActivateTabAsync(replacement);
        repositoryTabs.Remove(closing);
        closing.Dispose();
        await activation;
        TrySaveWorkspaceLayout();
    }

    private async Task RestoreWorkspaceAsync()
    {
        var settings = model.Settings;
        var tabs = settings.OpenTabs.ToList();
        var activeKey = settings.ActiveTabKey;
        if (tabs.Count == 0 && !string.IsNullOrWhiteSpace(settings.LastLocation))
            tabs.Add(new(settings.LastLocation, settings.LastLocationIsLocal, settings.LastRelativeDirectory, settings.ExpandedDirectories.ToList()));
        foreach (var saved in tabs)
        {
            try
            {
                var parsed = RepositoryLocation.Parse(saved.Location);
                if (parsed.IsLocal != saved.IsLocal || repositoryTabs.Any(tab => tab.Key == RepositoryLocation.Key(parsed.Location, parsed.IsLocal))) continue;
                CreateTab(parsed.Location, parsed.IsLocal, saved with { Location = parsed.Location, ExpandedDirectories = saved.ExpandedDirectories ?? [] });
            }
            catch (ArgumentException ex) { model.AddLog($"跳过无效的会话：{ex.Message}"); }
        }
        if (repositoryTabs.Count == 1) return;
        var blank = activeTab;
        var selected = repositoryTabs.FirstOrDefault(tab => tab.Key == activeKey) ?? repositoryTabs[1];
        var restoration = ActivateTabAsync(selected);
        repositoryTabs.Remove(blank); blank.Dispose();
        await restoration;
    }

    private void PersistWorkspace()
    {
        var settings = model.Settings;
        settings.OpenTabs = repositoryTabs.Select(tab => tab.Capture()).OfType<SavedRepositoryTab>().ToList();
        settings.ActiveTabKey = activeTab.Key ?? "";
        // An intentionally empty workspace must stay empty on restart.
        if (settings.OpenTabs.Count == 0) { settings.LastLocation = ""; settings.LastRelativeDirectory = ""; settings.ExpandedDirectories = []; }
        settings.Save();
    }

    private void TrySaveWorkspaceLayout()
    {
        try { PersistWorkspace(); }
        catch (IOException ex) { model.AddLog(ex.Message); }
    }

    private void SaveWorkspace()
    {
        foreach (var tab in repositoryTabs.Where(tab => tab != activeTab)) tab.Model.SaveSession();
        model.SaveSession();
        PersistWorkspace();
    }

    private void Favorites_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || e.OriginalSource != menu) return;
        menu.Items.Clear();
        var addCurrent = new MenuItem { Header = "收藏当前位置…", IsEnabled = model.HasSession };
        addCurrent.Click += (_, _) => AddFavorite(true);
        menu.Items.Add(addCurrent);
        var add = new MenuItem { Header = "添加工作副本 / URL 收藏…" };
        add.Click += (_, _) => AddFavorite(false);
        menu.Items.Add(add);
        var links = new MenuItem { Header = "工作副本与 URL 关联…" };
        links.Click += WorkingCopyLinks_Click;
        menu.Items.Add(links);
        menu.Items.Add(new Separator());
        foreach (var favorite in model.Settings.Favorites.ToArray())
        {
            var entry = new MenuItem { Header = favorite.Name, ToolTip = favorite.Location + (favorite.RelativeDirectory.Length == 0 ? "" : "\n目录：" + favorite.RelativeDirectory) };
            var open = new MenuItem { Header = "打开 / 切换到此收藏" };
            open.Click += async (_, _) =>
            {
                if (!CanChangeSession) return;
                try { await OpenTabAsync(favorite.Location, favorite.IsLocal, favorite.RelativeDirectory); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开收藏失败"); }
            };
            entry.Items.Add(open);
            var rename = new MenuItem { Header = "重命名…" };
            rename.Click += (_, _) =>
            {
                var values = InputDialog.Ask(this, "重命名收藏", "为收藏设置容易辨认的名称。", new InputField("名称", favorite.Name));
                if (values == null || string.IsNullOrWhiteSpace(values[0])) return;
                var index = model.Settings.Favorites.IndexOf(favorite);
                if (index >= 0) model.Settings.Favorites[index] = favorite with { Name = values[0].Trim() };
                TrySaveWorkspaceLayout();
            };
            entry.Items.Add(rename);
            var remove = new MenuItem { Header = "移除收藏" };
            remove.Click += (_, _) => { model.Settings.Favorites.Remove(favorite); TrySaveWorkspaceLayout(); };
            entry.Items.Add(remove);
            menu.Items.Add(entry);
        }
        if (model.Settings.Favorites.Count == 0) menu.Items.Add(new MenuItem { Header = "尚无收藏", IsEnabled = false });
    }

    private void AddFavorite(bool current)
    {
        var session = model.CaptureSession();
        var location = current ? session?.Location ?? "" : "";
        var name = current && session != null ? RepositoryLocation.DisplayName(session.Location, session.IsLocal)
            + (session.RelativeDirectory.Length == 0 ? "" : " / " + session.RelativeDirectory) : "";
        var fields = current ? new[] { new InputField("名称", name) }
            : new[] { new InputField("名称"), new InputField("工作副本完整路径或仓库 URL") };
        var values = InputDialog.Ask(this, "添加收藏", current ? "保存当前仓库及所在目录，之后可从收藏菜单直接切换。" : "收藏保存在本机配置中，不包含登录密码。", fields);
        if (values == null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(values[0])) throw new ArgumentException("收藏名称不能为空。");
            var parsed = RepositoryLocation.Parse(current ? location : values[1]);
            var relative = current ? session?.RelativeDirectory ?? "" : "";
            var favorite = new FavoriteLocation(values[0].Trim(), parsed.Location, parsed.IsLocal, relative);
            var index = model.Settings.Favorites.FindIndex(item => RepositoryLocation.Key(item.Location, item.IsLocal) == RepositoryLocation.Key(parsed.Location, parsed.IsLocal)
                && item.RelativeDirectory == relative);
            if (index < 0) model.Settings.Favorites.Add(favorite); else model.Settings.Favorites[index] = favorite;
            PersistWorkspace();
            model.StatusMessage = "已收藏：" + favorite.Name;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { MessageBox.Show(this, ex.Message, "添加收藏失败"); }
    }

    private async void WorkingCopyLinks_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeSession) return;
        var dialog = new WorkingCopyLinksWindow(this, model.Settings.WorkingCopyLinks);
        var open = dialog.ShowDialog() == true;
        model.Settings.WorkingCopyLinks = dialog.Links.ToList();
        TrySaveWorkspaceLayout();
        if (open && dialog.SelectedLink is { } link)
        {
            if (dialog.OpenOnline) await OpenLocationAsync(link.Url);
            else await OpenLocationAsync(link.LocalPath);
        }
    }
}
