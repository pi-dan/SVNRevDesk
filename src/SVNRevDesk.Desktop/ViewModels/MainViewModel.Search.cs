using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public Task<RepositorySearchResult> SearchAsync(string scope, string query, bool includeIgnored,
        IProgress<RepositorySearchProgress> progress, CancellationToken token)
    {
        var session = Session ?? throw new InvalidOperationException("请先打开仓库。");
        return Task.Run(() => new RepositorySearch(repository).SearchAsync(session, scope, query, includeIgnored, progress, token), token);
    }
}
