using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed record SearchMount(string RelativePath, string Url, bool IsDirectory);
public sealed record SearchServerNode(string RelativePath, string Url, ServerEntry Entry);
public sealed record SearchPathChange(string RelativePath, bool IsDirectory, string Action);
public sealed record SearchIndexStatus(bool HasIndex, long Revision, int EntryCount, DateTimeOffset? ServerCheckedAt,
    DateTimeOffset? LocalCheckedAt, string Error, bool NeedsRebuild);

public interface ISearchIndexSource : IDisposable
{
    Task<long> ReadRevisionAsync(RepositorySession session, CancellationToken token);
    Task<IReadOnlyList<ServerEntry>> ReadEntriesAsync(string url, long revision, bool recursive, CancellationToken token);
    Task<SvnInfo?> ReadNodeAsync(string url, long revision, CancellationToken token);
    Task<IReadOnlyList<SearchPathChange>> ReadChangesAsync(string url, long fromRevision, long toRevision, CancellationToken token);
    Task<RepositorySnapshot> ReadLocalAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token);
}
