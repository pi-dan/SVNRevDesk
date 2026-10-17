using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class RevisionPickerTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var owner = new Window { Left = -8000, Top = -8000, Width = 100, Height = 100, ShowActivated = false };
        owner.Show();
        var date = new DateTimeOffset(2026, 10, 17, 10, 30, 0, TimeSpan.FromHours(8));
        var entries = new[] { new HistoryEntry(123, "alice", date, "修复历史查询\n保留完整提交说明"),
            new HistoryEntry(118, "bob", date.AddHours(-1), "更新界面"), new HistoryEntry(3, "carol", date.AddDays(-1), "建立仓库") };
        var request = new TaskCompletionSource<HistoryPage>();
        var calls = 0;
        var picker = NewPicker((start, peg, _) =>
        {
            calls++;
            if (calls == 1) return request.Task;
            Check(start == 117 && peg == 123, "Loading earlier history preserves its cursor and frozen snapshot");
            return Task.FromResult(new HistoryPage([entries[2]], 123, null));
        });
        await Drive(picker, async () =>
        {
            await Until(() => calls == 1);
            var input = Find<ComboBox>(picker, "RevisionInput");
            input.Text = "119";
            request.SetResult(new HistoryPage(entries.Take(2).ToArray(), 123, 117));
            var grid = Find<DataGrid>(picker, "RevisionHistory");
            await Until(() => grid.Items.Count == 2);
            Check(input.Text == "119", "Asynchronous history loading preserves a manually entered revision");
            grid.SelectedItem = entries[0];
            Check(input.Text == "123" && Find<TextBox>(picker, "CommitDetails").Text.Contains("保留完整提交说明"),
                "Choosing a history row fills the numeric revision and full commit details");
            input.SelectedItem = "118";
            Check(input.Text == "118" && Equals(grid.SelectedItem, entries[1]), "The editable revision dropdown synchronizes with history details");
            var search = Find<TextBox>(picker, "HistorySearch");
            foreach (var query in new[] { "alice", "r123", "保留完整", "2026-10-17" })
            {
                search.Text = query;
                Check(grid.Items.Count > 0, "Loaded records can be filtered by " + query);
            }
            search.Text = "carol";
            Check(grid.Items.Count == 0, "Searching loaded records does not imply that unloaded commits were searched");
            Click(picker, "LoadEarlier");
            await Until(() => grid.Items.Count == 1);
            Check(input.Items.Count == 3 && !Find<Button>(picker, "LoadEarlier").IsEnabled, "Earlier history joins the dropdown and ends at the oldest visible commit");
            search.Text = ""; grid.SelectedItem = entries[0];
            render(picker, Path.Combine(output, "revision-picker.png"), 960, 680);
            picker.Width = 700; picker.Height = 640; picker.UpdateLayout();
            render(picker, Path.Combine(output, "revision-picker-narrow.png"), 700, 640);
            Check(grid.ActualHeight >= grid.RowHeight * 2, "The minimum window size leaves room to browse history rows");
            input.Text = "not a revision"; Click(picker, "AcceptRevision");
            Check(picker.IsVisible, "An invalid revision keeps the picker open for correction");
            input.Text = "0"; Click(picker, "AcceptRevision");
        });
        Check(picker.SelectedRevision == 0, "Updating accepts revision zero through the manual entry field");

        calls = 0;
        picker = NewPicker((_, _, _) => ++calls == 1 ? Task.FromException<HistoryPage>(new IOException("test unavailable"))
            : Task.FromResult(new HistoryPage([entries[0]], 123, null)), allowZero: false);
        await Drive(picker, async () =>
        {
            await Until(() => Find<TextBlock>(picker, "HistoryStatus").Text.Contains("读取失败"));
            Check(Find<Button>(picker, "LoadEarlier").IsEnabled, "History failures expose retry while preserving manual entry");
            Click(picker, "LoadEarlier");
            await Until(() => Find<DataGrid>(picker, "RevisionHistory").Items.Count == 1);
            var input = Find<ComboBox>(picker, "RevisionInput");
            input.Text = "0"; Click(picker, "AcceptRevision");
            Check(picker.IsVisible, "Rollback rejects revision zero within the picker");
            input.Text = "456"; Click(picker, "AcceptRevision");
        });
        Check(picker.SelectedRevision == 456, "A valid manual revision need not be present in the loaded history");

        var late = new TaskCompletionSource<HistoryPage>();
        CancellationToken readToken = default;
        picker = NewPicker((_, _, token) => { readToken = token; return late.Task; });
        await Drive(picker, async () =>
        {
            await Until(() => readToken.CanBeCanceled);
            picker.Close();
        });
        Check(readToken.IsCancellationRequested, "Closing the picker cancels its outstanding history read");
        late.SetResult(new HistoryPage([entries[0]], 123, null));
        await Task.Delay(40);
        Check(Find<DataGrid>(picker, "RevisionHistory").Items.Count == 0, "A late history response cannot update a closed picker");

        picker = NewPicker((_, _, _) => Task.FromException<HistoryPage>(new IOException("offline")));
        await Drive(picker, async () =>
        {
            await Until(() => Find<TextBlock>(picker, "HistoryStatus").Text.Contains("读取失败"));
            Find<ComboBox>(picker, "RevisionInput").Text = "42"; Click(picker, "AcceptRevision");
        });
        Check(picker.SelectedRevision == 42, "Manual entry remains usable when the server history is unavailable");
        owner.Close();

        RevisionPickerWindow NewPicker(Func<long?, long?, CancellationToken, Task<HistoryPage>> loader, bool allowZero = true) =>
            new(owner, "更新到指定修订号", "选择或输入目标修订号。目录包含子目录。SVN 会尝试保留本地修改，冲突留待手动处理。", "https://svn.example.com/project/src/history.cs", "100", allowZero, loader, CancellationToken.None)
            { Left = -8000, Top = -8000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
    }

    private static async Task Drive(RevisionPickerWindow picker, Func<Task> action)
    {
        var completion = new TaskCompletionSource();
        _ = picker.Dispatcher.BeginInvoke(async () =>
        {
            try { await action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (picker.IsVisible) picker.Close(); }
        });
        picker.ShowDialog(); await completion.Task;
    }

    private static void Click(Window window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).Single(element => element.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.Elapsed.TotalSeconds > 15) throw new TimeoutException("Revision picker did not settle"); await Task.Delay(20); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); Console.WriteLine("PASS: " + message); }
}
