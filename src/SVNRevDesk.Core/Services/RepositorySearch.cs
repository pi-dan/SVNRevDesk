using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed record RepositorySearchProgress(string Directory, int DirectoriesRead, IReadOnlyList<BrowserEntry> Matches);
public sealed record RepositorySearchResult(long Revision, int DirectoriesRead, int MatchCount, bool LimitReached);

// Reuse directory listing rules, including sparse nodes, switched URLs and local-only children.
public sealed class RepositorySearch(IRepositoryService repository)
{
    public async Task<RepositorySearchResult> SearchAsync(RepositorySession session, string scope, string query,
        bool includeIgnored, IProgress<RepositorySearchProgress>? progress, CancellationToken token, int limit = 5000)
    {
        query = query.Trim().Replace('\\', '/');
        if (query.Length == 0) throw new ArgumentException("请输入要查找的名称或路径。");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var snapshot = await repository.RefreshDirectoryAsync(session, new DirectoryRefresh(scope, true), token);
        var pending = new Queue<string>(); pending.Enqueue(scope);
        var count = 0; var read = 0;
        while (pending.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var entries = await repository.ReadDirectoryAsync(session, snapshot, directory, token);
            read++;
            var matches = new List<BrowserEntry>();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!includeIgnored && entry.State == FileState.Ignored) continue;
                if (entry.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                {
                    matches.Add(entry); count++;
                    if (count >= limit)
                    {
                        progress?.Report(new(directory, read, matches));
                        return new(snapshot.Revision, read, count, true);
                    }
                }
                if (entry.IsDirectory && CanDescend(entry, snapshot)) pending.Enqueue(entry.RelativePath);
            }
            progress?.Report(new(directory, read, matches));
        }
        return new(snapshot.Revision, read, count, false);
    }

    private static bool CanDescend(BrowserEntry entry, RepositorySnapshot snapshot)
    {
        if (entry.LocalPath is not { } local) return true;
        if (snapshot.Statuses.TryGetValue(local, out var status) && status.Item == "external") return false;
        if (!Directory.Exists(local)) return true;
        // Do not follow junctions or traverse a second working copy.
        return (File.GetAttributes(local) & FileAttributes.ReparsePoint) == 0
            && !Directory.Exists(Path.Combine(local, ".svn"));
    }
}
