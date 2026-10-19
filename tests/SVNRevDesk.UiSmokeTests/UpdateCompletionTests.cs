using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class UpdateCompletionTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "update-completion");
        Directory.CreateDirectory(root);
        var repositoryPath = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start)!)
        {
            await admin.WaitForExitAsync();
            Check(admin.ExitCode == 0, "Create update fixture repository");
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var local = Path.Combine(root, "working-copy");
        var peer = Path.Combine(root, "peer");
        var file = Path.Combine(local, "report.txt");
        await operations.CheckoutAsync(url, local, false, CancellationToken.None);
        File.WriteAllText(file, "original");
        await operations.AddAsync([file], CancellationToken.None);
        await operations.CommitAsync([local], "initial", CancellationToken.None);
        await operations.CheckoutAsync(url, peer, false, CancellationToken.None);

        var window = new MainWindow(new UserSettings(Path.Combine(root, "settings")));
        using var model = (MainViewModel)window.DataContext;
        await model.OpenAsync(local, true, CancellationToken.None);
        var index = model.GetSearchIndex();
        await index.RefreshAsync();
        var originalRevision = index.Status.Revision;
        File.WriteAllText(Path.Combine(peer, "report.txt"), "updated on server");
        await operations.CommitAsync([peer], "server update", CancellationToken.None);
        await model.RefreshCurrentDirectoryAsync(CancellationToken.None);
        await UntilAsync(() => !model.IsReading);

        // Hold maintenance to reproduce an index that cannot finish, without a real network delay.
        var gate = (SemaphoreSlim)typeof(RepositorySearchIndex).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(index)!;
        await gate.WaitAsync();
        try
        {
            var selected = model.Entries.Single();
            var update = model.RunAsync("更新所选", token => new ClientActions(window, model).ExecuteAsync("update", [selected], token));
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            render(window, Path.Combine(output, "update-complete-index-background.png"), 1360, 780);
            Check(File.ReadAllText(file) == "updated on server" && model.Entries.Single().State == FileState.Equal,
                "SVN update and the displayed file state complete while index maintenance is blocked");
            Check(!model.IsBusy && model.CanOperate && !model.IsOperationProgressVisible
                && model.StatusMessage == "更新所选完成" && model.IsReading
                && model.ReadStatusMessage.Contains("搜索索引")
                && ((TextBlock)window.FindName("BackgroundReadStatus")).Text == "后台更新搜索索引，可继续操作"
                && ((FrameworkElement)window.FindName("OperationProgress")).Visibility == Visibility.Collapsed,
                "A slow index has an explicit background label and does not retain the operation progress or lock");
            var cachedSearch = await model.SearchAsync("", "report", false,
                new Progress<RepositorySearchProgress>(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Check(cachedSearch.MatchCount == 1, "Cached indexed searches remain available during maintenance");

            await model.RunAsync("后续操作", _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            Check(model.IsReading && model.ReadStatusMessage.Contains("搜索索引") && model.StatusMessage == "后续操作完成",
                "A later explicit operation requeues the index maintenance it interrupted");
            Check(index.Status.HasIndex, "Cancelling background maintenance retains the published index");

            await model.RefreshCurrentDirectoryAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Check(model.IsReading && !model.IsBusy, "Directory refresh also returns before index maintenance");
            model.Cancel();
            await UntilAsync(() => !model.IsReading);
            Check(model.StatusMessage == "后续操作完成", "Cancelling index maintenance does not replace the successful operation status");
        }
        finally
        {
            model.Cancel();
            gate.Release();
        }

        await model.RunAsync("重新核对", model.RefreshCurrentDirectoryAsync);
        await UntilAsync(() => !model.IsReading);
        Check(index.Status.Revision > originalRevision && index.FindEntry("report.txt")!.Value.Entry.State == FileState.Equal
            && model.StatusMessage == "重新核对完成",
            "Background maintenance eventually publishes the current index without replacing the operation completion");
        await IndexSchedulingAsync(model, index, async () =>
        {
            File.WriteAllText(Path.Combine(peer, "report.txt"), "newer revision during index maintenance");
            await operations.CommitAsync([peer], "index request coalescing", CancellationToken.None);
        });
        await IndexResumeAfterSearchDialogAsync(model, index);
        Console.WriteLine("PASS: real SVN update completes with blocked index maintenance, WPF progress hides, cached search stays available, later operations take priority, cancellation preserves cache, and background refresh finishes; search dialog requeues index maintenance.");
    }

    private static async Task IndexSchedulingAsync(MainViewModel model, RepositorySearchIndex index, Func<Task> changeServer)
    {
        var maintenance = (SemaphoreSlim)typeof(RepositorySearchIndex).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(index)!;
        var queue = typeof(MainViewModel).GetMethod("QueueSearchIndexUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reads = (System.Collections.ICollection)typeof(MainViewModel).GetField("backgroundReads", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(model)!;
        await maintenance.WaitAsync();
        try
        {
            await model.RunAsync("仅查看信息", _ => Task.CompletedTask);
            Check(!model.IsReading, "An operation with no interrupted maintenance or refresh must leave the index idle");
            for (var i = 0; i < 4; i++) queue.Invoke(model, [model.Session]);
            Check(reads.Count == 1, "Repeated index update requests must share one background task");
            await changeServer();
            await model.RefreshCurrentDirectoryAsync(CancellationToken.None);
            Check(reads.Count == 1, "A newer revision arriving during maintenance must reuse the background task");
        }
        catch { model.Cancel(); throw; }
        finally { maintenance.Release(); }
        await UntilAsync(() => !model.IsReading);
        Check(index.Status.Revision == model.Snapshot!.Revision
            && index.FindEntry("report.txt")!.Value.Entry.State == FileState.ServerChanged,
            "Coalesced maintenance must publish the newest requested revision and file state");
        // Completion clears the indicator and does not schedule itself again.
        queue.Invoke(model, [model.Session]);
        await UntilAsync(() => !model.IsReading);
        var checkedAt = index.Status.ServerCheckedAt;
        await Task.Delay(200);
        Check(!model.IsReading && index.Status.ServerCheckedAt == checkedAt, "Completed index maintenance stays idle");
        Console.WriteLine("PASS: idle operations do not start index maintenance, repeated refresh requests share one task, and completed maintenance stays idle.");
    }

    // Opening a modal dialog stops background reads, and closing it without a result runs no directory
    // refresh, so RunAsync must requeue index maintenance itself or the background label vanishes for good.
    private static async Task IndexResumeAfterSearchDialogAsync(MainViewModel model, RepositorySearchIndex index)
    {
        var knownRevision = index.Status.Revision;
        var searchShown = false;
        var maintenance = (SemaphoreSlim)typeof(RepositorySearchIndex).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(index)!;
        await maintenance.WaitAsync();
        try
        {
            // Only a genuinely interrupted maintenance task should be resumed after a dialog closes.
            await model.RefreshCurrentDirectoryAsync(CancellationToken.None);
            Check(model.IsReading, "Directory refresh starts the maintenance that the search dialog will interrupt");
            var search = model.RunAsync("跨目录搜索", async token =>
            {
                model.WaitForUserInput(() => { searchShown = true; return false; }, result => !result);
                await Task.CompletedTask;
            });
            await UntilAsync(() => searchShown);
            await search.WaitAsync(TimeSpan.FromSeconds(10));
            Check(model.IsReading && model.ReadStatusMessage.Contains("搜索索引") && !model.IsBusy
                && model.StatusMessage == "已取消跨目录搜索。",
                "Closing the search dialog without a result restores the background index maintenance indicator");
            model.Cancel();
            await UntilAsync(() => !model.IsReading);
        }
        finally { maintenance.Release(); }
        await UntilAsync(() => !model.IsReading);
        Check(index.Status.Revision == knownRevision && index.Status.HasIndex,
            "The requeued maintenance leaves the published index unchanged when cancelled");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Check(condition(), "Index background work did not finish within the test timeout");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
