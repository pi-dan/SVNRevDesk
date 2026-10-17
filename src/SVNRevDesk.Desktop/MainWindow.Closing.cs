using System.ComponentModel;
using System.IO;
using System.Windows;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop;

public partial class MainWindow
{
    private SystemTrayIcon? trayIcon;
    private WindowState stateBeforeTray = WindowState.Normal;
    private bool exitRequested;
    private bool systemShutdown;
    private bool decidingClose;

    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();

    private void RequestExit()
    {
        if (decidingClose) return;
        exitRequested = true;
        try { Close(); }
        finally { exitRequested = false; }
    }

    internal void PrepareForSystemShutdown() => systemShutdown = true;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (decidingClose) { e.Cancel = true; return; }
        decidingClose = true;
        try
        {
            var behavior = exitRequested || systemShutdown ? WindowCloseBehavior.Exit : model.Settings.CloseBehavior;
            var remember = false;
            if (behavior == WindowCloseBehavior.Ask)
            {
                var dialog = new CloseWindowDialog(this);
                if (dialog.ShowDialog() != true) { e.Cancel = true; return; }
                behavior = dialog.SelectedBehavior;
                remember = dialog.RememberChoice;
            }

            if (behavior == WindowCloseBehavior.MinimizeToTray)
            {
                e.Cancel = true;
                if (!TryMinimizeToTray()) return;
                if (remember) model.Settings.CloseBehavior = behavior;
                SaveSessionBeforeClose();
                return;
            }

            if (model.IsBusy && !systemShutdown
                && MessageBox.Show(this, "SVN 操作仍在执行。退出会取消当前命令，已完成部分不会回滚。是否退出？",
                    "退出软件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { e.Cancel = true; return; }

            if (remember) model.Settings.CloseBehavior = behavior;
            SaveSessionBeforeClose();
            foreach (var tab in repositoryTabs) tab.Model.Cancel();
            refreshTimer.Stop();
            localWatcher?.Dispose(); localWatcher = null;
            backupCleanupTimer.Stop();
            backupLifetime.Cancel();
            trayIcon?.Dispose(); trayIcon = null;
            // A cancelled SVN operation may still be unwinding when the process exits.
            DetachSessionEvents();
            foreach (var tab in repositoryTabs.Where(tab => !tab.Model.IsBusy)) tab.Dispose();
        }
        finally { decidingClose = false; }
    }

    private void ShowTrayIcon()
    {
        trayIcon ??= new SystemTrayIcon(RestoreFromTray, RequestExit);
        trayIcon.Show();
    }

    private bool TryMinimizeToTray()
    {
        try
        {
            ShowTrayIcon();
            stateBeforeTray = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            Hide();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or ArgumentException)
        {
            MessageBox.Show(this, $"无法最小化到系统托盘：{ex.Message}", "关闭软件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
            WindowState = stateBeforeTray;
        }
        else if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SaveSessionBeforeClose()
    {
        try { SaveWorkspace(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (systemShutdown) model.AddLog(ex.Message);
            else MessageBox.Show(this, ex.Message, "保存配置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
