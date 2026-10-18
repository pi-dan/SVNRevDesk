using System.IO;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public event Action<string>? EntryCreated;
    public event Action<string>? LocalItemCreationStarted;
    public event Action<string>? LocalItemCreationFinished;

    public bool CanCreateInDirectory(string relative)
    {
        if (!CanOperate || Session?.LocalRoot == null || Snapshot == null || !IsValidRelativeDirectory(relative)) return false;
        var path = RepositoryService.GetLocalPath(Session, relative)!;
        return Directory.Exists(path) && IsCreationDirectoryMapped(path);
    }

    private bool IsCreationDirectoryMapped(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if (Snapshot!.Statuses.GetValueOrDefault(current)?.Item == "external"
                || Snapshot.LocalInfo.TryGetValue(current, out var info) && info.RepositoryId != Session!.RepositoryId
                || Path.GetFileName(current).Equals(".svn", StringComparison.OrdinalIgnoreCase)) return false;
            if (current.Equals(Session!.LocalRoot, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(Path.Combine(current, ".svn"))) return false;
        }
        return true;
    }

    public async Task CreateLocalItemAsync(string relative, NewItemRequest request, bool addToSvn, CancellationToken token)
    {
        var session = Session ?? throw new InvalidOperationException("请先打开本地工作副本。");
        if (session.LocalRoot == null) throw new InvalidOperationException("请先关联或检出本地工作副本。");
        if (!IsValidRelativeDirectory(relative)) throw new ArgumentException("创建目录必须位于工作副本内。");
        var directory = RepositoryService.GetLocalPath(session, relative)!;
        if (Snapshot == null || !IsCreationDirectoryMapped(directory))
            throw new InvalidOperationException("外部工作副本或 SVN 元数据目录需单独处理。");
        if (FindNode(relative) == null) throw new InvalidOperationException("目录节点已变化，请重新选择目录。");
        LocalItemCreator.ValidateName(request.Name);
        if (directoryCache.TryGetValue(relative, out var existing)
            && existing.Entries.Any(entry => entry.ServerRevision != null && entry.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("服务器缓存中已有同名项目，请换一个名称；下载已有项目可使用“更新”。");
        var path = Path.Combine(directory, request.Name);
        SetProgressTarget(path);
        LocalItemCreationStarted?.Invoke(path);
        try
        {
            await Task.Run(() => LocalItemCreator.CreateAsync(session.LocalRoot, directory, request, token), token);
            AddLog($"已创建：{path}");
            Exception? addError = null;
            var scheduled = false;
            if (addToSvn)
            {
                try { AddLog(await Operations.AddAsync([path], token)); scheduled = true; }
                catch (Exception ex) when (ex is SvnException or InvalidOperationException or OperationCanceledException
                    or IOException or System.ComponentModel.Win32Exception)
                { addError = ex; }
            }
            // Publish the created item even when optional SVN scheduling fails or is cancelled.
            IReadOnlyList<WorkingStatus> statuses;
            try
            {
                statuses = await Task.Run(() => Operations.ReadLocalDirectoryStatusAsync(directory, session.LocalRoot, CancellationToken.None));
            }
            catch (Exception ex) when (ex is SvnException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                AddLog($"本地状态读取失败：{ex.Message}");
                statuses = [new WorkingStatus(path, scheduled ? "added" : "unversioned", "none", "", "", null, null, false, false, null, null)];
            }
            if (disposed || session != Session) return;
            var merged = Snapshot!.Statuses.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var local in statuses)
            {
                var previous = merged.GetValueOrDefault(local.Path);
                merged[local.Path] = local with
                {
                    RepositoryItem = previous?.RepositoryItem ?? "",
                    RepositoryProperties = previous?.RepositoryProperties ?? "",
                    ServerLock = previous?.ServerLock
                };
            }
            Snapshot = LocalIgnores.Apply(Snapshot with { Statuses = merged });
            cacheGeneration++;
            var hasServerListing = existing?.HasServerListing == true || merged.GetValueOrDefault(directory)?.Item is "added" or "unversioned";
            var entries = (await Task.Run(() => RepositoryService.ReadLocalDirectory(session, Snapshot, relative,
                existing?.Entries ?? [], hasServerListing))).ToList();
            entries = LocalIgnores.Apply(Snapshot, entries).ToList();
            var cachedSnapshot = Snapshot with
            {
                Revision = existing?.Snapshot.Revision ?? Snapshot.Revision,
                CheckedAt = existing?.Snapshot.CheckedAt ?? Snapshot.CheckedAt
            };
            directoryCache[relative] = new(cachedSnapshot, entries) { HasServerListing = hasServerListing };
            if (request.Kind == NewItemKind.Folder)
            {
                var newRelative = RepositoryService.JoinRelative(relative, request.Name);
                directoryCache[newRelative] = new(Snapshot, []);
            }
            // Cached ancestors keep their server timestamps, but their local difference summaries change immediately.
            UpdateCachedSummaries();
            foreach (var ancestor in AllNodes().Where(node => !node.IsPlaceholder))
                ancestor.Summary = GetDirectorySummary(ancestor, Snapshot);
            var wasUpdating = IsUpdatingTree;
            IsUpdatingTree = true;
            try
            {
                var node = FindNode(relative);
                if (node == null) throw new InvalidOperationException("创建成功，但目录节点已变化，请重新浏览该目录。");
                FilterText = "";
                SelectedState = "全部状态";
                ShowDirectory(relative, node, directoryCache[relative].Entries);
                node.Loaded = hasServerListing;
                node.IsExpanded = true;
                for (var parent = relative; parent.Length > 0;)
                {
                    parent = ParentDirectory(parent);
                    if (FindNode(parent) is { } ancestor) ancestor.IsExpanded = true;
                }
            }
            finally { IsUpdatingTree = wasUpdating; }
            navigationVersion++;
            RememberSession();
            NotifyLocation();
            EntryCreated?.Invoke(RepositoryService.JoinRelative(relative, request.Name));
            await UpdateLocalSearchIndexAsync(session, [path]);
            if (addError != null)
                throw new InvalidOperationException($"项目已创建并保留在本地，但添加到 SVN 未完成：\n{path}\n\n{addError.Message}", addError);
        }
        finally { LocalItemCreationFinished?.Invoke(path); }
    }
}
