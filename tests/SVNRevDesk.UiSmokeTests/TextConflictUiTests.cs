using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class TextConflictUiTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var repoPath = Path.Combine(output, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = output };
        start.ArgumentList.Add("create"); start.ArgumentList.Add("repository");
        using (var admin = Process.Start(start)!)
        { var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync(); if (admin.ExitCode != 0) throw new IOException(await error); }
        var url = new Uri(repoPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(output, "Temp"), Path.Combine(output, "Backups"));
        var wc = Path.Combine(output, "working-copy"); var peer = Path.Combine(output, "peer");
        await operations.CheckoutAsync(url, wc, false, CancellationToken.None);
        foreach (var name in new[] { "merge.txt", "cancel.txt" }) File.WriteAllText(Path.Combine(wc, name), "original\n");
        await operations.AddAsync([Path.Combine(wc, "merge.txt"), Path.Combine(wc, "cancel.txt")], CancellationToken.None);
        await operations.CommitAsync([wc], "initial", CancellationToken.None);
        await operations.CheckoutAsync(url, peer, false, CancellationToken.None);
        foreach (var name in new[] { "merge.txt", "cancel.txt" })
        { File.WriteAllText(Path.Combine(wc, name), "local\n"); File.WriteAllText(Path.Combine(peer, name), "incoming\n"); }
        await operations.CommitAsync([peer], "incoming", CancellationToken.None);
        await operations.UpdateAsync(wc, true, CancellationToken.None);
        var owner = new MainWindow(new UserSettings(Path.Combine(output, "settings")) { CloseBehavior = WindowCloseBehavior.Exit });
        var model = (MainViewModel)owner.DataContext;
        await model.RunAsync("Open conflict fixture", token => model.OpenAsync(wc, true, token));
        typeof(MainWindow).GetField("windowLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, true);
        owner.Left = -8000; owner.Top = -8000; owner.ShowActivated = false; owner.Show();
        var session = await operations.PrepareTextMergeAsync(Path.Combine(wc, "merge.txt"), wc, CancellationToken.None);
        var launchCount = 0;
        var window = new TextConflictWindow(owner, session, async (draft, token) =>
        {
            launchCount++; await Task.Delay(20, token);
            File.WriteAllText(draft.ResultFile, launchCount == 1 ? "<<<<<<< unresolved\n" : "local + incoming\n");
        }, operations.ApplyTextMergeAsync, CancellationToken.None);
        await Drive(window, async () =>
        {
            var accept = Find<Button>(window, "ApplyTextMerge"); var reviewed = Find<CheckBox>(window, "ReviewedTextMerge");
            Check(!accept.IsEnabled, "Text resolution requires a saved draft and explicit review");
            Click(window, "LaunchTextMerge"); await Until(() => Find<Button>(window, "LaunchTextMerge").IsEnabled);
            Check(!accept.IsEnabled && reviewed.IsChecked != true && window.IsVisible, "Closing the merge editor alone never resolves the conflict");
            reviewed.IsChecked = true; Click(window, "ApplyTextMerge");
            await Until(() => Find<TextBlock>(window, "TextMergeStatus").Text.Contains("冲突标记"));
            Check(window.IsVisible && !window.Applied && File.ReadAllText(session.Path).Contains("<<<<<<<"), "An invalid saved merge keeps the dialog open and the working file intact");
            Click(window, "LaunchTextMerge"); await Until(() => Find<Button>(window, "LaunchTextMerge").IsEnabled);
            Check(reviewed.IsChecked != true && !accept.IsEnabled, "Editing a draft again resets the previous resolution confirmation");
            reviewed.IsChecked = true;
            await Task.Delay(40); render(window, Path.Combine(output, "text-conflict.png"), 760, 540);
            Click(window, "ApplyTextMerge"); await Until(() => !window.IsVisible);
        });
        Check(window.Applied && File.ReadAllText(session.Path) == "local + incoming\n", "Confirming the saved text merge writes the result and closes the actual WPF dialog");

        session = await operations.PrepareTextMergeAsync(Path.Combine(wc, "cancel.txt"), wc, CancellationToken.None);
        var original = File.ReadAllBytes(session.Path); CancellationToken editorToken = default;
        window = new TextConflictWindow(owner, session, async (_, token) => { editorToken = token; await Task.Delay(30000, token); }, operations.ApplyTextMergeAsync, CancellationToken.None);
        await Drive(window, async () =>
        {
            Click(window, "LaunchTextMerge"); await Until(() => editorToken.CanBeCanceled);
            Check(!Find<Button>(window, "LaunchTextMerge").IsEnabled, "A pending merge editor cannot be launched twice");
            window.Close();
        });
        Check(editorToken.IsCancellationRequested && !window.Applied && File.ReadAllBytes(session.Path).SequenceEqual(original), "Closing during editing cancels the wait without writing or resolving the working file");

        var driven = new TaskCompletionSource();
        _ = owner.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Until(() => Application.Current.Windows.OfType<ChoiceDialog>().Any());
                var choice = Application.Current.Windows.OfType<ChoiceDialog>().Single();
                Descendants<Button>(choice).Single(button => Equals(button.Content, "用 WinMerge 合并文本…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Application.Current.Windows.OfType<TextConflictWindow>().Any());
                var mergeWindow = Application.Current.Windows.OfType<TextConflictWindow>().Single();
                Check(model.IsBusy && !model.IsOperationProgressVisible && Find<Button>(mergeWindow, "LaunchTextMerge").IsEnabled,
                    "The real conflict action opens the text workflow under the operation lock without showing false progress");
                mergeWindow.Close(); driven.SetResult();
            }
            catch (Exception ex) { driven.SetException(ex); }
        });
        await model.RunAsync("Resolve text conflict", token => new ClientActions(owner, model).ExecuteAsync("resolve", [model.Entries.Single(entry => entry.Name == "cancel.txt")], token));
        await driven.Task;
        Check(!model.IsBusy && model.Entries.Single(entry => entry.Name == "cancel.txt").State == SVNRevDesk.Core.Models.FileState.Conflict,
            "Cancelling the real text workflow releases the operation lock and preserves the conflict");
        model.Dispose(); owner.Close();
    }

    private static async Task Drive(Window window, Func<Task> action)
    {
        window.Left = -8000; window.Top = -8000; window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowActivated = false;
        var completion = new TaskCompletionSource();
        _ = window.Dispatcher.BeginInvoke(async () =>
        { try { await action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } finally { if (window.IsVisible) window.Close(); } });
        window.ShowDialog(); await completion.Task;
    }
    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(element => element.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static async Task Until(Func<bool> condition)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Text merge UI did not settle"); await Task.Delay(20); } }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
