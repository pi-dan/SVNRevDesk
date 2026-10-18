using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed record RepositorySearchProgress(string Directory, int DirectoriesRead, IReadOnlyList<BrowserEntry> Matches);
public sealed record RepositorySearchResult(long Revision, int DirectoriesRead, int MatchCount, bool LimitReached)
{
    public bool FromIndex { get; init; }
    public int IndexedCount { get; init; }
    public DateTimeOffset? ServerCheckedAt { get; init; }
    public DateTimeOffset? LocalCheckedAt { get; init; }
}
