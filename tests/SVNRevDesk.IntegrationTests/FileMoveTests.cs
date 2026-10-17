using System.Diagnostics;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class FileMoveTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var repository = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repository);
        using (var process = Process.Start(start)!)
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var local = Path.Combine(root, "中文 @ 工作副本");
        var source = Path.Combine(local, "源目录");
        var target = Path.Combine(local, "目标目录");
        var outside = Path.Combine(root, "资源管理器来源");
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var token = CancellationToken.None;
        await operations.CheckoutAsync(url, local, false, token);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(source, "报告 @ 空格.txt");
        File.WriteAllText(file, "original");
        var folder = Path.Combine(source, "受控目录");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "child.txt"), "child original");
        await operations.AddAsync([source, target], token);
        await operations.CommitAsync([local], "Move fixture", token);
        File.AppendAllText(file, " with local edits");
        File.WriteAllText(Path.Combine(folder, "unversioned.txt"), "retain me too");
        var newFile = Path.Combine(source, "本地新增.txt");
        File.WriteAllText(newFile, "new local");
        var added = Path.Combine(source, "added.txt");
        File.WriteAllText(added, "added but uncommitted");
        await operations.AddAsync([added], token);

        var plan = await operations.PrepareFileMoveAsync([file, folder, Path.Combine(folder, "child.txt"), newFile, added], target, local, token);
        check(plan.Items.Count == 4 && plan.Items.Count(item => item.IsVersioned) == 3 && File.Exists(file)
            && !Directory.Exists(Path.Combine(target, "受控目录")), "Move preparation deduplicates descendants, classifies current SVN state and changes no files");
        check(await operations.MoveFilesAsync(plan, false, token) == 4, "A mixed batch moves modified, versioned, added and unversioned items");
        var destination = Path.Combine(target, Path.GetFileName(file));
        check(!File.Exists(file) && File.ReadAllText(destination) == "original with local edits", "SVN move preserves local edits and Unicode/space-before-@ names in both filenames and ancestors");
        var statuses = await operations.ReadLocalDirectoryStatusAsync(local, local, token, true);
        check(statuses.Single(status => status.Path == file).Item == "deleted"
            && statuses.Single(status => status.Path == destination).Item == "added"
            && (await runner.RunAsync(["info", "--xml", SvnRunner.Target(destination)], token)).Contains("<copy-from-url>", StringComparison.Ordinal),
            "Versioned moves schedule source deletion and destination addition with copy history");
        var paired = await operations.ReadCommitChangesAsync([destination], local, token);
        check(paired.Count == 2 && paired.All(change => change.RequiredMoves.Count == 1)
            && paired.Any(change => change.Path == file) && paired.Any(change => change.Path == destination),
            "Opening commit from one moved file includes only its source/destination pair and records both dependencies");
        await RejectAsync(() => operations.CommitSelectedAsync(paired.Where(change => change.Path == destination).ToArray(), "incomplete move", local, token));
        check(File.Exists(destination), "A half-selected move is rejected before committing either side");
        check(File.ReadAllText(Path.Combine(target, "受控目录", "unversioned.txt")) == "retain me too"
            && statuses.Single(status => status.Path == Path.Combine(target, "本地新增.txt")).Item == "unversioned"
            && !statuses.Any(status => status.Path == added) && statuses.Single(status => status.Path == Path.Combine(target, "added.txt")).Item == "added",
            "Directory moves preserve unversioned children, ordinary new files remain unversioned and scheduled additions move correctly");
        check(await runner.RunAsync(["cat", SvnRunner.Target(url + "/源目录/报告%20%40%20空格.txt")], token) == "original",
            "Moving locally does not change or commit server content");

        var plainFolder = Path.Combine(outside, "外部目录");
        Directory.CreateDirectory(Path.Combine(plainFolder, "空目录"));
        File.WriteAllText(Path.Combine(plainFolder, "external.txt"), "external data");
        plan = await operations.PrepareFileMoveAsync([plainFolder], target, local, token);
        await operations.MoveFilesAsync(plan, false, token);
        check(!Directory.Exists(plainFolder) && File.ReadAllText(Path.Combine(target, "外部目录", "external.txt")) == "external data"
            && Directory.Exists(Path.Combine(target, "外部目录", "空目录")), "Explorer-origin ordinary directories move with contents and empty directories");

        var collision = Path.Combine(outside, "本地新增.txt");
        var free = Path.Combine(outside, "free.txt");
        File.WriteAllText(collision, "do not overwrite");
        File.WriteAllText(free, "free contents");
        plan = await operations.PrepareFileMoveAsync([collision, free], target, local, token);
        check(plan.Conflicts.Count == 1, "Planning identifies same-name conflicts before writing");
        await RejectAsync(() => operations.MoveFilesAsync(plan, false, token));
        check(File.Exists(collision) && File.Exists(free), "Unconfirmed conflicts stop the whole batch before any source moves");
        await operations.MoveFilesAsync(plan, true, token);
        check(File.ReadAllText(collision) == "do not overwrite" && File.ReadAllText(Path.Combine(target, "本地新增.txt")) == "new local"
            && !File.Exists(free) && File.Exists(Path.Combine(target, "free.txt")), "Skipping conflicts retains both same-name originals and moves only free targets");
        plan = await operations.PrepareFileMoveAsync([destination], target, local, token);
        check(plan.Items.Single().IsSameLocation && await operations.MoveFilesAsync(plan, false, token) == 0
            && !Directory.EnumerateFiles(target).Any(path => Path.GetFileName(path).Contains("副本", StringComparison.Ordinal)),
            "Pasting a cut in its original directory is a no-op rather than making a copy");

        var newFolder = Path.Combine(source, "新建目录");
        Directory.CreateDirectory(newFolder);
        await RejectAsync(() => operations.PrepareFileMoveAsync([newFolder], newFolder, local, token));
        await RejectAsync(() => operations.PrepareFileMoveAsync([local], target, local, token));
        await RejectAsync(() => operations.PrepareFileMoveAsync([newFolder], outside, local, token));
        await RejectAsync(() => operations.PrepareFileMoveAsync([Path.Combine(local, ".svn", "wc.db")], target, local, token));
        check(Directory.Exists(newFolder) && File.Exists(Path.Combine(local, ".svn", "wc.db")), "Self/descendant, root, out-of-scope destination and SVN metadata moves are rejected");
        await operations.CheckoutAsync(url, Path.Combine(newFolder, "nested"), false, token);
        await RejectAsync(() => operations.PrepareFileMoveAsync([newFolder], target, local, token));
        await RejectAsync(() => operations.PrepareFileMoveAsync([Path.Combine(newFolder, "nested", "源目录", "报告 @ 空格.txt")], target, local, token));
        var peer = Path.Combine(root, "other-working-copy");
        await operations.CheckoutAsync(url, peer, false, token);
        await RejectAsync(() => operations.PrepareFileMoveAsync([Path.Combine(peer, "源目录", "报告 @ 空格.txt")], target, local, token));
        check(File.Exists(Path.Combine(peer, "源目录", "报告 @ 空格.txt")), "Nested and other working copies are protected from filesystem moves");

        var late = Path.Combine(outside, "late.txt");
        File.WriteAllText(late, "keep source");
        plan = await operations.PrepareFileMoveAsync([late], target, local, token);
        File.WriteAllText(Path.Combine(target, "late.txt"), "late destination");
        await RejectAsync(() => operations.MoveFilesAsync(plan, false, token));
        check(File.ReadAllText(late) == "keep source" && File.ReadAllText(Path.Combine(target, "late.txt")) == "late destination", "A destination created after planning is never overwritten");
        var cancelled = Path.Combine(outside, "cancelled.txt");
        File.WriteAllText(cancelled, "cancel safely");
        plan = await operations.PrepareFileMoveAsync([cancelled], target, local, token);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RejectAsync(() => operations.MoveFilesAsync(plan, false, cancellation.Token));
        check(File.Exists(cancelled) && !File.Exists(Path.Combine(target, "cancelled.txt")), "Cancellation before execution preserves source and destination");
        var renamed = Path.Combine(target, "renamed.txt");
        plan = await operations.PrepareFileRenameAsync(Path.Combine(target, "本地新增.txt"), renamed, local, token);
        await operations.MoveFilesAsync(plan, false, token);
        check(File.ReadAllText(renamed) == "new local" && !File.Exists(Path.Combine(target, "本地新增.txt")), "The rename workflow also supports unversioned files without SVN errors");

        var first = Path.Combine(outside, "batch-first.txt");
        var second = Path.Combine(outside, "batch-second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        plan = await operations.PrepareFileMoveAsync([first, second], target, local, token);
        using var partialCancellation = new CancellationTokenSource();
        await RejectAsync(() => operations.MoveFilesAsync(plan, false, partialCancellation.Token,
            new InlineProgress(progress => { if (progress.CompletedItems == 1) partialCancellation.Cancel(); })));
        check(!File.Exists(first) && File.Exists(Path.Combine(target, "batch-first.txt")) && File.Exists(second)
            && !File.Exists(Path.Combine(target, "batch-second.txt")), "Cancelling a batch retains completed destinations and untouched remaining sources");

        var movedDirectory = Path.Combine(target, "受控目录");
        var moveChanges = await operations.ReadCommitChangesAsync([destination, movedDirectory], local, token);
        await operations.CommitSelectedAsync(moveChanges.Where(change => change.CanCommit && !change.NeedsAddition).ToArray(), "Commit SVN moves", local, token);
        var history = await operations.LogAsync(destination, token);
        check(history.Count >= 2 && history.Any(entry => entry.Message == "Move fixture"), "Committing a move retains the original file's history");
        await FileMoveOverwriteTests.RunAsync(Path.Combine(root, "overwrite"), check);
    }

    private sealed class InlineProgress(Action<FileMoveProgress> report) : IProgress<FileMoveProgress>
    {
        public void Report(FileMoveProgress value) => report(value);
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
        throw new InvalidOperationException("An unsafe move was accepted.");
    }
}
