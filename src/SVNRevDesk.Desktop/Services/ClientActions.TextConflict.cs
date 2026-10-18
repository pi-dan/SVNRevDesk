using System.Diagnostics;
using SVNRevDesk.Core.Comparison;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop.Services;

public sealed partial class ClientActions
{
    private async Task MergeTextConflictAsync(BrowserEntry entry, CancellationToken token)
    {
        var session = await model.Operations.PrepareTextMergeAsync(Local(entry),
            model.Session?.LocalRoot ?? throw new InvalidOperationException("请先打开本地工作副本。"), token);
        model.AddLog("文本冲突备份和草稿：" + session.Directory);
        var window = new TextConflictWindow(owner, session, LaunchTextMergeAsync, model.Operations.ApplyTextMergeAsync, token);
        try { model.WaitForUserInput(() => window.ShowDialog(), result => result != true); }
        finally { await model.RefreshPathsAsync([session.Path], token); }
        if (window.Applied) model.AddLog("文本冲突已解决，合并结果保留为本地修改，请检查后手动提交。");
    }

    private async Task LaunchTextMergeAsync(TextMergeSession session, CancellationToken token)
    {
        // A configured two-file comparison tool has no defined three-way merge contract.
        var executable = await WinMergeExecutable.ResolveAsync(null, model.Settings.StorageDirectory, token);
        var (monitor, workArea) = WinMergeWindowPlacement.GetMonitorAreas(owner);
        await Task.Run(() => WinMergeWindowPlacement.Prepare(model.Settings.StorageDirectory, monitor, workArea), token);
        var start = WinMergeExecutable.CreateMergeStartInfo(executable, session.LocalFile,
            session.BaseFile, session.IncomingFile, session.ResultFile, model.Settings.StorageDirectory);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 WinMerge。");
        // Cancellation stops waiting, not the editor: unsaved edits must not be killed.
        await process.WaitForExitAsync(token);
        if (process.ExitCode is not (0 or 1)) throw new InvalidOperationException($"WinMerge 退出异常（{process.ExitCode}），请检查保存的合并草稿。");
    }
}
