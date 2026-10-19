using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class CurrentFilesUpdateTests
{
    public static async Task RunAsync(string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "current-files-update");
        Directory.CreateDirectory(root);
        var repositoryPath = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("create"); start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start)!)
        {
            await admin.WaitForExitAsync();
            Check(admin.ExitCode == 0, "Create shallow update fixture");
        }
        using var runner = new SvnRunner();
        var ops = new SvnOperations(runner);
        var token = CancellationToken.None;
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var local = Path.Combine(root, "working-copy");
        var peer = Path.Combine(root, "peer");
        const string folder = "当前 @ 目录";
        var current = Path.Combine(local, folder);
        await ops.CheckoutAsync(url, local, false, token);
        Directory.CreateDirectory(Path.Combine(current, "deep"));
        Directory.CreateDirectory(Path.Combine(current, "deleted-folder"));
        Directory.CreateDirectory(Path.Combine(local, "sibling"));
        foreach (var name in new[] { "report@one.txt", "stable.txt", "local-edit.txt", "deleted.txt", "missing.txt", "conflict.txt", "deep/child.txt", "deleted-folder/child.txt" })
            File.WriteAllText(Path.Combine(current, name), "original\n");
        File.WriteAllText(Path.Combine(local, "sibling", "child.txt"), "original\n");
        await ops.AddAsync([current, Path.Combine(local, "sibling")], token);
        await ops.CommitAsync([local], "initial files", token);
        await ops.UpdateAsync(local, true, token);
        await ops.CheckoutAsync(url, peer, false, token);
        var window = new MainWindow(new UserSettings(Path.Combine(root, "settings")));
        using var model = (MainViewModel)window.DataContext;
        var button = (Button)window.FindName("UpdateCurrentFilesButton");
        render(window, Path.Combine(output, "update-current-files-disabled.png"), 1000, 620);
        Check(!button.IsEnabled && !model.CanUpdateCurrentFiles, "The current-layer update requires an open local session");
        await model.OpenAsync(local, true, token);
        await model.NavigateAsync(folder + "/deep", token);
        await model.NavigateAsync(folder, token);
        var childRevision = await RevisionAsync(Path.Combine(current, "deep", "child.txt"));
        var directoryRevision = await RevisionAsync(Path.Combine(current, "deep"));
        var stableStamp = File.GetLastWriteTimeUtc(Path.Combine(current, "stable.txt"));

        // Commit after browsing so the action must check HEAD rather than trust cached row states.
        var remote = Path.Combine(peer, folder);
        foreach (var name in new[] { "report@one.txt", "conflict.txt", "deep/child.txt" })
            File.WriteAllText(Path.Combine(remote, name), "server change\n");
        File.WriteAllText(Path.Combine(peer, "sibling", "child.txt"), "server change\n");
        File.WriteAllText(Path.Combine(remote, "new.txt"), "new direct file\n");
        File.WriteAllText(Path.Combine(remote, "deep", "new.txt"), "new descendant\n");
        Directory.CreateDirectory(Path.Combine(remote, "new-subdirectory"));
        File.WriteAllText(Path.Combine(remote, "new-subdirectory", "child.txt"), "new directory\n");
        await ops.AddAsync([Path.Combine(remote, "new.txt"), Path.Combine(remote, "deep", "new.txt"), Path.Combine(remote, "new-subdirectory")], token);
        await ops.DeleteAsync([Path.Combine(remote, "deleted.txt"), Path.Combine(remote, "deleted-folder")], token);
        await runner.RunAsync(["propset", "svn:externals", "^/sibling external-dir", SvnRunner.Target(remote)], token);
        await ops.CommitAsync([peer], "changes at several depths", token);
        File.WriteAllText(Path.Combine(current, "local-edit.txt"), "uncommitted local change\n");
        File.WriteAllText(Path.Combine(current, "conflict.txt"), "different local change\n");
        File.Delete(Path.Combine(current, "missing.txt"));
        model.FilterText = "no rows match this filter";
        render(window, Path.Combine(output, "update-current-files-before.png"), 1360, 780);
        Check(button.IsEnabled && model.VisibleEntryCount == 0 && model.SelectionText == "未选择项目",
            "The toolbar action needs no selection and remains enabled with every row filtered out");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.IsBusy && !button.IsEnabled, "The actual toolbar action disables itself while running");
        await UntilAsync(() => !model.IsBusy && model.StatusMessage == "更新当前层文件完成");

        Check(File.ReadAllText(Path.Combine(current, "report@one.txt")) == "server change\n"
            && File.ReadAllText(Path.Combine(current, "new.txt")) == "new direct file\n"
            && File.ReadAllText(Path.Combine(current, "missing.txt")) == "original\n"
            && !File.Exists(Path.Combine(current, "deleted.txt")),
            "Current files are updated, new and missing files downloaded, and remote file deletions applied despite stale and filtered rows");
        Check(File.ReadAllText(Path.Combine(current, "deep", "child.txt")) == "original\n"
            && !File.Exists(Path.Combine(current, "deep", "new.txt"))
            && File.Exists(Path.Combine(current, "deleted-folder", "child.txt"))
            && !Directory.Exists(Path.Combine(current, "new-subdirectory"))
            && !Directory.Exists(Path.Combine(current, "external-dir"))
            && File.ReadAllText(Path.Combine(local, "sibling", "child.txt")) == "original\n"
            && await RevisionAsync(Path.Combine(current, "deep", "child.txt")) == childRevision
            && await RevisionAsync(Path.Combine(current, "deep")) == directoryRevision,
            "Subdirectory content, revisions, additions, deletions, externals and sibling files are not updated");
        Check(File.ReadAllText(Path.Combine(current, "local-edit.txt")) == "uncommitted local change\n"
            && model.Entries.Single(entry => entry.Name == "local-edit.txt").State == FileState.LocalModified
            && model.Entries.Single(entry => entry.Name == "conflict.txt").State == FileState.Conflict
            && File.ReadAllText(Path.Combine(current, "conflict.txt.mine")) == "different local change\n"
            && File.GetLastWriteTimeUtc(Path.Combine(current, "stable.txt")) == stableStamp,
            "Local edits survive, conflicts are postponed with the original local file retained, and unchanged files are not rewritten");
        Check(model.CurrentRelative == folder && model.FilterText == "no rows match this filter"
            && model.Entries.Single(entry => entry.Name == "report@one.txt").State == FileState.Equal,
            "Completion refreshes the current layer without changing navigation or filters");
        model.FilterText = "";
        render(window, Path.Combine(output, "update-current-files-after.png"), 1360, 780);
        render(window, Path.Combine(output, "update-current-files-narrow.png"), 1000, 620);

        var sparse = Path.Combine(root, "sparse");
        await ops.CheckoutAsync(url + "/" + Uri.EscapeDataString(folder), sparse, true, token);
        var sparseWindow = new MainWindow(new UserSettings(Path.Combine(root, "sparse-settings")));
        using var sparseModel = (MainViewModel)sparseWindow.DataContext;
        await sparseModel.OpenAsync(sparse, true, token);
        await sparseModel.RunAsync("更新当前层文件", readToken => new ClientActions(sparseWindow, sparseModel)
            .ExecuteAsync("update-directory-files", [], readToken));
        var sparseInfo = XDocument.Parse(await runner.RunAsync(["info", "--xml", SvnRunner.Target(sparse)], token));
        Check(File.ReadAllText(Path.Combine(sparse, "report@one.txt")) == "server change\n"
            && !Directory.Exists(Path.Combine(sparse, "deep")) && !Directory.Exists(Path.Combine(sparse, "new-subdirectory"))
            && !Directory.Exists(Path.Combine(sparse, "external-dir"))
            && sparseInfo.Descendants("depth").Single().Value == "files",
            "A root-level sparse update downloads immediate files without creating subdirectories and records file depth");
        await model.OpenAsync(url, false, token);
        Check(!button.IsEnabled && !model.CanUpdateCurrentFiles
            && !BrowserActionAvailability.CanExecute("update-directory-files", model.Session, []),
            "Online-only browsing disables the current-layer update");
        Console.WriteLine("PASS: current-layer toolbar update, filtered/unselected and stale rows, new/missing/deleted files, unchanged child revisions/content, local edits/conflicts, sparse depth, externals and online availability.");

        async Task<long> RevisionAsync(string path) => SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", SvnRunner.Target(path)], token)).Single().Revision;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        Check(condition(), "The toolbar update did not complete within the test timeout");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
