using System.Diagnostics;
using System.IO;
using System.Windows;
using SVNRevDesk.Core.Comparison;

namespace SVNRevDesk.Desktop.Services;

public static class ComparisonTool
{
    public static async Task<ProcessStartInfo> PrepareAsync(Window owner, string configuredPath,
        string leftPath, string rightPath, string leftTitle, string rightTitle,
        string applicationDirectory, CancellationToken token)
    {
        var executable = await WinMergeExecutable.ResolveAsync(configuredPath, applicationDirectory, token);
        token.ThrowIfCancellationRequested();
        if (Path.GetFileName(executable).Equals(WinMergeExecutable.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            var (monitor, workArea) = WinMergeWindowPlacement.GetMonitorAreas(owner);
            await Task.Run(() => WinMergeWindowPlacement.Prepare(applicationDirectory, monitor, workArea), token);
        }
        token.ThrowIfCancellationRequested();
        return WinMergeExecutable.CreateStartInfo(executable, leftPath, rightPath, leftTitle, rightTitle, applicationDirectory);
    }
}
