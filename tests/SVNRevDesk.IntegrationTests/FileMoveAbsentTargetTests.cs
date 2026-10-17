using System.Diagnostics;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class FileMoveAbsentTargetTests
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
        var outside = Path.Combine(root, "outside");
        var backupRoot = Path.Combine(root, "Backups");
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(root, "Temp"), backupRoot);
        var token = CancellationToken.None;
        var overwrite = new FileMoveOptions(FileMoveConflictBehavior.OverwriteFiles);
        await operations.CheckoutAsync(new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri, local, false, token);
        foreach (var directory in new[] { source, target, outside }) Directory.CreateDirectory(directory);
        foreach (var name in new[] { "删除目标.txt", "缺失目标.txt", "普通删除.txt", "普通缺失.txt", "恢复缺失.txt", "恢复删除.txt", "迟到文件.txt", "原位置.txt", "其他移动.txt", "保留内容.txt" })
        {
            File.WriteAllText(Path.Combine(source, name), "source " + name);
            File.WriteAllText(Path.Combine(target, name), "target " + name);
        }
        Directory.CreateDirectory(Path.Combine(target, "删除目录"));
        Directory.CreateDirectory(Path.Combine(target, "缺失目录"));
        await operations.AddAsync([source, target], token);
        await operations.CommitAsync([local], "Absent target fixture", token);

        var sourceFile = Path.Combine(source, "删除目标.txt");
        var targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        await operations.DeleteAsync([targetFile], token);
        var plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        check(plan.Conflicts.Single().CanOverwrite && plan.Items.Single().DestinationSize == null,
            "A scheduled-deleted file is recognized by its local SVN kind and offers overwrite");
        var backups = new List<string>();
        await operations.MoveFilesAsync(plan, overwrite, token, backupCreated: backups.Add);
        var changes = await operations.ReadCommitChangesAsync([targetFile], local, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "source 删除目标.txt"
            && changes.Count == 2 && changes.All(change => change.RequiredMoves.Count == 1)
            && backups.Count == 0 && !Directory.Exists(backupRoot),
            "Filling a deleted target preserves the source's move pair without backing up nonexistent local content");
        await operations.CommitSelectedAsync(changes, "Fill deleted file", local, token);
        check((await operations.LogAsync(targetFile, token)).Any(entry => entry.Message == "Absent target fixture"),
            "A committed move onto a deleted path retains source history");

        sourceFile = Path.Combine(source, "缺失目标.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        File.Delete(targetFile);
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        check(plan.Conflicts.Single().CanOverwrite, "A disk-missing tracked file also offers overwrite");
        await operations.MoveFilesAsync(plan, overwrite, token);
        check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "source 缺失目标.txt"
            && (await operations.ReadCommitChangesAsync([targetFile], local, token)).Count == 2,
            "A disk-missing target is scheduled for replacement and filled through an SVN move");

        foreach (var deleted in new[] { true, false })
        {
            var name = deleted ? "普通删除.txt" : "普通缺失.txt";
            sourceFile = Path.Combine(outside, name);
            targetFile = Path.Combine(target, name);
            File.WriteAllText(sourceFile, "ordinary incoming " + name);
            await runner.RunAsync(["propset", "custom:keep", "original target property", SvnRunner.Target(targetFile)], token);
            if (deleted)
            {
                await operations.CommitAsync([targetFile], "Original target property", token);
                await operations.DeleteAsync([targetFile], token);
            }
            else File.Delete(targetFile);
            plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
            await operations.MoveFilesAsync(plan, overwrite, token);
            var status = (await operations.ReadLocalDirectoryStatusAsync(target, local, token)).Single(item => item.Path == targetFile);
            check(!File.Exists(sourceFile) && File.ReadAllText(targetFile) == "ordinary incoming " + name
                && status.Item == "modified" && (await operations.PropertiesAsync(targetFile, token)).Contains("original target property", StringComparison.Ordinal),
                "An ordinary source restores a " + (deleted ? "deleted" : "missing") + " tracked target while retaining destination properties and history");
        }

        sourceFile = Path.Combine(source, "原位置.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        // Use a new target so the reverse is an actual move-back, not undoing a replacement of another file.
        File.Delete(targetFile);
        await operations.DeleteAsync([targetFile], token);
        await operations.CommitAsync([targetFile], "Remove reverse fixture target", token);
        var forward = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        await operations.MoveFilesAsync(forward, false, token);
        File.AppendAllText(targetFile, " edits after move");
        await runner.RunAsync(["propset", "custom:after-move", "keep edits", SvnRunner.Target(targetFile)], token);
        plan = await operations.PrepareFileMoveAsync([targetFile], source, local, token);
        check(plan.Conflicts.Single().CanOverwrite && plan.Items.Single().IsReverseMove,
            "Moving a file back recognizes both ends of its pending SVN move");
        await operations.MoveFilesAsync(plan, overwrite, token);
        changes = await operations.ReadCommitChangesAsync([sourceFile], local, token);
        check(File.ReadAllText(sourceFile) == "source 原位置.txt edits after move" && !File.Exists(targetFile)
            && changes.Count == 1 && changes.Single().Status.Item == "modified"
            && changes.Single().RequiredMoves.Count == 0
            && (await operations.PropertiesAsync(sourceFile, token)).Contains("keep edits", StringComparison.Ordinal),
            "An exact reverse move collapses the pending move and retains local content and property edits");

        sourceFile = Path.Combine(source, "其他移动.txt");
        var other = Path.Combine(source, "其他位置.txt");
        await operations.MoveFilesAsync(await operations.PrepareFileRenameAsync(sourceFile, other, local, token), false, token);
        plan = await operations.PrepareFileRenameAsync(Path.Combine(target, "其他移动.txt"), sourceFile, local, token);
        check(!plan.Conflicts.Single().CanOverwrite && plan.Items.Single().OverwriteUnavailableReason.Contains("尚未提交的移动", StringComparison.Ordinal)
            && await operations.MoveFilesAsync(plan, overwrite, token) == 0 && File.Exists(other),
            "A deletion belonging to a different pending move is protected with the correct reason");

        foreach (var name in new[] { "删除目录", "缺失目录" })
        {
            targetFile = Path.Combine(target, name);
            if (name == "删除目录") await operations.DeleteAsync([targetFile], token);
            else Directory.Delete(targetFile);
            plan = await operations.PrepareFileRenameAsync(Path.Combine(source, "保留内容.txt"), targetFile, local, token);
            check(!plan.Conflicts.Single().CanOverwrite && plan.Items.Single().OverwriteUnavailableReason.Contains("类型不同", StringComparison.Ordinal)
                && await operations.MoveFilesAsync(plan, overwrite, token) == 0,
                "The local SVN kind still protects a " + name + " directory against file replacement");
        }

        sourceFile = Path.Combine(source, "迟到文件.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        await operations.DeleteAsync([targetFile], token);
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        File.WriteAllText(targetFile, "late local content");
        check(await operations.MoveFilesAsync(plan, overwrite, token) == 0 && File.Exists(sourceFile)
            && File.ReadAllText(targetFile) == "late local content", "A deleted target that reappears after confirmation is not overwritten");

        sourceFile = Path.Combine(source, "恢复缺失.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        await runner.RunAsync(["propset", "custom:missing", "keep missing property", SvnRunner.Target(targetFile)], token);
        File.Delete(targetFile);
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        await FailCommandOnceAsync("delete 完成", () => operations.MoveFilesAsync(plan, overwrite, token));
        check(File.Exists(sourceFile) && !File.Exists(targetFile)
            && (await operations.ReadLocalDirectoryStatusAsync(target, local, token)).Single(item => item.Path == targetFile).Item == "missing"
            && (await operations.PropertiesAsync(targetFile, token)).Contains("keep missing property", StringComparison.Ordinal),
            "Failure while scheduling an absent target restores its original missing state and local properties");

        sourceFile = Path.Combine(outside, "恢复删除.txt");
        targetFile = Path.Combine(target, Path.GetFileName(sourceFile));
        File.WriteAllText(sourceFile, "ordinary source survives");
        await runner.RunAsync(["propset", "custom:deleted", "keep deleted property", SvnRunner.Target(targetFile)], token);
        await operations.CommitAsync([targetFile], "Deleted target property", token);
        await operations.DeleteAsync([targetFile], token);
        plan = await operations.PrepareFileMoveAsync([sourceFile], target, local, token);
        await FailCommandOnceAsync("revert 完成", () => operations.MoveFilesAsync(plan, overwrite, token));
        check(File.ReadAllText(sourceFile) == "ordinary source survives" && !File.Exists(targetFile)
            && (await operations.ReadLocalDirectoryStatusAsync(target, local, token)).Single(item => item.Path == targetFile).Item == "deleted"
            && (await runner.RunAsync(["proplist", "--verbose", "-r", "BASE", SvnRunner.Target(targetFile)], token)).Contains("keep deleted property", StringComparison.Ordinal),
            "Failure while reviving an ordinary target restores its deletion and retains the incoming source");

        var addedMissing = Path.Combine(target, "新增后缺失.txt");
        File.WriteAllText(addedMissing, "uncommitted target");
        await operations.AddAsync([addedMissing], token);
        File.Delete(addedMissing);
        plan = await operations.PrepareFileRenameAsync(Path.Combine(source, "保留内容.txt"), addedMissing, local, token);
        check(!plan.Conflicts.Single().CanOverwrite && plan.Items.Single().OverwriteUnavailableReason.Contains("尚未提交", StringComparison.Ordinal),
            "A missing addition with no committed baseline is protected with an accurate reason");
        check(!Directory.Exists(backupRoot) && !Directory.EnumerateFiles(local, ".svnclient-new-*", SearchOption.AllDirectories).Any(),
            "Absent-target moves and recovery create neither content backups nor leftover staging files");

        async Task FailCommandOnceAsync(string marker, Func<Task> action)
        {
            var fail = true;
            void Inject(string message)
            {
                if (!fail || !message.Contains(marker, StringComparison.Ordinal)) return;
                fail = false;
                throw new IOException("Injected absent-target failure");
            }
            runner.CommandCompleted += Inject;
            try
            {
                try { await action(); }
                catch (IOException) { return; }
                throw new InvalidOperationException("Expected failure was not raised.");
            }
            finally { runner.CommandCompleted -= Inject; }
        }
    }
}
