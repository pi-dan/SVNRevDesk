using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class BackupManagementTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var settings = new UserSettings(Path.Combine(output, "backup-management-settings-" + Guid.NewGuid().ToString("N")))
        { AutoBackupCleanup = false, BackupRetentionDays = 45, CloseBehavior = WindowCloseBehavior.Exit };
        var backups = new BackupManager(settings.StorageDirectory);
        var old = Path.Combine(backups.DirectoryPath, DateTime.Now.AddDays(-80).ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var fresh = Path.Combine(backups.DirectoryPath, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        File.WriteAllBytes(Path.Combine(old, "old.txt"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(fresh, "new.txt"), new byte[2048]);
        var owner = new MainWindow(settings);
        using var model = (MainViewModel)owner.DataContext;
        var dialog = new SettingsWindow(null, settings, backups);
        try
        {
            ((TabControl)dialog.FindName("SettingsTabs")).SelectedIndex = 1;
            await dialog.RefreshBackupsAsync();
            var summary = (TextBlock)dialog.FindName("BackupSummaryText");
            var expired = (TextBlock)dialog.FindName("ExpiredSummaryText");
            var progress = (ProgressBar)dialog.FindName("CleanupProgress");
            var days = (TextBox)dialog.FindName("RetentionDaysBox");
            var enabled = (CheckBox)dialog.FindName("AutoCleanupBox");
            var clean = (Button)dialog.FindName("CleanBackupsButton");
            var cleanAll = (Button)dialog.FindName("CleanAllBackupsButton");
            Check(summary.Text.Contains("6 KB") && expired.Text.Contains("1 项") && expired.Text.Contains("4 KB")
                && clean.IsEnabled && cleanAll.IsEnabled && days.Text == "45" && enabled.IsChecked == false,
                "Settings show saved retention, cleanup switch, readable total size and cleanable size");
            Check(progress.Visibility == Visibility.Collapsed && !model.IsBusy,
                "Opening and scanning backup settings displays no operation progress or browser lock");
            render(dialog, Path.Combine(output, "backup-management.png"), 700, 580);

            days.Text = "90";
            await dialog.RefreshBackupsAsync();
            Check(expired.Text.Contains("0 项") && !clean.IsEnabled && cleanAll.IsEnabled,
                "All-backup cleanup remains available when no backups have expired");
            days.Text = "0";
            await dialog.RefreshBackupsAsync();
            Check(dialog.ValidateOptions() != null && !clean.IsEnabled && cleanAll.IsEnabled,
                "Invalid retention disables expiry cleanup and Save but does not block all-backup cleanup");
            days.Text = "45";
            enabled.IsChecked = true;
            await dialog.RefreshBackupsAsync();
            Check(dialog.ValidateOptions() == null && dialog.BackupRetentionDays == 45 && dialog.AutoBackupCleanup
                && !settings.AutoBackupCleanup && settings.BackupRetentionDays == 45,
                "Editing the settings dialog does not apply changes before Save");

            var start = typeof(MainWindow).GetMethod("StartAutomaticBackupCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = typeof(MainWindow).GetField("automaticBackupCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!;
            start.Invoke(owner, null);
            await (Task)task.GetValue(owner)!;
            Check(Directory.Exists(old), "Disabling automatic cleanup retains expired backups");
            settings.AutoBackupCleanup = true;
            start.Invoke(owner, null);
            await (Task)task.GetValue(owner)!;
            Check(!Directory.Exists(old) && File.Exists(Path.Combine(fresh, "new.txt")) && !model.IsBusy
                && ((FrameworkElement)owner.FindName("OperationProgress")).Visibility == Visibility.Collapsed,
                "Automatic cleanup works without a repository and leaves browsing and progress state available");
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("backupCleanupTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            Check(timer.Interval == TimeSpan.FromDays(1), "Running cleanup checks use a separate daily timer");
            await dialog.RefreshBackupsAsync();
            Check(summary.Text.Contains("2 KB") && !clean.IsEnabled && progress.Visibility == Visibility.Collapsed,
                "The management page reflects cleanup and hides progress when idle");

            var cleanup = typeof(SettingsWindow).GetMethod("CleanBackupsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            days.Text = "0";
            await dialog.RefreshBackupsAsync();
            using (var confirmation = new CleanupConfirmation(dialog, accept: false, text =>
            {
                Check(progress.Visibility == Visibility.Collapsed && File.Exists(Path.Combine(fresh, "new.txt"))
                    && !model.IsBusy && text.Contains("1 项") && text.Contains("2 KB") && text.Contains("尚未过期"),
                    "All cleanup confirms count/size and recent backups before displaying progress or deleting files");
            }))
            {
                await (Task)cleanup.Invoke(dialog, [true])!;
                Check(confirmation.Answered && Directory.Exists(fresh) && progress.Visibility == Visibility.Collapsed,
                    "Cancelling all-backup confirmation leaves recent backups and idle progress unchanged");
            }
            var showedProgress = false;
            var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(ProgressBar));
            EventHandler trackProgress = (_, _) => showedProgress |= progress.Visibility == Visibility.Visible;
            visibility.AddValueChanged(progress, trackProgress);
            try
            {
                using var confirmation = new CleanupConfirmation(dialog, accept: true, _ =>
                    Check(progress.Visibility == Visibility.Collapsed, "Progress remains hidden until all-cleanup confirmation is accepted"));
                await (Task)cleanup.Invoke(dialog, [true])!;
                Check(confirmation.Answered && showedProgress && !Directory.Exists(fresh) && summary.Text.Contains("共 0 项")
                    && !cleanAll.IsEnabled && progress.Visibility == Visibility.Collapsed && !model.IsBusy
                    && ((TextBlock)dialog.FindName("CleanupMessageText")).Text.Contains("2 KB"),
                    "Confirmed all cleanup removes fresh backups despite invalid retention and resets counts/buttons/progress");
            }
            finally { visibility.RemoveValueChanged(progress, trackProgress); }
            render(dialog, Path.Combine(output, "backup-management-after-all-cleanup.png"), 700, 580);
            Console.WriteLine("PASS: backup settings, size/expiry preview, validation, edit isolation, automatic cleanup/daily timer, all-backup confirmation/cancellation/execution and independent progress.");
        }
        finally { dialog.Close(); owner.Close(); }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class CleanupConfirmation : IDisposable
    {
        private readonly Window owner;
        private readonly bool accept;
        private readonly Action<string> inspect;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        public bool Answered { get; private set; }
        private Exception? failure;

        public CleanupConfirmation(Window owner, bool accept, Action<string> inspect)
        {
            this.owner = owner;
            this.accept = accept;
            this.inspect = inspect;
            // Offscreen WPF rendering does not create a native owner handle by itself.
            new WindowInteropHelper(owner).EnsureHandle();
            timer.Tick += Tick;
            timer.Start();
        }

        private void Tick(object? sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle == 0) return;
            var popup = GetWindow(handle, 6); // GW_ENABLEDPOPUP; only this test dialog's popup
            if (popup == 0 || popup == handle) return;
            var title = new StringBuilder(128);
            GetWindowText(popup, title, title.Capacity);
            if (title.ToString() != "清理全部备份") return;
            var message = new StringBuilder();
            EnumChildWindows(popup, (child, _) =>
            {
                var text = new StringBuilder(1024);
                GetWindowText(child, text, text.Capacity);
                message.AppendLine(text.ToString());
                return true;
            }, 0);
            try { inspect(message.ToString()); }
            catch (Exception ex) { failure = ex; }
            Answered = true;
            timer.Stop();
            // Reject instead of executing if verification of the confirmation failed.
            SendMessage(popup, 0x0111, accept && failure == null ? 6 : 7, 0); // WM_COMMAND / IDYES or IDNO
        }

        public void Dispose()
        {
            timer.Stop(); timer.Tick -= Tick;
            if (failure != null) throw new InvalidOperationException("Cleanup confirmation verification failed", failure);
        }
    }

    private delegate bool EnumWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
