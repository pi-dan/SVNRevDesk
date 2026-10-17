using System.Diagnostics;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class FileMoveOverwriteTests
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
        var source = Path.Combine(local, "来源 @ 目录");
        var target = Path.Combine(local, "目标 @ 目录");
        var outside = Path.Combine(root, "外部来源");
        var backupRoot = Path.Combine(root, "data", "Backups");
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(root, "data", "Temp"), backupRoot);
        var token = CancellationToken.None;
        var overwrite = new FileMoveOptions(FileMoveConflictBehavior.OverwriteFiles);
        var direct = overwrite with { BackupBeforeOverwrite = false };
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await operations.CheckoutAsync(url, local, false, token);
        foreach (var directory in new[] { source, target, outside }) Directory.CreateDirectory(directory);
        foreach (var name in new[] { "有备份 @ 文件.txt", "直接覆盖.txt", "普通移入.txt", "未版本目标.txt", "恢复状态.txt", "迟到变化.txt", "备份失败.txt", "取消覆盖.txt", "新增目标.txt", "新增恢复.txt" })
        {
            File.WriteAllText(Path.Combine(source, name), "source " + name);
            File.WriteAllText(Path.Combine(target, name), "target " + name);
        }
        Directory.CreateDirectory(Path.Combine(source, "同名目录"));
        Directory.CreateDirectory(Path.Combine(target, "同名目录"));
        File.WriteAllText(Path.Combine(source, "同名目录", "source.txt"), "source child");
        File.WriteAllText(Path.Combine(target, "同名目录", "target.txt"), "target child");
        await operations.AddAsync([source, target], token);
        await operations.CommitAsync([local], "Original overwrite fixture", token);

        var sourceFile = Path.Combine(source, "有备份 @ 文件.txt");
        var targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        File.AppendAllText(sourceFile, " local source edits");
        File.AppendAllText(targetFile, " local target edits");
        var expectedTarget = File.ReadAllText(targetFile);
        var expectedSource = File.ReadAllText(sourceFile);
        var plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        check(plan.Conflicts.Single().CanOverwrite && File.Exists(sourceFile), "Same-name versioned files offer an overwrite without changing either file during preparation");
        var backups = new List<string>();
        check(await operations.MoveFilesAsync(plan, overwrite, token, backupCreated: backups.Add) == 1
            && !File.Exists(sourceFile) && File.ReadAllText(targetFile) == expectedSource
            && backups.Count == 1 && File.ReadAllText(backups.Single()) == expectedTarget,
            "Confirmed overwrite backs up the old target's local edits and moves the source content");
        var changes = await operations.ReadCommitChangesAsync([targetFile], local, token);
        check(changes.Count == 2 && changes.Any(change => change.Path == targetFile && change.Status.Item == "replaced")
            && changes.All(change => change.RequiredMoves.Count == 1), "Overwriting a versioned target remains a paired SVN replacement with source history");
        await operations.CommitSelectedAsync(changes, "Overwrite using source history", local, token);
        var history = await operations.LogAsync(targetFile, token);
        check(history.Any(entry => entry.Message == "Original overwrite fixture") && history.Any(entry => entry.Message == "Overwrite using source history"),
            "Committing the overwritten move retains the original source's history");
        var manager = new BackupManager(Path.Combine(root, "data"));
        var summary = await manager.GetSummaryAsync(30);
        check(summary.CleanableCount == 1 && summary.RetainedCount == 0, "Move overwrite backups participate in the existing retention and manual-cleanup policy");

        sourceFile = Path.Combine(source, "直接覆盖.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        var backupCount = Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories).Count();
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        await operations.MoveFilesAsync(plan, direct, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "source 直接覆盖.txt"
            && Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories).Count() == backupCount,
            "Disabling overwrite backup replaces a versioned file without creating permanent backup files");

        sourceFile = Path.Combine(outside, "普通移入.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        File.WriteAllText(sourceFile, "ordinary incoming content");
        await runner.RunAsync(["propset", "custom:keep", "keep destination properties", SvnRunner.Target(targetFile)], token);
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        await operations.MoveFilesAsync(plan, direct, token);
        var statuses = await operations.ReadLocalDirectoryStatusAsync(target, local, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "ordinary incoming content"
            && statuses.Single(status => status.Path == targetFile).Item == "modified"
            && (await operations.PropertiesAsync(targetFile, token)).Contains("keep destination properties", StringComparison.Ordinal),
            "An Explorer-origin ordinary file replaces tracked contents while preserving destination history and properties");

        sourceFile = Path.Combine(source, "未版本目标.txt");
        targetFile = Path.Combine(target, "未版本文件.txt");
        File.WriteAllText(targetFile, "unversioned target");
        plan = await operations.PrepareFileRenameAsync(sourceFile, targetFile, local, token);
        await operations.MoveFilesAsync(plan, direct, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "source 未版本目标.txt"
            && (await runner.RunAsync(["info", "--xml", SvnRunner.Target(targetFile)], token)).Contains("<copy-from-url>", StringComparison.Ordinal),
            "A versioned source can overwrite an unversioned target through rename while retaining source history");

        sourceFile = Path.Combine(source, "新增目标.txt");
        targetFile = Path.Combine(target, "added-target.txt");
        File.WriteAllText(targetFile, "added target contents");
        await operations.AddAsync([targetFile], token);
        plan = await operations.PrepareFileRenameAsync(sourceFile, targetFile, local, token);
        check(plan.Conflicts.Single().CanOverwrite, "Newly added ordinary target files can be overwritten before their first commit");
        await operations.MoveFilesAsync(plan, direct, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "source 新增目标.txt"
            && (await runner.RunAsync(["info", "--xml", SvnRunner.Target(targetFile)], token)).Contains("<copy-from-url>", StringComparison.Ordinal),
            "Overwriting a newly added target substitutes the versioned source's history");

        var copiedTarget = Path.Combine(target, "copied-target.txt");
        await runner.RunAsync(["copy", SvnRunner.Target(Path.Combine(source, "备份失败.txt")), ".\\copied-target.txt"], token, workingDirectory: target);
        plan = await operations.PrepareFileRenameAsync(Path.Combine(source, "取消覆盖.txt"), copiedTarget, local, token);
        check(!plan.Conflicts.Single().CanOverwrite && await operations.MoveFilesAsync(plan, direct, token) == 0
            && File.Exists(copiedTarget) && File.Exists(Path.Combine(source, "取消覆盖.txt")),
            "Targets with an existing uncommitted SVN copy are skipped to preserve their pending history relationship");

        plan = await operations.PrepareFileMoveAsync([Path.Combine(source, "同名目录")], target, local, token);
        check(!plan.Conflicts.Single().CanOverwrite && await operations.MoveFilesAsync(plan, direct, token) == 0
            && File.Exists(Path.Combine(source, "同名目录", "source.txt")) && File.Exists(Path.Combine(target, "同名目录", "target.txt")),
            "Same-name directory moves are explicitly skipped without replacing or merging either tree");

        sourceFile = Path.Combine(source, "迟到变化.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        var free = Path.Combine(outside, "free-before-conflict.txt");
        File.WriteAllText(free, "untouched free source");
        plan = await operations.PrepareFileMoveAsync([free, sourceFile], target, local, token);
        File.WriteAllText(targetFile, "changed while waiting for confirmation");
        await RejectAsync(() => operations.MoveFilesAsync(plan, direct, token));
        check(File.Exists(free) && File.Exists(sourceFile) && File.ReadAllText(targetFile) == "changed while waiting for confirmation",
            "A target changed after the dialog plan stops the batch before moving unrelated free files");

        sourceFile = Path.Combine(source, "恢复状态.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        File.AppendAllText(targetFile, " target local modifications");
        await runner.RunAsync(["propset", "custom:local", "本地属性\n保留", SvnRunner.Target(targetFile)], token);
        var originalContent = File.ReadAllText(targetFile);
        var originalProperties = await operations.PropertiesAsync(targetFile, token);
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        var failDeletion = true;
        void FailAfterDeletion(string message)
        {
            if (!failDeletion || !message.Contains("delete 完成", StringComparison.Ordinal)) return;
            failDeletion = false;
            throw new IOException("Injected failure after destination scheduling");
        }
        runner.CommandCompleted += FailAfterDeletion;
        try { await RejectAsync(() => operations.MoveFilesAsync(plan, direct, token)); }
        finally { runner.CommandCompleted -= FailAfterDeletion; }
        statuses = await operations.ReadLocalDirectoryStatusAsync(target, local, token);
        check(File.Exists(sourceFile) && File.ReadAllText(targetFile) == originalContent
            && await operations.PropertiesAsync(targetFile, token) == originalProperties
            && statuses.Single(status => status.Path == targetFile).Item == "modified"
            && !Directory.EnumerateFiles(target, ".svnclient-new-*").Any(),
            "Failure after scheduling a replacement restores original target contents and local properties even without a permanent backup");

        sourceFile = Path.Combine(source, "新增恢复.txt");
        targetFile = Path.Combine(target, "added-restore.txt");
        File.WriteAllText(targetFile, "original newly added content");
        await operations.AddAsync([targetFile], token);
        await runner.RunAsync(["propset", "custom:added", "added local value", SvnRunner.Target(targetFile)], token);
        plan = await operations.PrepareFileRenameAsync(sourceFile, targetFile, local, token);
        failDeletion = true;
        runner.CommandCompleted += FailAfterDeletion;
        try { await RejectAsync(() => operations.MoveFilesAsync(plan, direct, token)); }
        finally { runner.CommandCompleted -= FailAfterDeletion; }
        statuses = await operations.ReadLocalDirectoryStatusAsync(target, local, token);
        check(File.Exists(sourceFile) && File.ReadAllText(targetFile) == "original newly added content"
            && statuses.Single(status => status.Path == targetFile).Item == "added"
            && (await operations.PropertiesAsync(targetFile, token)).Contains("added local value", StringComparison.Ordinal),
            "A failed replacement restores a newly added target's scheduling and properties");

        var brokenBackup = Path.Combine(root, "not-a-directory");
        File.WriteAllText(brokenBackup, "blocks backup creation");
        var failedBackupOperations = new SvnOperations(runner, backupDirectory: brokenBackup);
        sourceFile = Path.Combine(source, "备份失败.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        plan = await failedBackupOperations.PrepareFileMoveAsync([sourceFile], target, local, token);
        await RejectAsync(() => failedBackupOperations.MoveFilesAsync(plan, overwrite, token));
        check(File.Exists(sourceFile) && File.ReadAllText(targetFile) == "target 备份失败.txt", "A requested backup failure leaves source and target intact");
        await failedBackupOperations.MoveFilesAsync(plan, direct, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "source 备份失败.txt", "Direct overwrite does not require a usable backup directory");

        sourceFile = Path.Combine(source, "取消覆盖.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        using var cancellation = new CancellationTokenSource();
        await RejectAsync(() => operations.MoveFilesAsync(plan, overwrite, cancellation.Token, backupCreated: _ => cancellation.Cancel()));
        check(File.Exists(sourceFile) && File.ReadAllText(targetFile) == "target 取消覆盖.txt", "Cancellation during backup leaves both move endpoints unchanged");
        check(!Directory.EnumerateFiles(local, ".svnclient-new-*", SearchOption.AllDirectories).Any(), "Successful, rejected and recovered overwrite paths leave no staging files in the working copy");
        await FileMoveAbsentTargetTests.RunAsync(Path.Combine(root, "absent-targets"), check);
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException) { return; }
        throw new InvalidOperationException("An unconfirmed or unsafe overwrite was accepted.");
    }
}
