using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private RepositorySearchIndex? searchIndex;
    private long searchIndexUpdateVersion;
    private (RepositorySession Session, string Executable, string? User, string? Password, string? Config, string? Template)? searchIdentity;

    private void ResetSearchIndex()
    {
        // A replacement index must not merge its requests into the disposed index's task.
        queuedIndexRead?.Cancellation.Cancel();
        searchIndex?.Dispose(); searchIndex = null; searchIdentity = null;
    }

    public RepositorySearchIndex GetSearchIndex()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var session = Session ?? throw new InvalidOperationException("请先打开仓库。");
        var identity = (session, Runner.Executable, Runner.Username, Runner.Password, Runner.ConfigurationDirectory, Runner.ConfigurationTemplateDirectory);
        if (searchIndex != null && searchIdentity == identity) return searchIndex;
        ResetSearchIndex();
        var indexRunner = new SvnRunner { Executable = Runner.Executable, Username = Runner.Username, Password = Runner.Password,
            ConfigurationDirectory = Runner.ConfigurationDirectory, ConfigurationTemplateDirectory = Runner.ConfigurationTemplateDirectory };
        searchIndex = new(session, new SvnSearchIndexSource(indexRunner, ownsRunner: true), Settings.CacheDirectory,
            new LocalIgnoreStore(Settings.LocalIgnoreConfigurationPath), Runner.Username);
        searchIdentity = identity;
        return searchIndex;
    }

    public Task<RepositorySearchResult> SearchAsync(string scope, string query, bool includeIgnored,
        IProgress<RepositorySearchProgress> progress, CancellationToken token)
    {
        var index = GetSearchIndex();
        return Task.Run(() => index.SearchAsync(scope, query, includeIgnored, progress, token), token);
    }

    // Refresh the index's own snapshot, including switched/sparse mappings, without moving the main browser.
    public async Task<(BrowserEntry Entry, long Revision)> ReadSearchResultAsync(string relative, CancellationToken token)
    {
        var session = Session ?? throw new InvalidOperationException("请先打开仓库。");
        if (relative.Length == 0 || !IsValidRelativeDirectory(relative) || relative.Split('/').Any(part => part.Length == 0 || part.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("搜索结果必须位于当前打开范围内。");
        var index = GetSearchIndex();
        await Task.Run(() => index.RefreshAsync(token: token), token);
        token.ThrowIfCancellationRequested();
        if (session != Session || disposed) throw new OperationCanceledException("仓库会话已变化。");
        return index.FindEntry(relative)
            ?? throw new InvalidOperationException("此项目已变化或删除，请刷新索引后重试。");
    }

    private void QueueSearchIndexUpdate(RepositorySession session)
    {
        if (disposed || session != Session || searchIndex == null
            && !RepositorySearchIndex.HasSavedIndex(Settings.CacheDirectory, session, Runner.Username)) return;
        var index = GetSearchIndex();
        searchIndexUpdateVersion++;
        if (queuedIndexRead is { Cancellation.IsCancellationRequested: false }) return;
        // Index maintenance reconciles the entire working copy, which can be slow on network drives.
        // Track it as a cancellable read so completed writes release the UI and later writes take priority.
        _ = StartBackgroundRead("后台更新搜索索引，可继续操作", async token =>
        {
            try
            {
                // Merge requests while maintenance is running. If a refresh arrives during a pass,
                // reconcile again with the latest revision instead of queuing another waiting task.
                long version;
                do
                {
                    token.ThrowIfCancellationRequested();
                    version = searchIndexUpdateVersion;
                    var knownRevision = Snapshot?.Revision;
                    await Task.Run(() => index.RefreshAsync(knownRevision: knownRevision, token: token), token);
                } while (version != searchIndexUpdateVersion && !disposed && Session == session && searchIndex == index);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!disposed && Session == session && searchIndex == index)
                    AddLog("搜索索引更新未完成，保留缓存：" + ex.Message);
            }
        }, navigation: false, reportCompletion: false, indexMaintenance: true);
    }

    private async Task UpdateLocalSearchIndexAsync(RepositorySession session, IEnumerable<string> paths)
    {
        if (disposed || session != Session || searchIndex == null) return;
        var index = GetSearchIndex();
        try { await Task.Run(() => index.RefreshLocalAsync(paths)); }
        catch (Exception ex) { if (!disposed && Session == session) AddLog("本地搜索索引更新未完成：" + ex.Message); }
    }
}
