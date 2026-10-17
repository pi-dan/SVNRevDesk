using SVNRevDesk.Core.Services;

internal static class FileTransferTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var source = Path.Combine(root, "资源管理器来源");
        var local = Path.Combine(root, "中文 @ 工作副本");
        var backups = Path.Combine(root, "Backups");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(local, ".svn"));
        File.WriteAllText(Path.Combine(local, ".svn", "wc.db"), "metadata");
        var file = Path.Combine(source, "报告 @ 空格.txt");
        File.WriteAllText(file, "new contents");
        var folder = Path.Combine(source, "文件夹");
        Directory.CreateDirectory(Path.Combine(folder, "子目录"));
        Directory.CreateDirectory(Path.Combine(folder, ".svn"));
        File.WriteAllText(Path.Combine(folder, ".svn", "wc.db"), "source metadata");
        File.WriteAllText(Path.Combine(folder, "子目录", "文件.bin"), "binary");
        Directory.CreateDirectory(Path.Combine(folder, "空目录"));
        var linkFile = Path.Combine(source, "应用快捷方式.lnk");
        File.WriteAllBytes(linkFile, [1, 2, 3, 4]);
        var plan = LocalFileTransfer.Prepare([file, folder, linkFile], local, local);
        check(plan.TopLevelPaths.Count == 3 && plan.Conflicts.Count == 0, "Plan supports multi-selection and Unicode/@ names");
        await LocalFileTransfer.CopyAsync(plan, false, backups, CancellationToken.None);
        var destination = Path.Combine(local, Path.GetFileName(file));
        check(File.ReadAllText(destination) == "new contents" && File.Exists(file), "Paste copies files and preserves the source");
        check(File.Exists(Path.Combine(local, "文件夹", "子目录", "文件.bin")) && Directory.Exists(Path.Combine(local, "文件夹", "空目录")), "Paste recursively copies contents and empty folders");
        check(!Directory.Exists(Path.Combine(local, "文件夹", ".svn")) && File.ReadAllText(Path.Combine(local, ".svn", "wc.db")) == "metadata", "SVN metadata is excluded and the target metadata is preserved");
        check(File.ReadAllBytes(Path.Combine(local, "应用快捷方式.lnk")).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Windows shortcut files are copied as normal files");

        File.WriteAllText(destination, "local changes");
        plan = LocalFileTransfer.Prepare([file], local, local);
        check(plan.Conflicts.Count == 1 && File.ReadAllText(destination) == "local changes" && !Directory.Exists(backups), "Conflict preparation does not write or create backups");
        await LocalFileTransfer.CopyAsync(plan, false, backups, CancellationToken.None);
        check(File.ReadAllText(destination) == "local changes", "Skip preserves the existing file");
        await LocalFileTransfer.CopyAsync(plan, true, backups, CancellationToken.None);
        var backup = Directory.GetDirectories(backups).Single();
        check(File.ReadAllText(destination) == "new contents" && File.ReadAllText(Path.Combine(backup, Path.GetFileName(file))) == "local changes", "Replacement backs up the original content");
        var manager = new BackupManager(root);
        var summary = await manager.GetSummaryAsync(30);
        check(summary.CleanableCount == 1 && summary.ExpiredCount == 0, "Paste backups participate in the existing backup manager");

        Directory.CreateDirectory(Path.Combine(local, "文件夹", "子目录"));
        File.WriteAllText(Path.Combine(local, "文件夹", "子目录", "保留.txt"), "keep me");
        plan = LocalFileTransfer.Prepare([folder], local, local);
        await LocalFileTransfer.CopyAsync(plan, true, backups, CancellationToken.None);
        check(File.ReadAllText(Path.Combine(local, "文件夹", "子目录", "保留.txt")) == "keep me", "Directory merge preserves unrelated target files");

        plan = LocalFileTransfer.Prepare([destination], local, local);
        await LocalFileTransfer.CopyAsync(plan, false, backups, CancellationToken.None);
        check(Path.GetFileName(plan.TopLevelPaths.Single()) == "报告 @ 空格 - 副本.txt" && File.Exists(plan.TopLevelPaths.Single()), "Paste into the source directory creates a copy name");
        var duplicate = LocalFileTransfer.Prepare([destination], local, local);
        check(Path.GetFileName(duplicate.TopLevelPaths.Single()) == "报告 @ 空格 - 副本 (2).txt", "Repeated paste chooses a unique copy name");

        plan = LocalFileTransfer.Prepare([file], local, local);
        File.WriteAllText(destination, "changed after confirmation");
        await ExpectFailure(() => LocalFileTransfer.CopyAsync(plan, true, backups, CancellationToken.None));
        check(File.ReadAllText(destination) == "changed after confirmation", "A changed destination is preserved rather than overwritten");
        var newSource = Path.Combine(source, "new.txt");
        File.WriteAllText(newSource, "source");
        plan = LocalFileTransfer.Prepare([newSource], local, local);
        File.WriteAllText(Path.Combine(local, "new.txt"), "appeared later");
        await ExpectFailure(() => LocalFileTransfer.CopyAsync(plan, false, backups, CancellationToken.None));
        check(File.ReadAllText(Path.Combine(local, "new.txt")) == "appeared later", "A file that appears after preparation is preserved");

        var largeSource = Path.Combine(source, "large.bin");
        File.WriteAllBytes(largeSource, new byte[4 * 1024 * 1024]);
        var largeTarget = Path.Combine(local, "large.bin");
        File.WriteAllText(largeTarget, "original large file");
        plan = LocalFileTransfer.Prepare([largeSource], local, local);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileTransferProgress>(value =>
        {
            if (value.Bytes == 0) Thread.Sleep(120); // Allow a mid-file progress report even on a fast SSD.
            else cancellation.Cancel();
        });
        await ExpectFailure(() => LocalFileTransfer.CopyAsync(plan, true, backups, cancellation.Token, progress));
        check(File.ReadAllText(largeTarget) == "original large file", "Cancellation retains the original file");
        check(!Directory.EnumerateFiles(local, ".svnclient-new-*", SearchOption.AllDirectories).Any(), "No incomplete staging files remain after failure or cancellation");

        var invalidBackup = Path.Combine(root, "not-a-directory");
        File.WriteAllText(invalidBackup, "block backup creation");
        plan = LocalFileTransfer.Prepare([file], local, local);
        await ExpectFailure(() => LocalFileTransfer.CopyAsync(plan, true, invalidBackup, CancellationToken.None));
        check(File.ReadAllText(destination) == "changed after confirmation", "Backup failure retains the original file");
        await ExpectFailure(() => Task.Run(() => LocalFileTransfer.Prepare([file], local, source)));
        await ExpectFailure(() => Task.Run(() => LocalFileTransfer.Prepare([file], local, Path.Combine(local, ".svn"))));
        await ExpectFailure(() => Task.Run(() => LocalFileTransfer.Prepare([folder], source, Path.Combine(folder, "子目录"))));
        check(true, "Out-of-root, metadata and self-descendant destinations are rejected");
        var nested = Path.Combine(local, "嵌套工作副本");
        Directory.CreateDirectory(Path.Combine(nested, ".svn"));
        await ExpectFailure(() => Task.Run(() => LocalFileTransfer.Prepare([file], local, nested)));
        check(true, "Nested working copies cannot be modified by paste");
        var conflictingFolder = Path.Combine(source, "new.txt");
        File.Delete(conflictingFolder);
        Directory.CreateDirectory(conflictingFolder);
        await ExpectFailure(() => Task.Run(() => LocalFileTransfer.Prepare([conflictingFolder], local, local)));
        check(File.ReadAllText(Path.Combine(local, "new.txt")) == "appeared later", "File/directory name collisions are rejected without deletion");
        var junction = Path.Combine(source, "链接目录");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", "/J", junction, folder }
        })!) await process.WaitForExitAsync();
        await ExpectFailure(() => Task.Run(() => LocalFileTransfer.Prepare([junction], local, local)));
        check(File.ReadAllText(Path.Combine(folder, "子目录", "文件.bin")) == "binary", "Junctions are rejected without following their targets");
        Directory.Delete(junction, recursive: false);
    }

    private static async Task ExpectFailure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected a protected copy operation to fail.");
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
