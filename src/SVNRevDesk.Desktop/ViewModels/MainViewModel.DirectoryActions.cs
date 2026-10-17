using System.IO;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    // Right-clicking a tree node uses existing metadata, including unvisited and deleted directories.
    public BrowserEntry? GetDirectoryEntry(string relative)
    {
        if (Session == null || Snapshot == null || FindNode(relative) is not { IsPlaceholder: false } node) return null;
        if (relative.Length > 0 && directoryCache.TryGetValue(ParentDirectory(relative), out var parent)
            && parent.Entries.FirstOrDefault(entry => entry.RelativePath == relative) is { } cached) return cached;
        var local = RepositoryService.GetLocalPath(Session, relative);
        var status = local != null ? Snapshot.Statuses.GetValueOrDefault(local) : null;
        var info = local != null ? Snapshot.LocalInfo.GetValueOrDefault(local) : null;
        var onServer = status?.RepositoryItem != "deleted" && status?.Item is not ("added" or "unversioned" or "ignored");
        if (local != null && Snapshot.Statuses.Values.Any(ancestor => ancestor.Item is "added" or "unversioned" or "ignored"
            && RepositoryService.IsDescendant(local, ancestor.Path))) onServer = false;
        return new(node.Name, relative, info?.Url ?? RepositoryService.AppendUrl(Session.Url, relative), local, true,
            FileStateClassifier.Classify(onServer, local != null && Directory.Exists(local), status, Session.LocalRoot != null),
            status?.BaseRevision ?? info?.Revision, status?.ChangedRevision ?? info?.ChangedRevision,
            onServer ? info?.ChangedRevision ?? Snapshot.Revision : null, null, info?.Author ?? "", null, status?.ServerLock,
            RepositoryService.GetDetail(status, Session.LocalRoot != null), node.Summary)
        { IsUnversioned = status?.Item == "unversioned" };
    }

    public long GetEntrySnapshotRevision(BrowserEntry entry)
    {
        if (entry.IsDirectory && directoryCache.TryGetValue(entry.RelativePath, out var directory)) return directory.Snapshot.Revision;
        return directoryCache.TryGetValue(ParentDirectory(entry.RelativePath), out var parent) ? parent.Snapshot.Revision : Snapshot!.Revision;
    }

    public Task RefreshDirectoryInBackgroundAsync(string relative)
    {
        if (!CanOperate || disposed || Session == null) return Task.CompletedTask;
        CancelBackgroundReads();
        return StartBackgroundRead("后台刷新所选目录，可继续浏览",
            token => RefreshDirectoriesAsync([new DirectoryRefresh(relative)], token), navigation: false);
    }
}
