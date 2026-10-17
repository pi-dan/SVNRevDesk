using System.Globalization;
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
using Forms = System.Windows.Forms;

namespace SVNRevDesk.UiSmokeTests;

internal static class CloseBehaviorTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "close-behavior");
        var settings = new UserSettings(root) { AutoRefresh = false, AutoBackupCleanup = false };
        Check(settings.CloseBehavior == WindowCloseBehavior.Ask, "Existing users default to asking before closing");
        settings.CloseBehavior = WindowCloseBehavior.MinimizeToTray;
        settings.Save();
        Check(UserSettings.Load(root).CloseBehavior == WindowCloseBehavior.MinimizeToTray,
            "Tray preference persists alongside existing settings");
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"CloseBehavior\":99,\"RefreshSeconds\":300}");
        var invalid = UserSettings.Load(root);
        Check(invalid.CloseBehavior == WindowCloseBehavior.Ask && invalid.RefreshSeconds == 300,
            "An unsupported close preference falls back to asking without discarding other settings");
        settings.CloseBehavior = WindowCloseBehavior.Ask;

        var choices = new CloseWindowDialog(null);
        Check(choices.SelectedBehavior == WindowCloseBehavior.MinimizeToTray && !choices.RememberChoice,
            "Closing defaults to a nonremembered tray choice");
        render(choices, Path.Combine(output, "close-window-dialog.png"), 454, 320);
        choices.Close();
        var options = new SettingsWindow(null, settings, new BackupManager(root));
        var closeBox = (ComboBox)options.FindName("CloseBehaviorBox");
        Check(Equals(closeBox.SelectedValue, WindowCloseBehavior.Ask), "Settings show the current close preference");
        closeBox.SelectedValue = WindowCloseBehavior.Exit;
        Check(options.CloseBehavior == WindowCloseBehavior.Exit && settings.CloseBehavior == WindowCloseBehavior.Ask,
            "Editing close settings does not apply them before saving");
        render(options, Path.Combine(output, "settings-close-behavior.png"), 700, 580);
        options.Close();

        var window = CreateWindow(settings);
        var model = (MainViewModel)window.DataContext;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        using var dialogs = new DialogDriver(window);
        try
        {
            window.Show();
            await Task.Yield();
            var tray = GetField(window, "trayIcon")!;
            Check(tray != null && window.ShowInTaskbar && window.Icon != null,
                "Startup creates a tray icon and keeps the main window's taskbar icon");
            var notification = (Forms.NotifyIcon)GetField(tray!, "notification")!;
            Check(notification.Visible, "The tray icon is visible as soon as the window opens");
            var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Check(DateTime.TryParseExact(version, "'V'yyMMdd.HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "The assembly version records the build time to the second");
            Check(((TextBlock)window.FindName("VersionText")).Text == $"版本 {version}"
                && window.Title == "SVNRevDesk" && notification.Text.Contains(version, StringComparison.Ordinal),
                "The header and tray tooltip show the embedded version while the window title shows only the app name");
            window.Width = 1360;
            window.Height = 780;
            window.UpdateLayout();
            render(window, Path.Combine(output, "main-window-version.png"), 1360, 780);
            window.Width = 1000;
            window.Height = 620;
            window.UpdateLayout();
            render(window, Path.Combine(output, "main-window-version-narrow.png"), 1000, 620);
            dialogs.Choose = dialog => dialog.DialogResult = false;
            window.Close();
            Check(!closed && window.IsVisible && settings.CloseBehavior == WindowCloseBehavior.Ask,
                "Cancelling the close dialog leaves the window and preference intact");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = model.RunAsync("关闭行为测试", token => completion.Task.WaitAsync(token));
            dialogs.Choose = dialog => ((Button)dialog.FindName("ConfirmButton"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Close();
            Check(!window.IsVisible && settings.CloseBehavior == WindowCloseBehavior.Ask
                && UserSettings.Load(root).CloseBehavior == WindowCloseBehavior.Ask,
                "Minimizing without Remember keeps the next close in Ask mode");
            typeof(Forms.NotifyIcon).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(notification, [new Forms.MouseEventArgs(Forms.MouseButtons.Left, 2, 0, 0, 0)]);
            await Task.Yield();
            Check(window.IsVisible && notification.Visible && model.IsBusy,
                "Left double-clicking the tray icon restores the window while its operation continues");
            dialogs.Choose = dialog =>
            {
                ((RadioButton)dialog.FindName("ExitOption")).IsChecked = true;
                ((CheckBox)dialog.FindName("RememberChoiceBox")).IsChecked = true;
                ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            dialogs.RefuseBusyExit = true;
            window.Close();
            Check(window.IsVisible && settings.CloseBehavior == WindowCloseBehavior.Ask
                && UserSettings.Load(root).CloseBehavior == WindowCloseBehavior.Ask && !operation.IsCompleted,
                "Refusing a busy exit does not cancel the operation or remember an unaccepted exit choice");
            dialogs.RefuseBusyExit = false;
            dialogs.Choose = dialog =>
            {
                ((CheckBox)dialog.FindName("RememberChoiceBox")).IsChecked = true;
                ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            model.FilterText = "保留筛选";
            window.Close();
            Check(!closed && !window.IsVisible && model.IsBusy && !operation.IsCompleted,
                "Actual window Close hides to the tray without cancelling an ongoing operation");
            Check(settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray
                && UserSettings.Load(root).CloseBehavior == WindowCloseBehavior.MinimizeToTray,
                "Remembering a confirmed tray choice persists to the executable-relative settings");
            Check(notification.Visible, "A hidden window has a live notification-area icon");
            notification.ContextMenuStrip!.Items[0].PerformClick();
            await Task.Yield();
            Check(window.IsVisible && notification.Visible && model.FilterText == "保留筛选"
                && model.IsBusy && !operation.IsCompleted,
                "The tray Open command restores the same window without reloading or cancelling its operation");

            dialogs.Choose = null;
            window.Close();
            Check(!window.IsVisible && notification.Visible && !closed,
                "A remembered tray choice skips the dialog on subsequent closes");
            dialogs.RefuseBusyExit = true;
            notification.ContextMenuStrip.Items[2].PerformClick();
            Check(dialogs.BusyExitPromptCount == 2 && !closed && notification.Visible
                && model.IsBusy && !operation.IsCompleted,
                "Explicit tray Exit still confirms a busy operation; refusing leaves the hidden app running");
            dialogs.RefuseBusyExit = false;
            completion.SetResult();
            await operation;
            notification.ContextMenuStrip.Items[0].PerformClick();
            await Task.Yield();
            Check(window.IsVisible && notification.Visible && !model.IsBusy,
                "Restoring after an operation completes preserves a usable window");
            window.Close();
            notification.ContextMenuStrip.Items[2].PerformClick();
            Check(closed && !notification.Visible && GetField(window, "trayIcon") == null,
                "Tray Exit bypasses a remembered minimize choice and removes the tray icon");
            dialogs.Verify();
        }
        finally
        {
            // Cleanup must never leave an invisible test process or operation running.
            model.Cancel();
            if (!closed) { Invoke(window, "PrepareForSystemShutdown"); window.Close(); }
            model.Dispose();
        }

        var exitSettings = new UserSettings(Path.Combine(root, "exit")) { AutoBackupCleanup = false };
        var exiting = CreateWindow(exitSettings);
        var exited = false;
        exiting.Closed += (_, _) => exited = true;
        using (var driver = new DialogDriver(exiting))
        {
            exiting.Show();
            driver.Choose = dialog =>
            {
                ((RadioButton)dialog.FindName("ExitOption")).IsChecked = true;
                ((CheckBox)dialog.FindName("RememberChoiceBox")).IsChecked = true;
                ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            exiting.Close();
            Check(exited && exitSettings.CloseBehavior == WindowCloseBehavior.Exit
                && UserSettings.Load(exitSettings.StorageDirectory).CloseBehavior == WindowCloseBehavior.Exit,
                "Choosing Exit closes the window and remembers the confirmed preference");
            driver.Verify();
        }

        var rememberedExit = CreateWindow(UserSettings.Load(exitSettings.StorageDirectory));
        var rememberedExited = false;
        rememberedExit.Closed += (_, _) => rememberedExited = true;
        using (var driver = new DialogDriver(rememberedExit))
        {
            rememberedExit.Show();
            rememberedExit.Close();
            Check(rememberedExited && driver.ChoicePromptCount == 0, "A saved Exit preference survives restarting");
            driver.Verify();
        }

        var shutdown = CreateWindow(new UserSettings(Path.Combine(root, "shutdown")) { AutoBackupCleanup = false });
        var shutDown = false;
        shutdown.Closed += (_, _) => shutDown = true;
        using (var driver = new DialogDriver(shutdown))
        {
            shutdown.Show();
            Invoke(shutdown, "PrepareForSystemShutdown");
            shutdown.Close();
            Check(shutDown && driver.ChoicePromptCount == 0, "Windows shutdown bypasses the close chooser");
            driver.Verify();
        }
        Console.WriteLine("PASS: close choices/cancellation, portable preference memory/settings, real hide/restore, continuing operations, tray exit/cleanup, and Windows shutdown.");
    }

    private static MainWindow CreateWindow(UserSettings settings)
    {
        var window = new MainWindow(settings) { Left = -8000, Top = -8000, ShowActivated = false };
        // This suite exercises window lifetime only; startup arguments belong to the SVN fixture suite.
        typeof(MainWindow).GetField("windowLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        return window;
    }

    private static object? GetField(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Invoke(object target, string name) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class DialogDriver : IDisposable
    {
        private readonly MainWindow owner;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly List<string> unexpectedDialogs = [];
        public Action<CloseWindowDialog>? Choose { get; set; }
        public bool RefuseBusyExit { get; set; }
        public int ChoicePromptCount { get; private set; }
        public int BusyExitPromptCount { get; private set; }

        public DialogDriver(MainWindow owner)
        {
            this.owner = owner;
            timer.Tick += Tick;
            timer.Start();
        }

        private void Tick(object? sender, EventArgs e)
        {
            var choice = Application.Current.Windows.OfType<CloseWindowDialog>()
                .FirstOrDefault(dialog => dialog.IsVisible && ReferenceEquals(dialog.Owner, owner));
            if (choice != null)
            {
                ChoicePromptCount++;
                if (Choose != null) Choose(choice);
                else { unexpectedDialogs.Add("Unexpected close chooser"); choice.DialogResult = false; }
                return;
            }
            // Only inspect this test window's enabled popup, never another application's windows.
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle == 0) return;
            var popup = GetWindow(handle, 6); // GW_ENABLEDPOPUP
            if (popup == 0 || popup == handle) return;
            var className = new StringBuilder(64);
            GetClassName(popup, className, className.Capacity);
            if (className.ToString() != "#32770") return;
            var title = new StringBuilder(128);
            GetWindowText(popup, title, title.Capacity);
            if (title.ToString() == "退出软件" && RefuseBusyExit)
            {
                BusyExitPromptCount++;
                SendMessage(popup, 0x0111, 7, 0); // WM_COMMAND / IDNO
            }
            else
            {
                unexpectedDialogs.Add(title.ToString());
                SendMessage(popup, 0x0111, 1, 0); // WM_COMMAND / IDOK: release an unexpected error dialog
            }
        }

        public void Verify() => Check(unexpectedDialogs.Count == 0,
            "No unexpected closing errors: " + string.Join(", ", unexpectedDialogs));
        public void Dispose() { timer.Stop(); timer.Tick -= Tick; }
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
}
