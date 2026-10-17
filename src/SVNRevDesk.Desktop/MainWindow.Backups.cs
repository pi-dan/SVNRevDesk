using System.IO;
using System.Windows.Threading;
using SVNRevDesk.Core.Formatting;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private readonly BackupManager backupManager;
    private readonly DispatcherTimer backupCleanupTimer = new() { Interval = TimeSpan.FromDays(1) };
    private readonly CancellationTokenSource backupLifetime = new();
    private CancellationTokenSource? automaticBackupCancellation;
    private Task automaticBackupCleanup = Task.CompletedTask;

    private void StartAutomaticBackupCleanup()
    {
        if (backupLifetime.IsCancellationRequested || !model.Settings.AutoBackupCleanup || !automaticBackupCleanup.IsCompleted) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(backupLifetime.Token);
        automaticBackupCancellation = cancellation;
        automaticBackupCleanup = CleanupBackupsAutomaticallyAsync(model.Settings.BackupRetentionDays, cancellation);
    }

    private async Task CleanupBackupsAutomaticallyAsync(int days, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await backupManager.CleanupExpiredAsync(days, cancellation.Token);
            if (backupLifetime.IsCancellationRequested) return;
            if (result.DeletedCount > 0)
                model.AddLog($"自动清理超过 {days} 天的备份：{result.DeletedCount} 项，释放 {FileSizeFormatter.Format(result.DeletedBytes)}。");
            foreach (var error in result.Errors) model.AddLog("备份清理：" + error);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            if (!backupLifetime.IsCancellationRequested) model.AddLog($"自动清理备份未完成：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(automaticBackupCancellation, cancellation)) automaticBackupCancellation = null;
            cancellation.Dispose();
        }
    }
}
