using System.Diagnostics;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class LocalDeletionTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> assert)
    {
        Directory.CreateDirectory(fixture);
        var repositoryPath = Path.Combine(fixture, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start) ?? throw new InvalidOperationException("Cannot start svnadmin"))
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var backups = Path.Combine(fixture, "Backups");
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(fixture, "Temp"), backups);
        var repository = new RepositoryService(runner);
        var local = Path.Combine(fixture, "删除 @ 工作副本");
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var token = CancellationToken.None;
        await operations.CheckoutAsync(url, local, false, token);
        var existing = Path.Combine(local, "existing.txt");
        var modified = Path.Combine(local, "modified.txt");
        var source = Path.Combine(local, "source.txt");
        var existingDirectory = Path.Combine(local, "existing-directory");
        Directory.CreateDirectory(existingDirectory);
        foreach (var path in new[] { existing, modified, source, Path.Combine(existingDirectory, "child.txt") }) File.WriteAllText(path, "initial\n");
        await operations.AddAsync([existing, modified, source, existingDirectory], token);
        await operations.SetPropertyAsync(local, "svn:ignore", "*.tmp", token);
        await operations.CommitAsync([local], "Deletion fixture", token);
        await operations.UpdateAsync(local, true, token);
        var initialRevision = (await repository.RefreshAsync(await repository.OpenWorkingCopyAsync(local, token), token)).Revision;

        var deleteCommands = 0;
        runner.ProgressChanged += progress => { if (progress.Stage == SvnProgressStage.Started && progress.Command == "delete") deleteCommands++; };
        bool HasBackup(string name, byte[] content) => Directory.EnumerateFiles(backups, name, SearchOption.AllDirectories)
            .Any(path => File.ReadAllBytes(path).SequenceEqual(content));

        var document = await LocalItemCreator.CreateAsync(local, local, new("新建 @ Word文档.docx", NewItemKind.WordDocument), token);
        var documentBytes = File.ReadAllBytes(document);
        var output = await operations.DeleteSelectedAsync([document], local, token);
        assert(!File.Exists(document) && deleteCommands == 0 && output.Contains(backups, StringComparison.Ordinal)
            && HasBackup(Path.GetFileName(document), documentBytes),
            "Unversioned Word files are deleted locally without svn delete and their original bytes are backed up");

        var folder = Path.Combine(local, "未添加目录");
        var child = Path.Combine(folder, "nested", "未添加子文件.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        File.WriteAllText(child, "unversioned content");
        await operations.DeleteSelectedAsync([child, folder, folder], local, token);
        assert(!Directory.Exists(folder) && deleteCommands == 0 && HasBackup(Path.GetFileName(child), "unversioned content"u8.ToArray())
            && Directory.EnumerateDirectories(backups, "empty", SearchOption.AllDirectories).Any(),
            "Unversioned folders are backed up recursively, including empty folders, and overlapping selections delete once");

        var ignored = Path.Combine(local, "ignored.tmp");
        File.WriteAllText(ignored, "ignored content");
        await operations.DeleteSelectedAsync([ignored], local, token);
        assert(!File.Exists(ignored) && deleteCommands == 0 && HasBackup(Path.GetFileName(ignored), "ignored content"u8.ToArray()),
            "Explicitly selected ignored files follow local deletion rather than SVN deletion");

        var added = Path.Combine(local, "added.txt");
        File.WriteAllText(added, "new");
        await operations.AddAsync([added], token);
        File.AppendAllText(added, " edited after add");
        await operations.DeleteSelectedAsync([added], local, token);
        assert(!File.Exists(added) && !(await operations.ReadLocalDirectoryStatusAsync(local, local, token)).Any(status => status.Path == added)
            && HasBackup(Path.GetFileName(added), "new edited after add"u8.ToArray()),
            "Added but uncommitted files can be deleted after editing, canceling addition and preserving a backup");

        var addedFolder = Path.Combine(local, "已添加目录");
        var addedChild = Path.Combine(addedFolder, "已添加子文件.txt");
        var newChild = Path.Combine(addedFolder, "未添加子文件.bin");
        Directory.CreateDirectory(addedFolder);
        File.WriteAllText(addedChild, "scheduled child");
        await operations.AddAsync([addedFolder], token);
        File.WriteAllText(newChild, "unscheduled child");
        await operations.DeleteSelectedAsync([addedFolder, addedChild], local, token);
        assert(!Directory.Exists(addedFolder) && HasBackup(Path.GetFileName(addedChild), "scheduled child"u8.ToArray())
            && HasBackup(Path.GetFileName(newChild), "unscheduled child"u8.ToArray())
            && !(await operations.ReadLocalDirectoryStatusAsync(local, local, token)).Any(status => status.Path == addedFolder),
            "Deleting an added directory backs up scheduled and unscheduled children and cancels its addition");

        var copied = Path.Combine(local, "copied.txt");
        await runner.RunAsync(["copy", SvnRunner.Target(source), Path.GetFileName(copied)], token, workingDirectory: local);
        File.AppendAllText(copied, "edited copy");
        await operations.DeleteSelectedAsync([copied], local, token);
        assert(!File.Exists(copied) && File.ReadAllText(source) == "initial\n"
            && HasBackup(Path.GetFileName(copied), "initial\nedited copy"u8.ToArray()),
            "Added copies with history can be removed without changing their source, retaining local edits in backup");
        assert((await repository.RefreshAsync(await repository.OpenWorkingCopyAsync(local, token), token)).Revision == initialRevision,
            "Deleting new local items never commits or changes repository HEAD");

        var mixed = Path.Combine(local, "mixed-new.txt");
        File.WriteAllText(mixed, "mixed new");
        await operations.DeleteSelectedAsync([existing, mixed], local, token);
        assert(!File.Exists(existing) && !File.Exists(mixed)
            && (await operations.ReadLocalDirectoryStatusAsync(local, local, token)).Single(status => status.Path == existing).Item == "deleted"
            && await runner.RunAsync(["cat", SvnRunner.Target(url + "/existing.txt")], token) == "initial\n",
            "Mixed selection deletes new files locally and schedules existing files while the server still retains them");
        var session = await repository.OpenWorkingCopyAsync(local, token);
        var snapshot = await repository.RefreshAsync(session, token);
        var entries = await repository.ReadDirectoryAsync(session, snapshot, "", token);
        var scheduledDeletion = entries.Single(entry => entry.Name == "existing.txt");
        assert(scheduledDeletion.State == FileState.LocalDeleted && scheduledDeletion.StateText == "本地已删除"
            && scheduledDeletion.Detail.Contains("尚未提交", StringComparison.Ordinal),
            "Scheduled deletion has its own local-deleted state and pending-commit explanation");
        File.Delete(source);
        snapshot = await repository.RefreshAsync(session, token);
        entries = await repository.ReadDirectoryAsync(session, snapshot, "", token);
        assert(entries.Single(entry => entry.Name == "source.txt").State == FileState.Missing,
            "A tracked file removed outside SVN remains local missing rather than scheduled deletion");
        await operations.RevertAsync([source], token);
        var deletedStatus = snapshot.Statuses[existing];
        assert(FileStateClassifier.Classify(true, false, deletedStatus with { RepositoryItem = "modified" }, true) == FileState.BothChanged
            && FileStateClassifier.Classify(true, false, deletedStatus with { TreeConflict = true }, true) == FileState.Conflict,
            "Concurrent remote edits and actual conflicts keep their higher-priority states");
        assert((int)FileState.Unchecked == 9 && (int)FileState.LocalDeleted == 10,
            "New deletion state preserves existing numeric values in persisted browsing caches");
        await operations.CommitAsync([existing], "Commit existing deletion", token);
        assert(!SvnXml.ReadList(await runner.RunAsync(["list", "--xml", SvnRunner.Target(url)], token)).Any(entry => entry.Name == "existing.txt"),
            "An existing server file is removed only after committing its scheduled deletion");

        File.AppendAllText(modified, "local changes");
        var protectedNew = Path.Combine(local, "protected-new.txt");
        File.WriteAllText(protectedNew, "keep new");
        var refused = false;
        try { await operations.DeleteSelectedAsync([modified, protectedNew], local, token); }
        catch (InvalidOperationException) { refused = true; }
        assert(refused && File.ReadAllText(modified) == "initial\nlocal changes" && File.ReadAllText(protectedNew) == "keep new"
            && (await operations.ReadLocalDirectoryStatusAsync(local, local, token)).Single(status => status.Path == modified).Item == "modified",
            "Versioned local edits are never force-deleted, and a failed versioned deletion preserves new items in the same selection");
        var extra = Path.Combine(existingDirectory, "extra.txt");
        File.WriteAllText(extra, "keep extra");
        refused = false;
        try { await operations.DeleteSelectedAsync([existingDirectory], local, token); }
        catch (SvnException) { refused = true; }
        assert(refused && File.ReadAllText(extra) == "keep extra" && File.Exists(Path.Combine(existingDirectory, "child.txt")),
            "Existing directories with unversioned content retain SVN's normal deletion protection");

        var outer = Path.Combine(local, "outer");
        var nested = Path.Combine(outer, "nested-working-copy");
        await operations.CheckoutAsync(url, nested, false, token);
        refused = false;
        try { await operations.DeleteSelectedAsync([outer], local, token); }
        catch (InvalidOperationException) { refused = true; }
        assert(refused && Directory.Exists(Path.Combine(nested, ".svn")),
            "Local-only directory deletion refuses to remove nested working copies");
        var outside = Path.Combine(fixture, "outside.txt");
        File.WriteAllText(outside, "outside");
        var guarded = true;
        foreach (var path in new[] { outside, local, Path.Combine(local, ".svn") })
        {
            try { await operations.DeleteSelectedAsync([path], local, token); guarded = false; }
            catch (ArgumentException) { }
        }
        assert(guarded && File.ReadAllText(outside) == "outside" && Directory.Exists(Path.Combine(local, ".svn")),
            "Deletion refuses paths outside the opened scope, its root and SVN metadata");

        var locked = Path.Combine(local, "locked-new.txt");
        File.WriteAllText(locked, "locked");
        refused = false;
        using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await operations.DeleteSelectedAsync([locked], local, token); }
            catch (IOException) { refused = true; }
        }
        assert(refused && File.ReadAllText(locked) == "locked", "A backup failure leaves the new local file untouched");

        var cancelFile = Path.Combine(local, "cancel-new.txt");
        File.WriteAllText(cancelFile, "cancel content");
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        void CancelBeforeDelete(SvnProgress progress)
        {
            if (progress.Command == "status" && progress.Stage == SvnProgressStage.Started && ++reads == 2) cancellation.Cancel();
        }
        runner.ProgressChanged += CancelBeforeDelete;
        var cancelled = false;
        try { await operations.DeleteSelectedAsync([cancelFile], local, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        finally { runner.ProgressChanged -= CancelBeforeDelete; }
        assert(cancelled && File.ReadAllText(cancelFile) == "cancel content" && HasBackup(Path.GetFileName(cancelFile), "cancel content"u8.ToArray()),
            "Cancellation after backup preserves the new local file and its completed backup");
    }
}
