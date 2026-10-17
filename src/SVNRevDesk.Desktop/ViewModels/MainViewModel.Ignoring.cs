using System.IO;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public async Task SetIgnoredAsync(BrowserEntry entry, bool ignored, CancellationToken token)
    {
        var session = Session ?? throw new InvalidOperationException("请先打开工作副本。");
        if (session.LocalRoot == null || entry.LocalPath == null) throw new InvalidOperationException("请先关联本地工作副本。");
        AddLog(await Task.Run(() => Operations.SetIgnoredAsync(entry.LocalPath, session.LocalRoot, ignored, token), token));
        // Once the local configuration is saved, publish state even if cancellation arrives afterwards.
        await RefreshLocalTransfersAsync(session, currentRelative, Path.GetDirectoryName(entry.LocalPath)!,
            [entry.LocalPath], [], [], preserveFilters: true);
    }
}
