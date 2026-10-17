using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public interface IRepositoryService
{
    Task<RepositorySession> OpenWorkingCopyAsync(string directory, CancellationToken token);
    Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token);
    Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token);
    Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token);
    Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session,
        RepositorySnapshot snapshot, string relativePath, CancellationToken token);
}
