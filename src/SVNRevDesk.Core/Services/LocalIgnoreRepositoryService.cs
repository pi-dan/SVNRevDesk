using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed class LocalIgnoreRepositoryService(IRepositoryService inner, LocalIgnoreStore ignores) : IRepositoryService
{
    public Task<RepositorySession> OpenWorkingCopyAsync(string directory, CancellationToken token) => inner.OpenWorkingCopyAsync(directory, token);
    public Task<RepositorySession> OpenUrlAsync(string url, CancellationToken token) => inner.OpenUrlAsync(url, token);
    public async Task<RepositorySnapshot> RefreshAsync(RepositorySession session, CancellationToken token)
    {
        ignores.Reload();
        return ignores.Apply(await inner.RefreshAsync(session, token));
    }
    public async Task<RepositorySnapshot> RefreshDirectoryAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
    {
        ignores.Reload();
        return ignores.Apply(await inner.RefreshDirectoryAsync(session, scope, token));
    }
    public async Task<IReadOnlyList<BrowserEntry>> ReadDirectoryAsync(RepositorySession session, RepositorySnapshot snapshot, string relativePath, CancellationToken token) =>
        ignores.Apply(snapshot, await inner.ReadDirectoryAsync(session, ignores.Apply(snapshot), relativePath, token));
}
