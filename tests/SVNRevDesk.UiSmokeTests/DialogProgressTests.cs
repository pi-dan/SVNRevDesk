using System.IO;
using System.Windows;
using System.Windows.Threading;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class DialogProgressTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var window = new MainWindow(new UserSettings(Path.Combine(output, "dialog-progress-settings")));
        using var model = (MainViewModel)window.DataContext;
        var progress = (FrameworkElement)window.FindName("OperationProgress");
        var executed = false;
        await model.RunAsync("提交修改", async token =>
        {
            var accepted = model.WaitForUserInput(() =>
            {
                var elapsed = model.ProgressElapsedText;
                render(window, Path.Combine(output, "waiting-input-progress-hidden.png"), 1000, 620);
                Check(model.IsBusy && !model.CanOperate && !model.IsOperationProgressVisible
                    && progress.Visibility == Visibility.Collapsed, "Waiting hides progress while retaining the operation lock");
                // A modal dialog pumps the dispatcher; the operation timer must remain stopped throughout.
                PumpDispatcher(TimeSpan.FromMilliseconds(1200));
                Check(model.ProgressElapsedText == elapsed && !executed && progress.Visibility == Visibility.Collapsed,
                    "Dialog waiting time is excluded from elapsed time and starts no operation");
                model.WaitForUserInput(() =>
                {
                    Check(!model.IsOperationProgressVisible && progress.Visibility == Visibility.Collapsed, "Nested prompts keep progress hidden");
                    return true;
                });
                Check(!model.IsOperationProgressVisible, "Closing a nested prompt does not resume the outer prompt's timer");
                return true;
            }, result => !result);
            Check(accepted && model.IsOperationProgressVisible && progress.Visibility == Visibility.Visible,
                "Accepting input restores progress for actual execution");
            await Task.Delay(250, token);
            Check(model.ProgressElapsedText == "已用时 00:00:00", "Elapsed time does not include the modal wait");
            model.BeginProgressPhase("提交修改", "提交已勾选的项目");
            executed = true;
            await Task.Delay(1100, token);
            Check(model.ProgressElapsedText == "已用时 00:00:01" && progress.Visibility == Visibility.Visible,
                "The confirmed commit phase starts fresh timing and displays progress");
        });
        Check(executed && !model.IsBusy && progress.Visibility == Visibility.Collapsed, "Completion hides progress after the confirmed operation");

        foreach (var kind in new[] { "确认", "输入", "选项", "文件夹", "提交" })
        {
            executed = false;
            await model.RunAsync(kind, _ =>
            {
                var accepted = model.WaitForUserInput(() => false, result => !result);
                Check(!accepted && !model.IsOperationProgressVisible && progress.Visibility == Visibility.Collapsed,
                    "Cancelling a prompt does not flash progress again");
                if (accepted) executed = true;
                return Task.CompletedTask;
            });
            Check(!executed && !model.IsBusy && model.StatusMessage == $"已取消{kind}。"
                && progress.Visibility == Visibility.Collapsed, "Cancellation is reported without a false completion message");
        }
        try
        {
            await model.RunAsync("Dialog error", _ =>
            {
                model.WaitForUserInput<bool>(() => throw new IOException("Dialog failure fixture"));
                return Task.CompletedTask;
            });
        }
        catch (IOException) { }
        Check(!model.IsBusy && progress.Visibility == Visibility.Collapsed, "A dialog exception releases the operation and its progress state");
        await model.RunAsync("Next operation", _ =>
        {
            Check(model.IsOperationProgressVisible && progress.Visibility == Visibility.Visible,
                "A cancelled or failed dialog does not suppress the next operation's progress");
            return Task.CompletedTask;
        });
        Console.WriteLine("PASS: pending/nested dialogs hide progress and pause elapsed time, accepted phases resume, cancelled dialogs skip execution/completion, errors release state and subsequent operations recover.");
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
