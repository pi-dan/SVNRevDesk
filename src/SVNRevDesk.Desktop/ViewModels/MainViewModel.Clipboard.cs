using System.IO;
using SVNRevDesk.Core.Formatting;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public async Task<IReadOnlyList<string>> PrepareClipboardFilesAsync(IReadOnlyList<BrowserEntry> selection, CancellationToken token)
    {
        if (selection.Count == 0 || selection.Any(entry => !entry.ExistsLocally && entry.ServerRevision == null))
            throw new InvalidOperationException("所选项目既没有本地文件，也没有可下载的服务器版本。");
        // Ordinary files can be handed to Explorer immediately. Directories get a clean snapshot without .svn.
        var paths = new List<string>();
        var stage = Path.Combine(Settings.TemporaryDirectory, "Clipboard", Guid.NewGuid().ToString("N"));
        foreach (var entry in selection)
        {
            token.ThrowIfCancellationRequested();
            if (entry.ExistsLocally && !entry.IsDirectory)
            {
                LocalFileTransfer.ValidatePath(entry.LocalPath!);
                paths.Add(entry.LocalPath!);
                continue;
            }
            LocalFileTransfer.ValidatePath(stage);
            Directory.CreateDirectory(stage);
            SetProgressTarget(entry.Name);
            if (entry.ExistsLocally)
            {
                var plan = await Task.Run(() => LocalFileTransfer.Prepare([entry.LocalPath!], stage, stage, token), token);
                var progress = CreateFileTransferProgress();
                await Task.Run(() => LocalFileTransfer.CopyAsync(plan, false, Settings.BackupDirectory, token, progress), token);
                paths.Add(plan.TopLevelPaths.Single());
            }
            else
            {
                BeginProgressPhase("下载复制内容", entry.Url);
                var exporter = new RepositoryExporter(Operations);
                var revision = GetEntrySnapshotRevision(entry);
                await exporter.ExportAsync([entry], stage, revision.ToString(), token);
                paths.Add(Path.Combine(stage, entry.Name));
            }
        }
        return paths;
    }

    public async Task PasteLocalFilesAsync(string relative, FileTransferPlan plan, bool replaceExisting, CancellationToken token,
        bool backupBeforeOverwrite = true)
    {
        var session = Session ?? throw new InvalidOperationException("请先打开本地工作副本。");
        if (session.LocalRoot == null || Snapshot == null || !IsValidRelativeDirectory(relative)
            || !IsCreationDirectoryMapped(plan.DestinationDirectory)
            || !string.Equals(plan.DestinationDirectory, RepositoryService.GetLocalPath(session, relative), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("粘贴位置已变化，请重新选择目录。");
        var affected = plan.Items.Select(item => item.Destination).Append(plan.DestinationDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in affected) LocalItemCreationStarted?.Invoke(path);
        var progress = CreateFileTransferProgress();
        try
        {
            await Task.Run(() => LocalFileTransfer.CopyAsync(plan, replaceExisting, Settings.BackupDirectory, token,
                progress, path => AddLog($"粘贴覆盖前备份：{path}"), backupBeforeOverwrite), token);
            AddLog($"粘贴完成：{plan.DestinationDirectory}。跳过 {(!replaceExisting ? plan.Conflicts.Count : 0)} 个同名文件；新增项目尚未加入版本控制。");
        }
        finally
        {
            // Cancellation may leave completed files. Always reflect their local status without querying the server.
            try { await RefreshPastedFilesAsync(session, relative, plan); }
            catch (Exception ex) { AddLog($"粘贴后本地状态读取失败，请刷新：{ex.Message}"); }
            foreach (var path in affected) LocalItemCreationFinished?.Invoke(path);
        }
    }

    private IProgress<FileTransferProgress> CreateFileTransferProgress() => new Progress<FileTransferProgress>(progress =>
    {
        if (disposed || !IsBusy) return;
        SetProgressTarget(progress.Path);
        ProgressMessage = $"已复制 {progress.CompletedFiles}/{progress.FileCount} 个文件 · {FileSizeFormatter.Format(progress.Bytes)} / {FileSizeFormatter.Format(progress.TotalBytes)}";
    });

    private Task RefreshPastedFilesAsync(RepositorySession session, string relative, FileTransferPlan plan) =>
        RefreshLocalTransfersAsync(session, relative, plan.DestinationDirectory, plan.TopLevelPaths,
            plan.Items.Where(item => item.IsDirectory).Select(item => item.Destination).ToArray(), []);

    private async Task RefreshLocalTransfersAsync(RepositorySession session, string relative, string destination,
        IReadOnlyList<string> targets, IReadOnlyList<string> newDirectories, IReadOnlyList<string> movedSources, bool preserveFilters = false)
    {
        if (disposed || Session != session) return;
        var root = session.LocalRoot!;
        var parents = movedSources.Select(Path.GetDirectoryName).Append(destination)
            .Where(path => path != null && (path.Equals(root, StringComparison.OrdinalIgnoreCase) || RepositoryService.IsDescendant(path, root)))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var subtrees = movedSources.Concat(targets).Where(path => RepositoryService.IsDescendant(path, root))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var statuses = await Task.Run(async () =>
        {
            var local = new List<WorkingStatus>();
            foreach (var parent in parents.Where(Directory.Exists))
                local.AddRange(await Operations.ReadLocalDirectoryStatusAsync(parent, root, CancellationToken.None));
            foreach (var folder in subtrees.Where(path => Directory.Exists(path)
                || local.Any(status => status.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && status.Item == "deleted")))
                local.AddRange(await Operations.ReadLocalDirectoryStatusAsync(folder, root, CancellationToken.None, includeDescendants: true));
            return local;
        });
        if (disposed || Session != session) return;
        var merged = Snapshot!.Statuses.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var freshPaths = statuses.Select(status => status.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, status) in merged.ToArray())
            if (status.Item is "unversioned" or "ignored" or "added" && !freshPaths.Contains(path)
                && !File.Exists(path) && !Directory.Exists(path) && subtrees.Any(source => path.Equals(source, StringComparison.OrdinalIgnoreCase)
                    || RepositoryService.IsDescendant(path, source))) merged.Remove(path);
        foreach (var local in statuses)
        {
            var previous = merged.GetValueOrDefault(local.Path);
            merged[local.Path] = local with { RepositoryItem = previous?.RepositoryItem ?? "",
                RepositoryProperties = previous?.RepositoryProperties ?? "", ServerLock = previous?.ServerLock };
        }
        var ignoredRoots = statuses.Where(status => status.Item == "ignored" || status.IsLocallyIgnored).Select(status => status.Path).ToArray();
        foreach (var path in merged.Keys.Where(path => ignoredRoots.Any(parent => RepositoryService.IsDescendant(path, parent))).ToArray())
            merged.Remove(path);
        Snapshot = LocalIgnores.Apply(Snapshot with { Statuses = merged });
        cacheGeneration++;
        var changedDirectories = subtrees.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        var ancestors = new List<string> { relative };
        for (var parent = relative; parent.Length > 0;) { parent = ParentDirectory(parent); ancestors.Add(parent); }
        var directories = directoryCache.Keys.Where(path => changedDirectories.Any(folder => path == folder || DirectoryRefresh.IsInside(path, folder)))
            .Concat(parents.Concat(newDirectories.Where(Directory.Exists)).Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Concat(ancestors).Distinct(StringComparer.Ordinal).OrderBy(path => path.Length).ToArray();
        foreach (var path in directories)
        {
            var local = RepositoryService.GetLocalPath(session, path)!;
            if (!IsCreationDirectoryMapped(local)) continue;
            if (!Directory.Exists(local) && merged.GetValueOrDefault(local)?.Item != "deleted")
            { directoryCache.Remove(path); continue; }
            directoryCache.TryGetValue(path, out var previous);
            var localOnly = false;
            for (var parent = local; parent != null && !parent.Equals(session.LocalRoot, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                if (merged.GetValueOrDefault(parent)?.Item is "unversioned" or "added" or "ignored") { localOnly = true; break; }
            var hasListing = previous?.HasServerListing == true || localOnly;
            var entries = await Task.Run(() => RepositoryService.ReadLocalDirectory(session, Snapshot, path, previous?.Entries ?? [], hasListing));
            entries = LocalIgnores.Apply(Snapshot, entries);
            var cachedSnapshot = Snapshot with { Revision = previous?.Snapshot.Revision ?? Snapshot.Revision,
                CheckedAt = previous?.Snapshot.CheckedAt ?? Snapshot.CheckedAt };
            directoryCache[path] = new(cachedSnapshot, entries) { HasServerListing = hasListing };
        }
        UpdateCachedSummaries();
        foreach (var path in directories)
            if (directoryCache.TryGetValue(path, out var cached) && FindNode(path) is { } node)
            { UpdateChildren(node, cached.Entries); node.Loaded = cached.HasServerListing; }
        foreach (var node in AllNodes().Where(node => !node.IsPlaceholder)) node.Summary = GetDirectorySummary(node, Snapshot);
        var wasUpdating = IsUpdatingTree;
        IsUpdatingTree = true;
        try
        {
            if (!preserveFilters)
            {
                FilterText = "";
                SelectedState = "全部状态";
            }
            ShowDirectory(relative, FindNode(relative), directoryCache[relative].Entries);
        }
        finally { IsUpdatingTree = wasUpdating; }
        navigationVersion++;
        NotifyLocation();
        RememberSession();
        if (!preserveFilters && targets.FirstOrDefault(path => File.Exists(path) || Directory.Exists(path)) is { } first)
            EntryCreated?.Invoke(Path.GetRelativePath(session.LocalRoot!, first).Replace('\\', '/'));
    }
}
