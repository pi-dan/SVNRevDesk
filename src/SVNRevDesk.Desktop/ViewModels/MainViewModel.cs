using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using SVNRevDesk.Core.Formatting;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private bool busy;
    private bool differencesOnly;
    private string filterText = "";
    private string selectedState = "全部状态";
    private string statusMessage = "打开本地 SVN 工作副本，或输入仓库地址开始浏览。";
    private string address = "";
    private string currentRelative = "";
    private CancellationTokenSource? operationCancellation;
    private readonly IRepositoryService repository;
    private readonly List<BrowserEntry> currentEntries = [];
    private IReadOnlyList<BrowserEntry> selectedEntries = [];
    private bool updatingEntries;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch operationTime = new();
    private string progressTitle = "";
    private string progressTarget = "";
    private string progressMessage = "";
    private long? transferredBytes;
    public const string DetailsViewName = "详细列表";
    public const string CardViewName = "卡片视图";

    public SvnRunner Runner { get; }
    public SvnOperations Operations { get; }
    public LocalIgnoreStore LocalIgnores { get; }
    public FilePreviewViewModel Preview { get; }
    public UserSettings Settings { get; }
    public RepositorySession? Session { get; private set; }
    public RepositorySnapshot? Snapshot { get; private set; }
    public ObservableCollection<DirectoryNode> Roots { get; } = [];
    public ObservableCollection<BrowserEntry> Entries { get; } = [];
    public ObservableCollection<string> ActivityLog { get; } = [];
    public ICollectionView EntriesView { get; }
    public event Action? EntriesUpdating;
    public event Action? EntriesUpdated;
    public IEnumerable<string> StateFilters => new[] { "全部状态" }.Concat(Enum.GetValues<FileState>().Select(s => s.DisplayName()));
    public IEnumerable<string> RecentLocations => Settings.RecentLocations;
    public IEnumerable<string> ViewModes => [DetailsViewName, CardViewName];
    public string ViewMode
    {
        get => Settings.UseCardView ? CardViewName : DetailsViewName;
        set
        {
            if (value is not (DetailsViewName or CardViewName)) return;
            var useCards = value == CardViewName;
            if (Settings.UseCardView == useCards) return;
            Settings.UseCardView = useCards;
            Notify();
            Notify(nameof(IsCardView));
            Notify(nameof(IsDetailsView));
        }
    }
    public bool IsCardView => Settings.UseCardView;
    public bool IsDetailsView => !IsCardView;
    public bool IsBusy
    {
        get => busy;
        private set
        {
            if (!SetProperty(ref busy, value)) return;
            Preview.IsPaused = value;
            Notify(nameof(IsOperationProgressVisible));
            Notify(nameof(CanOperate));
            Notify(nameof(CanCancel));
            NotifyActionAvailability();
        }
    }
    public bool CanOperate => !IsBusy;
    public bool CanCancel => IsBusy || IsReading || Preview.IsLoading;
    public bool HasSession => Session != null;
    public bool IsOnlineOnly => Session is { LocalRoot: null };
    public bool CanRefresh => CanOperate && HasSession;
    public bool CanUpdateSelection => CanExecute("update");
    public bool CanUpdateCurrentFiles => CanExecute("update-directory-files");
    public bool CanCommitSelection => CanExecute("commit");
    public bool CanCompareSelection => CanExecute("compare-server");
    public bool CanGoParent => CanOperate && CurrentRelative.Length > 0;
    public string SelectionText => selectedEntries.Count == 0 ? "未选择项目" : $"已选择 {selectedEntries.Count} 项";
    public string ProgressTitle { get => progressTitle; private set => SetProperty(ref progressTitle, value); }
    public string ProgressTarget { get => progressTarget; private set => SetProperty(ref progressTarget, value); }
    public string ProgressMessage { get => progressMessage; private set => SetProperty(ref progressMessage, value); }
    public string ProgressElapsedText => $"已用时 {(int)operationTime.Elapsed.TotalHours:00}:{operationTime.Elapsed.Minutes:00}:{operationTime.Elapsed.Seconds:00}";
    public string ProgressTransferText => transferredBytes is { } bytes ? $"已下载 {FileSizeFormatter.Format(bytes)}" : "";
    public string Address { get => address; set => SetProperty(ref address, value); }
    public string StatusMessage { get => statusMessage; set => SetProperty(ref statusMessage, value); }
    public string CurrentRelative => currentRelative;
    public string? CurrentLocalPath => Session == null ? null : RepositoryService.GetLocalPath(Session, currentRelative);
    public bool CanOpenCurrentDirectory => CurrentLocalPath is { } path && Directory.Exists(path);
    public string CurrentRelativePathText => Session == null ? "尚未打开仓库"
        : currentRelative.Length == 0 ? "根目录" : $"根目录 / {currentRelative.Replace("/", " / ")}";
    public string CurrentPath
    {
        get
        {
            if (Session == null) return "尚未打开仓库";
            var local = RepositoryService.GetLocalPath(Session, currentRelative);
            if (local != null && Snapshot != null && Snapshot.LocalInfo.TryGetValue(local, out var info)) return info.Url;
            return RepositoryService.AppendUrl(Session.Url, currentRelative);
        }
    }
    public string SnapshotText => CurrentDirectorySnapshot is not { } snapshot ? "未检查服务器"
        : $"服务器 r{snapshot.Revision} · 最后检查 {snapshot.CheckedAt:HH:mm:ss}";
    public int VisibleEntryCount => EntriesView.Cast<BrowserEntry>().Count();
    public string EntryCount => $"显示 {VisibleEntryCount} / {Entries.Count} 项 · {Entries.Count(e => e.HasDifferences)} 项存在差异";
    public string DirectoryOverview => $"{Entries.Count(e => e.IsDirectory)} 个目录 · {Entries.Count(e => !e.IsDirectory)} 个文件";
    public string DifferenceOverview => IsOnlineOnly ? "尚未关联本地" : $"{Entries.Count(e => e.HasDifferences)} 项存在差异";
    public string AutoRefreshDescription => !AutoRefresh ? "定时检查已关闭 · 可手动刷新"
        : $"前台每 {RefreshIntervalText}检查 · 刷新不会下载文件";
    private string RefreshIntervalText => Settings.RefreshSeconds >= 3600 && Settings.RefreshSeconds % 3600 == 0
        ? $"{Settings.RefreshSeconds / 3600} 小时"
        : $"{Settings.RefreshSeconds / 60m:0.##} 分钟";
    public string EmptyStateTitle => !HasSession ? IsReading ? "正在载入仓库…"
        : OpenError is SvnException { IsAuthenticationError: true } ? "仓库需要登录"
        : OpenError != null ? "仓库打开失败" : "打开仓库，开始浏览" : IsReading ? "正在读取目录…"
        : IsOnlineOnly && DifferencesOnly ? "关联本地后查看差异"
        : Entries.Count == 0 ? "当前目录没有项目" : "没有符合筛选条件的项目";
    public string EmptyStateDescription => !HasSession ? IsReading ? "正在后台读取服务器信息，可取消读取或打开其他仓库。"
        : OpenError is SvnException { IsAuthenticationError: true } ? "请输入本次会话的账号密码后重新打开地址。可从“文件 → SVN 登录”设置。"
        : OpenError != null ? OpenError.Message
        : "选择本地 SVN 工作副本，或输入服务器地址。"
        : IsReading ? "读取在后台进行，可以继续切换目录。"
        : IsOnlineOnly && DifferencesOnly ? "在线浏览尚未检查本地状态。请关联工作副本或关闭“只显示差异”。"
        : Entries.Count == 0 ? "可点击刷新重新检查；读取失败时请查看底部操作记录。" : "调整名称或状态筛选，或关闭“只显示差异”。";
    public bool DifferencesOnly { get => differencesOnly; set { if (SetProperty(ref differencesOnly, value)) EntriesView.Refresh(); } }
    public string FilterText { get => filterText; set { if (SetProperty(ref filterText, value)) EntriesView.Refresh(); } }
    public string SelectedState { get => selectedState; set { if (SetProperty(ref selectedState, value)) EntriesView.Refresh(); } }
    public bool AutoRefresh { get => Settings.AutoRefresh; set { Settings.AutoRefresh = value; Notify(); Notify(nameof(AutoRefreshDescription)); } }

    public MainViewModel(UserSettings settings, IRepositoryService? repositoryService = null)
    {
        Settings = settings;
        Runner = new SvnRunner
        {
            Executable = settings.SvnExecutable,
            ConfigurationDirectory = settings.SvnConfigurationDirectory,
            ConfigurationTemplateDirectory = settings.LegacySvnConfigurationDirectory
        };
        LocalIgnores = new LocalIgnoreStore(settings.LocalIgnoreConfigurationPath);
        Operations = new SvnOperations(Runner, settings.TemporaryDirectory, settings.BackupDirectory, LocalIgnores);
        Preview = new FilePreviewViewModel((entry, revision, token) => FilePreviewService.LoadAsync(entry, revision, Runner, Settings, token))
        { IsEnabled = settings.PreviewEnabled };
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FilePreviewViewModel.IsEnabled)) Settings.PreviewEnabled = Preview.IsEnabled;
            else if (e.PropertyName == nameof(FilePreviewViewModel.IsLoading)) Notify(nameof(CanCancel));
        };
        repository = new LocalIgnoreRepositoryService(repositoryService ?? new RepositoryService(Runner), LocalIgnores);
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = MatchesFilter;
        EntriesView.CollectionChanged += (_, _) => { if (!updatingEntries) NotifyEntryOverview(); };
        Runner.CommandCompleted += AddLog;
        Runner.ProgressChanged += OnProgressChanged;
        progressTimer.Tick += (_, _) => Notify(nameof(ProgressElapsedText));
    }

    public bool CanExecute(string operation) => CanOperate && BrowserActionAvailability.CanExecute(operation, Session, selectedEntries);

    public void SetSelection(IReadOnlyList<BrowserEntry> selection)
    {
        selectedEntries = selection;
        NotifyActionAvailability();
        Notify(nameof(SelectionText));
        UpdatePreview();
    }

    private void UpdatePreview() => Preview.Update(selectedEntries, CurrentDirectorySnapshot?.Revision.ToString() ?? "");

    public void NotifySettingsChanged() => Notify(nameof(AutoRefreshDescription));

    private void NotifyActionAvailability()
    {
        Notify(nameof(CanRefresh)); Notify(nameof(CanUpdateSelection));
        Notify(nameof(CanUpdateCurrentFiles));
        Notify(nameof(CanCommitSelection)); Notify(nameof(CanCompareSelection)); Notify(nameof(CanGoParent));
    }

    private void NotifyEntryOverview()
    {
        Notify(nameof(VisibleEntryCount)); Notify(nameof(EntryCount)); Notify(nameof(DirectoryOverview));
        Notify(nameof(DifferenceOverview)); Notify(nameof(EmptyStateTitle)); Notify(nameof(EmptyStateDescription));
    }

    private bool MatchesFilter(object value)
    {
        if (value is not BrowserEntry entry) return false;
        if (DifferencesOnly && !entry.HasDifferences) return false;
        if (SelectedState != "全部状态" && entry.StateText != SelectedState
            && (!entry.IsDirectory || !entry.Summary.Contains(SelectedState, StringComparison.Ordinal))) return false;
        return string.IsNullOrWhiteSpace(FilterText) || entry.Name.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase);
    }

    public async Task RunAsync(string description, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        operationDeclined = false;
        ProgressTitle = description;
        ProgressTarget = "";
        ProgressMessage = "正在准备…";
        transferredBytes = null;
        Notify(nameof(ProgressTransferText));
        operationTime.Restart();
        Notify(nameof(ProgressElapsedText));
        progressTimer.Start();
        IsBusy = true;
        StatusMessage = description;
        using var cancellation = new CancellationTokenSource();
        // A large transfer can legitimately take more than 30 minutes; the user can cancel it.
        operationCancellation = cancellation;
        var interruptedIndexSession = queuedIndexRead is { Cancellation.IsCancellationRequested: false } ? Session : null;
        try
        {
            // Reads yield to an explicit operation. Wait for their processes to finish before a write.
            await StopBackgroundReadsAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            await action(cancellation.Token);
            StatusMessage = operationDeclined ? $"已取消{description}。" : $"{description}完成";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "操作已取消。已执行的 SVN 操作不会自动回滚，请刷新；工作副本被锁住时可执行清理。";
            AddLog(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败，显示上次结果：{ex.Message.Split('\n')[0]}";
            AddLog(ex.Message);
            throw;
        }
        finally
        {
            operationCancellation = null;
            progressTimer.Stop();
            operationTime.Stop();
            Notify(nameof(ProgressElapsedText));
            IsBusy = false;
            // StopBackgroundReadsAsync cancelled queued index maintenance above; requeue it unless
            // the operation itself refreshed directories, which queues its own update on completion.
            if (interruptedIndexSession != null && Session == interruptedIndexSession && !disposed)
                RequeueInterruptedIndexMaintenance();
        }
    }

    public void Cancel()
    {
        operationCancellation?.Cancel();
        CancelBackgroundReads();
        Preview.Cancel();
    }

    public void SetProgressTarget(string target)
    {
        ProgressTarget = target.Contains("://", StringComparison.Ordinal) ? Uri.UnescapeDataString(target) : target;
    }

    private void OnProgressChanged(SvnProgress progress)
    {
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnProgressChanged(progress));
            return;
        }
        if (!IsBusy) return;
        var command = progress.Command switch
        {
            "update" => "更新工作文件", "checkout" => "检出工作副本", "cat" => "下载文件内容",
            "commit" => "提交修改", "status" => "检查本地与服务器状态", "info" => "读取版本信息",
            "list" => "读取服务器目录", "switch" => "切换分支", "merge" => "合并修改",
            "export" => "导出文件", _ => $"执行 SVN {progress.Command}"
        };
        switch (progress.Stage)
        {
            case SvnProgressStage.Started:
                transferredBytes = null;
                ProgressMessage = command + "…";
                break;
            case SvnProgressStage.Output:
                ProgressMessage = progress.Message;
                break;
            case SvnProgressStage.Transfer:
                transferredBytes = progress.BytesTransferred;
                ProgressMessage = command + "…";
                break;
            case SvnProgressStage.Completed:
                ProgressMessage = command + "完成";
                break;
        }
        Notify(nameof(ProgressTransferText));
    }

    public async Task RestoreLastSessionAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(Settings.LastLocation)) return;

        // Opening resets the current directory, so copy the saved navigation state first.
        var location = Settings.LastLocation;
        var isLocal = Settings.LastLocationIsLocal;
        var relative = Settings.LastRelativeDirectory;
        var expanded = Settings.ExpandedDirectories.ToList();
        Address = location;
        await OpenAsync(location, isLocal, token);

        await RestoreNavigationAsync(relative, expanded, token);
    }

    public void SaveSession()
    {
        RememberSession();
        Settings.Save();
        SaveBrowsingCache();
    }

    private void RememberSession()
    {
        if (Session == null) return;
        Settings.LastLocation = Session.LocalRoot ?? Session.Url;
        Settings.LastLocationIsLocal = Session.LocalRoot != null;
        Settings.LastRelativeDirectory = currentRelative;
        Settings.ExpandedDirectories = AllNodes().Where(node => !node.IsPlaceholder && node.IsExpanded)
            .Select(node => node.RelativePath).ToList();
    }

    public async Task OpenAsync(string location, bool isLocal, CancellationToken token)
    {
        var session = await FetchSessionAsync(location, isLocal, token);
        var snapshot = await FetchSnapshotAsync(session, token);
        var entries = await FetchDirectoryAsync(session, snapshot, "", token);
        token.ThrowIfCancellationRequested();
        if (disposed) return;
        // Publish a complete initial view only after all queries succeed.
        ResetSearchIndex();
        Session = session;
        if (sessionCacheDirectory != null) UseDedicatedSessionCache(session.LocalRoot ?? session.Url, session.LocalRoot != null);
        Snapshot = snapshot;
        LastFullRefreshAt = snapshot.CheckedAt;
        cacheGeneration++;
        directoryCache.Clear();
        directoryCache[""] = new(snapshot, entries);
        navigationVersion++;
        Address = isLocal ? session.LocalRoot! : session.Url;
        currentRelative = "";
        Roots.Clear();
        var root = CreateRootNode(session);
        Roots.Add(root);
        SetEntries(entries);
        UpdateChildren(root, entries);
        root.IsExpanded = true;
        root.IsSelected = true;
        RememberSession();
        if (session.LocalRoot != null) Settings.RememberWorkingCopy(session.LocalRoot, session.Url, session.RepositoryId);
        Settings.Remember(Address);
        NotifyLocation();
    }

    public async Task AssociateWorkingCopyAsync(string location, CancellationToken token)
    {
        if (Session == null) throw new InvalidOperationException("请先打开仓库地址。");
        var local = await repository.OpenWorkingCopyAsync(location, token);
        if (local.RepositoryId != Session.RepositoryId || !new Uri(local.Url).Equals(new Uri(Session.Url)))
            throw new InvalidOperationException($"所选工作副本应对应浏览根地址：\n{Session.Url}\n\n当前所选：{local.Url}");
        var relative = CurrentRelative;
        await OpenAsync(location, true, token);
        if (relative.Length > 0) await NavigateAsync(relative, token);
    }

    public async Task RefreshAllAsync(CancellationToken token)
    {
        if (Session == null) return;
        var session = Session;
        var snapshot = await FetchSnapshotAsync(session, token);
        token.ThrowIfCancellationRequested();
        var paths = directoryCache.Keys.Concat(AllNodes().Where(node => node.IsExpanded && !node.IsPlaceholder)
            .Select(node => node.RelativePath)).Append("").Append(currentRelative).Distinct(StringComparer.Ordinal)
            .OrderBy(path => path.Length == 0 ? -1 : path.Count(c => c == '/')).ToList();
        var refreshed = new Dictionary<string, CachedDirectory>(StringComparer.Ordinal);
        var queuedPaths = paths.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = paths[index];
            try
            {
                var entries = await FetchDirectoryAsync(session, snapshot, path, token);
                refreshed[path] = new(snapshot, entries);
            }
            catch (SvnException ex) when (path.Length > 0 && ex.ErrorOutput.Contains("E160013", StringComparison.Ordinal))
            {
                AddLog($"目录已从服务器移除：{path}");
            }
            if (index == paths.Count - 1)
            {
                // The user may expand another branch while the refresh is awaiting the network.
                var addedPaths = directoryCache.Keys.Concat(AllNodes().Where(node => node.IsExpanded && !node.IsPlaceholder)
                    .Select(node => node.RelativePath)).Append(currentRelative);
                foreach (var added in addedPaths)
                    if (queuedPaths.Add(added)) paths.Add(added);
            }
        }
        token.ThrowIfCancellationRequested();
        if (session != Session || disposed) return;

        // Publish the complete snapshot and its directory lists together. Failed reads keep the old cache.
        Snapshot = snapshot;
        LastFullRefreshAt = snapshot.CheckedAt;
        cacheGeneration++;
        directoryCache.Clear();
        foreach (var (path, cached) in refreshed) directoryCache[path] = cached;
        while (!directoryCache.ContainsKey(currentRelative))
            currentRelative = ParentDirectory(currentRelative);
        foreach (var (path, cached) in refreshed)
        {
            var node = FindNode(path);
            if (node != null) UpdateChildren(node, cached.Entries);
        }
        foreach (var node in AllNodes().Where(node => !node.IsPlaceholder))
        {
            node.Loaded = directoryCache.ContainsKey(node.RelativePath);
            node.Summary = GetDirectorySummary(node, snapshot);
        }
        ShowDirectory(currentRelative, FindNode(currentRelative), directoryCache[currentRelative].Entries);
        NotifyLocation();
        RememberSession();
        QueueSearchIndexUpdate(session);
    }

    public async Task NavigateAsync(string relative, CancellationToken token)
    {
        if (Session == null || Snapshot == null) return;
        token.ThrowIfCancellationRequested();
        if (!IsValidRelativeDirectory(relative)) throw new ArgumentException("目录路径必须位于当前仓库内部。");
        var version = ++navigationVersion;
        var session = Session;
        var snapshot = Snapshot;
        var existing = FindNode(relative);
        directoryCache.TryGetValue(relative, out var cached);
        // Change the path and clear unrelated rows immediately, before any network await.
        ShowDirectory(relative, existing, cached?.Entries);
        if (cached?.HasServerListing != true && session.LocalRoot != null)
        {
            var generation = cacheGeneration;
            var localEntries = await Task.Run(() => RepositoryService.ReadLocalDirectory(session, snapshot,
                relative, cached?.Entries ?? [], hasServerListing: false), token);
            token.ThrowIfCancellationRequested();
            if (version != navigationVersion || session != Session || disposed) return;
            // A concurrent local operation or refresh owns any newer cache contents.
            if (generation == cacheGeneration
                && (!directoryCache.TryGetValue(relative, out var latest) || !latest.HasServerListing))
            {
                localEntries = LocalIgnores.Apply(snapshot, localEntries).ToList();
                directoryCache[relative] = new(snapshot, localEntries) { HasServerListing = false };
                ShowDirectory(relative, existing, localEntries);
            }
        }
        var node = await FindNearestDirectoryAsync(relative, token);
        if (node.RelativePath != relative) throw new InvalidOperationException("所选目录不存在，请刷新后重试。");
        var entries = await ReadCachedDirectoryAsync(session, snapshot, relative, token);
        token.ThrowIfCancellationRequested();
        if (version != navigationVersion || session != Session || disposed) return;
        ShowDirectory(relative, node, entries);
        node.IsExpanded = true;
        RememberSession();
    }

    private async Task<DirectoryNode> FindNearestDirectoryAsync(string relative, CancellationToken token)
    {
        var node = Roots.Single();
        foreach (var name in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var child = node.Children.FirstOrDefault(child => !child.IsPlaceholder && child.Name == name);
            // A local move may already have built this branch without a complete server listing.
            // Known children need no ancestor query; the destination still validates its own cache.
            if (child == null)
            {
                await LoadChildrenAsync(node, token);
                child = node.Children.FirstOrDefault(child => !child.IsPlaceholder && child.Name == name);
            }
            var wasUpdating = IsUpdatingTree;
            IsUpdatingTree = true;
            try { node.IsExpanded = true; }
            finally { IsUpdatingTree = wasUpdating; }
            if (child == null) break;
            node = child;
        }
        return node;
    }

    private static bool IsValidRelativeDirectory(string path) => !Path.IsPathRooted(path)
        && !path.Contains('\\') && path.Split('/').All(part => part is not ("." or ".."));

    public async Task LoadChildrenAsync(DirectoryNode node, CancellationToken token)
    {
        if (Session == null || Snapshot == null || node.IsPlaceholder || node.Loaded) return;
        var session = Session;
        while (session == Session && !disposed)
        {
            var snapshot = Snapshot;
            var entries = await ReadCachedDirectoryAsync(session, snapshot, node.RelativePath, token);
            token.ThrowIfCancellationRequested();
            if (session != Session || FindNode(node.RelativePath) != node) return;
            if (snapshot != Snapshot) continue;
            UpdateChildren(node, entries);
            return;
        }
    }

    private void UpdateChildren(DirectoryNode parent, IReadOnlyList<BrowserEntry> entries, bool updateSummary = true)
    {
        var wasUpdating = IsUpdatingTree;
        IsUpdatingTree = true;
        try
        {
            var old = parent.Children.Where(n => !n.IsPlaceholder).ToDictionary(n => n.RelativePath);
            parent.Children.Clear();
            foreach (var entry in entries.Where(e => e.IsDirectory))
            {
                var node = old.GetValueOrDefault(entry.RelativePath) ?? new DirectoryNode(entry.Name, entry.RelativePath);
                node.Summary = entry.State is FileState.Equal or FileState.Unchecked ? entry.Summary
                    : entry.Summary == "已检查项无差异" ? entry.StateText : entry.StateText + " · " + entry.Summary;
                parent.Children.Add(node);
            }
            parent.Loaded = true;
            if (updateSummary) parent.Summary = Session != null && Snapshot != null
                ? GetDirectorySummary(parent, Snapshot) : "未检查";
        }
        finally { IsUpdatingTree = wasUpdating; }
    }

    private string GetDirectorySummary(DirectoryNode node, RepositorySnapshot snapshot)
    {
        var path = RepositoryService.GetLocalPath(Session!, node.RelativePath);
        var summary = RepositoryService.GetSummary(snapshot, path);
        if (path == null) return summary;
        if (GetDirectoryEntry(node.RelativePath)?.IsLocallyIgnored == true) return FileState.Ignored.DisplayName();
        snapshot.Statuses.TryGetValue(path, out var status);
        if (status == null && snapshot.Statuses.Values.Any(parent => (parent.Item == "ignored" || parent.IsLocallyIgnored) && RepositoryService.IsDescendant(path, parent.Path)))
            return FileState.Ignored.DisplayName();
        var onServer = status?.RepositoryItem != "deleted" && (status?.RepositoryItem == "added" || status?.Item is not ("added" or "unversioned" or "ignored"));
        var state = FileStateClassifier.Classify(onServer,
            Directory.Exists(path), status, true);
        return state is FileState.Equal or FileState.Unchecked ? summary
            : summary == "已检查项无差异" ? state.DisplayName() : state.DisplayName() + " · " + summary;
    }

    private void SetEntries(IReadOnlyList<BrowserEntry> entries)
    {
        if (Snapshot != null) entries = LocalIgnores.Apply(Snapshot, entries);
        // Also filter older disk caches, before rows, selection and counts reach either view.
        entries = entries.Where(entry => !OfficeTemporaryFiles.IsLockFile(entry.Name, entry.IsDirectory)).ToList();
        if (currentEntries.SequenceEqual(entries)) return;
        EntriesUpdating?.Invoke();
        currentEntries.Clear();
        currentEntries.AddRange(entries);
        // Recount only after the full directory is installed.
        updatingEntries = true;
        try
        {
            Entries.Clear();
            foreach (var entry in currentEntries) Entries.Add(entry);
        }
        finally { updatingEntries = false; }
        EntriesUpdated?.Invoke();
        NotifyEntryOverview();
    }

    public DirectoryNode? FindNode(string relative) => AllNodes().FirstOrDefault(n => !n.IsPlaceholder && n.RelativePath == relative);
    private IEnumerable<DirectoryNode> AllNodes()
    {
        var stack = new Stack<DirectoryNode>(Roots.Reverse());
        while (stack.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children.Reverse()) stack.Push(child);
        }
    }

    private void NotifyLocation()
    {
        UpdatePreview();
        Notify(nameof(CurrentRelativePathText));
        Notify(nameof(CurrentPath)); Notify(nameof(SnapshotText)); Notify(nameof(RecentLocations)); Notify(nameof(DirectoryCacheText));
        Notify(nameof(CurrentLocalPath)); Notify(nameof(CanOpenCurrentDirectory));
        Notify(nameof(HasSession)); Notify(nameof(IsOnlineOnly));
        NotifyActionAvailability();
        NotifyEntryOverview();
    }

    public void AddLog(string text)
    {
        if (disposed) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => AddLog(text));
            return;
        }
        ActivityLog.Insert(0, text);
        while (ActivityLog.Count > 300) ActivityLog.RemoveAt(ActivityLog.Count - 1);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ResetSearchIndex();
        Preview.Dispose();
        Cancel();
        progressTimer.Stop();
        Runner.ProgressChanged -= OnProgressChanged;
        if (backgroundReads.Count == 0) Runner.Dispose();
        else _ = DisposeRunnerAfterReadsAsync();
    }
}
