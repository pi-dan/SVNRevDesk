using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed partial class RepositorySearchIndex
{
    private sealed record LocalNode(bool IsDirectory, long? Size);
    private Dictionary<string, LocalNode> localNodes = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
    private RepositorySnapshot localSnapshot = new(0, default,
        new Dictionary<string, WorkingStatus>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, SvnInfo>(StringComparer.OrdinalIgnoreCase));

    private void StartWatcher()
    {
        if (watcher != null || session.LocalRoot == null || !Directory.Exists(session.LocalRoot) || Volatile.Read(ref disposed) != 0) return;
        watcher = new(session.LocalRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += LocalChanged; watcher.Changed += LocalChanged; watcher.Deleted += LocalChanged;
        watcher.Renamed += (_, args) => QueueLocalChanges([args.OldFullPath, args.FullPath]);
        watcher.Error += (_, _) => QueueLocalChanges([session.LocalRoot]);
        watcher.EnableRaisingEvents = true;
    }

    private void LocalChanged(object sender, FileSystemEventArgs args) => QueueLocalChanges([args.FullPath]);

    public void QueueLocalChanges(IEnumerable<string> paths)
    {
        if (session.LocalRoot == null || Volatile.Read(ref disposed) != 0) return;
        foreach (var path in paths)
        {
            if (!path.Equals(session.LocalRoot, StringComparison.OrdinalIgnoreCase) && !RepositoryService.IsDescendant(path, session.LocalRoot)) continue;
            var relative = Path.GetRelativePath(session.LocalRoot, path).Replace('\\', '/');
            if (relative == ".") relative = "";
            if (relative.Split('/').Any(part => part.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
            {
                // Metadata-only external SVN commands (add/revert/switch) also need local reconciliation.
                if (relative.Equals(".svn/wc.db", StringComparison.OrdinalIgnoreCase)) pendingLocal[""] = 0;
                continue;
            }
            if (relative.Split('/').Any(part => part.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase))) continue;
            pendingLocal[relative] = 0;
        }
        if (pendingLocal.IsEmpty) return;
        try { localTimer.Change(500, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public async Task RefreshLocalAsync(IEnumerable<string>? paths = null, CancellationToken token = default)
    {
        await LoadAsync(token);
        if (paths == null && session.LocalRoot != null) pendingLocal[""] = 0;
        else if (paths != null) QueueLocalChanges(paths);
        await ProcessLocalChangesAsync(token, reportFailure: true);
    }

    private async Task ProcessLocalChangesAsync(CancellationToken token = default, bool reportFailure = false)
    {
        if (session.LocalRoot == null || Volatile.Read(ref disposed) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, token);
        var entered = false;
        try
        {
            await maintenance.WaitAsync(linked.Token); entered = true;
            if (!loaded || pendingLocal.IsEmpty) return;
            var changed = pendingLocal.Keys.Where(path => pendingLocal.TryRemove(path, out _)).ToArray();
            var scopes = new List<DirectoryRefresh>();
            foreach (var relative in changed)
            {
                if (relative.Length == 0) { scopes.Add(new("", true)); continue; }
                scopes.Add(new(Parent(relative)));
                if (Directory.Exists(RepositoryService.GetLocalPath(session, relative)) || localNodes.GetValueOrDefault(relative)?.IsDirectory == true)
                    scopes.Add(new(relative, true));
            }
            scopes = scopes.Distinct().Where(scope => !scopes.Any(other => other != scope && other.IncludeDescendants
                && (scope.RelativePath == other.RelativePath || DirectoryRefresh.IsInside(scope.RelativePath, other.RelativePath)))).ToList();
            if (scopes.Count == 0) return;
            await ReadLocalScopesAsync(scopes, linked.Token);
            Publish(Project(serverNodes, mounts));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { if (reportFailure) throw; }
        catch (Exception ex)
        {
            localError = "本地索引更新失败，保留上次清单：" + ex.Message;
            if (entered) Publish(published?.Entries ?? []);
            if (reportFailure) throw;
        }
        finally { if (entered) maintenance.Release(); }
    }

    private async Task ReadLocalScopesAsync(IReadOnlyList<DirectoryRefresh> scopes, CancellationToken token)
    {
        if (session.LocalRoot == null) { localKnown = true; return; }
        ignores.Reload();
        var statuses = localSnapshot.Statuses.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var infos = localSnapshot.LocalInfo.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, LocalNode>(localNodes, StringComparer.OrdinalIgnoreCase);
        var exclusions = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            token.ThrowIfCancellationRequested(); SearchIndexStore.ValidateRelative(scope.RelativePath, true);
            var partial = await source.ReadLocalAsync(session, scope, token);
            bool Contains(string relative) => relative == scope.RelativePath || (scope.IncludeDescendants
                ? DirectoryRefresh.IsInside(relative, scope.RelativePath) : Parent(relative) == scope.RelativePath);
            foreach (var path in statuses.Keys.Where(path => Contains(Relative(path))).ToArray()) statuses.Remove(path);
            foreach (var path in infos.Keys.Where(path => Contains(Relative(path))).ToArray()) infos.Remove(path);
            foreach (var path in files.Keys.Where(Contains).ToArray()) files.Remove(path);
            exclusions.RemoveWhere(path => Contains(path));
            foreach (var (path, status) in partial.Statuses)
                if (path.Equals(session.LocalRoot, StringComparison.OrdinalIgnoreCase) || RepositoryService.IsDescendant(path, session.LocalRoot)) statuses[path] = ignores.Apply(status);
            foreach (var (path, info) in partial.LocalInfo)
                if (path.Equals(session.LocalRoot, StringComparison.OrdinalIgnoreCase) || RepositoryService.IsDescendant(path, session.LocalRoot)) infos[path] = info;
            Scan(scope.RelativePath, scope.IncludeDescendants, files, exclusions, statuses, infos, token);
        }
        token.ThrowIfCancellationRequested();
        localNodes = files; blocked = exclusions;
        localSnapshot = new(0, DateTimeOffset.Now, statuses, infos);
        localKnown = true; localCheckedAt = DateTimeOffset.Now; localError = "";

        string Relative(string path) { var relative = Path.GetRelativePath(session.LocalRoot, path).Replace('\\', '/'); return relative == "." ? "" : relative; }
    }

    private void Scan(string relative, bool recursive, Dictionary<string, LocalNode> files, HashSet<string> exclusions,
        Dictionary<string, WorkingStatus> statuses, Dictionary<string, SvnInfo> infos, CancellationToken token)
    {
        var local = RepositoryService.GetLocalPath(session, relative)!;
        if (!Directory.Exists(local)) return;
        var pending = new Stack<(string Path, string Relative)>(); pending.Push((local, relative));
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (directory.Relative.Length > 0 && CannotDescend(directory.Path, statuses, infos))
            {
                exclusions.Add(directory.Relative);
                foreach (var path in files.Keys.Where(path => DirectoryRefresh.IsInside(path, directory.Relative)).ToArray()) files.Remove(path);
                continue;
            }
            if (directory.Relative.Length > 0) files[directory.Relative] = new(true, null);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (name.Equals(".svn", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (OfficeTemporaryFiles.IsLockFile(name, isDirectory)) continue;
                var child = RepositoryService.JoinRelative(directory.Relative, name);
                files[child] = new(isDirectory, isDirectory ? null : SafeSize(path));
                if (isDirectory)
                {
                    if (CannotDescend(path, statuses, infos))
                    {
                        exclusions.Add(child);
                        foreach (var old in files.Keys.Where(old => DirectoryRefresh.IsInside(old, child)).ToArray()) files.Remove(old);
                    }
                    else if (recursive) pending.Push((path, child));
                }
            }
        }
    }

    private bool CannotDescend(string path, IReadOnlyDictionary<string, WorkingStatus> statuses, IReadOnlyDictionary<string, SvnInfo> infos) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(Path.Combine(path, ".svn"))
        || statuses.GetValueOrDefault(path)?.Item == "external" || infos.GetValueOrDefault(path) is { } info && info.RepositoryId != session.RepositoryId;

    private static long? SafeSize(string path)
    {
        try { return new FileInfo(path).Length; } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
    }

    private bool IsBlocked(string relative, bool includeSelf = false) => blocked.Any(parent => includeSelf && relative.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase));

    private BrowserEntry[] Project(IReadOnlyDictionary<string, SearchServerNode> nodes, SearchMount[] mapping)
    {
        var comparer = session.LocalRoot == null ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var server = new Dictionary<string, SearchServerNode>(comparer);
        foreach (var node in nodes.Values.Where(node => !IsBlocked(node.RelativePath))) server[node.RelativePath] = node;
        var paths = server.Keys.Concat(localNodes.Keys).Concat(localSnapshot.Statuses.Keys.Select(path => Relative(path)))
            .Where(path => path.Length > 0 && !IsBlocked(path)).Distinct(comparer).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);
        var result = new List<BrowserEntry>();
        foreach (var path in paths)
        {
            server.TryGetValue(path, out var remote); localNodes.TryGetValue(path, out var local);
            var localPath = RepositoryService.GetLocalPath(session, path);
            var status = FindLocalStatus(path);
            var info = localPath == null ? null : localSnapshot.LocalInfo.GetValueOrDefault(localPath);
            var directory = remote?.Entry.IsDirectory ?? local?.IsDirectory ?? info?.IsDirectory ?? false;
            if (OfficeTemporaryFiles.IsLockFile(path, directory) || path.Split('/').Any(part => part.Equals(".svn", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase))) continue;
            if (status != null && remote?.Entry.ChangedRevision > status.BaseRevision && status.BaseRevision >= 0)
                status = status with { RepositoryItem = "modified" };
            var mount = SearchIndexStore.OwningMount(mapping, path);
            var url = remote?.Url ?? RepositoryService.AppendUrl(mount.Url, SearchIndexStore.RelativeTo(path, mount.RelativePath));
            var state = localKnown ? FileStateClassifier.Classify(remote != null, local != null, status, session.LocalRoot != null) : FileState.Unchecked;
            result.Add(new(path.Split('/')[^1], path, url, localPath, directory, state, status?.BaseRevision,
                status?.ChangedRevision ?? info?.ChangedRevision, remote?.Entry.ChangedRevision, remote?.Entry.Size ?? local?.Size,
                remote?.Entry.Author ?? "", remote?.Entry.ChangedAt, remote?.Entry.Lock ?? status?.ServerLock,
                RepositoryService.GetDetail(status, session.LocalRoot != null), "") { IsUnversioned = status?.Item == "unversioned" });
        }
        return ignores.Apply(localSnapshot, result).ToArray();

        string Relative(string path) { var relative = Path.GetRelativePath(session.LocalRoot!, path).Replace('\\', '/'); return relative == "." ? "" : relative; }
    }

    private WorkingStatus? FindLocalStatus(string relative)
    {
        if (session.LocalRoot == null) return null;
        var path = RepositoryService.GetLocalPath(session, relative)!;
        var own = localSnapshot.Statuses.GetValueOrDefault(path);
        for (var parent = Parent(relative); ; parent = Parent(parent))
        {
            var inherited = localSnapshot.Statuses.GetValueOrDefault(RepositoryService.GetLocalPath(session, parent)!);
            if (own == null && inherited?.Item is "unversioned" or "ignored") return inherited with { Path = path };
            if (own?.Item is "normal" or "modified" or "replaced" && inherited?.Item == "added") return own with { Item = "added" };
            if (parent.Length == 0) break;
        }
        return own;
    }

    private static string Parent(string relative) => relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
}
