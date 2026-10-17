using System.Diagnostics;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class CommitSelectionTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> assert)
    {
        Directory.CreateDirectory(fixture);
        var repositoryPath = Path.Combine(fixture, "repository");
        var start = new ProcessStartInfo("svnadmin.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start) ?? throw new InvalidOperationException("Cannot start svnadmin"))
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(fixture, "Temp"));
        var token = CancellationToken.None;
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var local = Path.Combine(fixture, "中文 @ 工作副本");
        await operations.CheckoutAsync(url, local, false, token);
        var directory = Path.Combine(local, "目录");
        var deletedDirectory = Path.Combine(local, "删除目录");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(deletedDirectory);
        var chosen = Path.Combine(directory, "勾选 @ 文件.txt");
        var uncheckedFile = Path.Combine(directory, "未勾选.txt");
        var missing = Path.Combine(local, "missing.txt");
        File.WriteAllText(chosen, "initial\n");
        File.WriteAllText(uncheckedFile, "initial\n");
        File.WriteAllText(missing, "initial\n");
        File.WriteAllText(Path.Combine(deletedDirectory, "child.txt"), "initial\n");
        await operations.AddAsync([directory, deletedDirectory, missing], token);
        await operations.CommitAsync([local], "", token);
        assert((await operations.LogAsync(url, token)).First().Message == "",
            "Direct commit accepts an empty message and records no generated description");
        await operations.UpdateAsync(local, true, token);

        File.AppendAllText(chosen, "chosen change\n");
        File.AppendAllText(uncheckedFile, "unchecked change\n");
        await operations.SetPropertyAsync(directory, "svn:ignore", "ignore-me*.tmp", token);
        File.WriteAllText(Path.Combine(local, "unversioned.txt"), "new\n");
        File.Delete(missing);
        var changes = await operations.ReadCommitChangesAsync([local], local, token);
        assert(changes.Any(change => change.Path == chosen && change.CanCommit)
            && changes.Any(change => change.Path == directory && change.StateText == "属性修改"),
            "Commit list includes changed files and directory properties from fresh local status");
        assert(changes.Single(change => change.Status.Item == "unversioned").CanCommit
            && changes.Single(change => change.Status.Item == "unversioned").NeedsAddition
            && changes.Single(change => change.Path == missing).CanCommit == false,
            "Unversioned items can be selected for automatic addition while missing files remain blocked");
        var direct = await operations.ReadCommitChangesAsync([chosen], local, token);
        assert(direct.Count == 1 && direct[0].Path == chosen, "Selecting a file does not include sibling changes or unrelated parent properties");
        var overlapping = await operations.ReadCommitChangesAsync([directory, chosen], local, token);
        assert(overlapping.Select(change => change.Path).Distinct().Count() == overlapping.Count,
            "Overlapping file/directory commit scopes are deduplicated");

        await operations.CommitSelectedAsync(changes.Where(change => change.Path == chosen || change.Path == directory).ToList(), "*\n按勾选提交", local, token);
        var remaining = await operations.ReadCommitChangesAsync([directory], local, token);
        assert(remaining.Count == 1 && remaining[0].Path == uncheckedFile,
            "Checking a directory property and one file does not recursively commit an unchecked child");
        assert((await operations.LogAsync(url, token)).First().Message == "*\n按勾选提交",
            "Selected commit preserves multiline Unicode and literal wildcard messages");

        var addedDirectory = Path.Combine(local, "新增目录");
        var addedNested = Path.Combine(addedDirectory, "子目录");
        Directory.CreateDirectory(addedNested);
        var addedChosen = Path.Combine(addedNested, "勾选.txt");
        var addedUnchecked = Path.Combine(addedNested, "未勾选.txt");
        File.WriteAllText(addedChosen, "chosen\n");
        File.WriteAllText(addedUnchecked, "unchecked\n");
        await operations.AddAsync([addedDirectory], token);
        var added = await operations.ReadCommitChangesAsync([addedChosen], local, token);
        assert(added.Count == 3 && added.Single(change => change.Path == addedChosen).RequiredParents.Count == 2,
            "Selecting an added file includes its required new ancestor directories");
        var refused = false;
        try { await operations.CommitSelectedAsync(added.Where(change => change.Path == addedChosen).ToList(), "Missing ancestors", local, token); }
        catch (ArgumentException) { refused = true; }
        assert(refused, "Added child commit requires explicit selection of its new parent directories");
        await operations.CommitSelectedAsync(added, "Only selected new file and parents", local, token);
        var newRemaining = await operations.ReadCommitChangesAsync([addedDirectory], local, token);
        assert(newRemaining.Count == 1 && newRemaining[0].Path == addedUnchecked && newRemaining[0].Status.Item == "added",
            "New directory commit leaves its unchecked added files uncommitted");

        await operations.DeleteAsync([deletedDirectory], token);
        var deleted = await operations.ReadCommitChangesAsync([local], local, token);
        var deletion = deleted.Single(change => change.Path == deletedDirectory);
        assert(deletion.IsDirectory && deletion.RequiresChildren
            && !deleted.Any(change => RepositoryService.IsDescendant(change.Path, deletedDirectory)),
            "A scheduled directory deletion is represented as one indivisible commit item");
        await operations.CommitSelectedAsync([deletion], "Delete selected directory", local, token);
        assert(!(await operations.ReadCommitChangesAsync([local], local, token)).Any(change => change.Path == deletedDirectory),
            "Selected directory deletion succeeds with exact-depth commit");

        refused = false;
        try { await operations.CommitSelectedAsync([], "Empty selection", local, token); }
        catch (ArgumentException) { refused = true; }
        assert(refused, "Empty commit selection is rejected before running SVN");
        await operations.CommitSelectedAsync(newRemaining, "", local, token);
        assert((await operations.ReadCommitChangesAsync([addedUnchecked], local, token)).Count == 0
            && (await operations.LogAsync(url, token)).First().Message == "",
            "Selected commit succeeds with an empty message and leaves the repository description empty");
        File.AppendAllText(addedUnchecked, "change with blank message\n");
        var blankMessageSelection = await operations.ReadCommitChangesAsync([addedUnchecked], local, token);
        await operations.CommitSelectedAsync(blankMessageSelection, " ", local, token);
        assert((await operations.ReadCommitChangesAsync([addedUnchecked], local, token)).Count == 0
            && string.IsNullOrWhiteSpace((await operations.LogAsync(url, token)).First().Message),
            "Selected commit also accepts a whitespace-only message");
        refused = false;
        try { await operations.CommitSelectedAsync(changes.Where(change => !change.CanCommit).ToList(), "Blocked items", local, token); }
        catch (ArgumentException) { refused = true; }
        assert(refused, "Uncommittable checked items are rejected before running SVN");

        var bulkDirectory = Path.Combine(local, "bulk");
        Directory.CreateDirectory(bulkDirectory);
        var bulkFiles = Enumerable.Range(0, 250).Select(index => Path.Combine(bulkDirectory, $"file-{index:000}-abcdefghijklmnopqrstuvwxyz0123456789.txt")).ToList();
        foreach (var path in bulkFiles) File.WriteAllText(path, "bulk\n");
        await operations.AddAsync([bulkDirectory], token);
        var bulk = await operations.ReadCommitChangesAsync(bulkFiles, local, token);
        assert(bulk.Count == bulkFiles.Count + 1 && bulk.Sum(change => change.Path.Length + 3) > 32767,
            "Large file selection is read in batches and includes its required parent");
        await operations.CommitSelectedAsync(bulk, "All bulk files in one revision", local, token);
        assert((await operations.ReadCommitChangesAsync([bulkDirectory], local, token)).Count == 0
            && (await operations.LogAsync(url, token)).First().Message == "All bulk files in one revision",
            "Select-all commit bypasses Windows command-line limits and remains one SVN transaction");

        var cancellationSelection = await operations.ReadCommitChangesAsync([uncheckedFile], local, token);
        using (var cancellation = new CancellationTokenSource())
        {
            void CancelCommit(SvnProgress progress) { if (progress.Command == "commit" && progress.Stage == SvnProgressStage.Started) cancellation.Cancel(); }
            runner.ProgressChanged += CancelCommit;
            var cancelled = false;
            try { await operations.CommitSelectedAsync(cancellationSelection, "Cancel before sending", local, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            finally { runner.ProgressChanged -= CancelCommit; }
            assert(cancelled, "Selected commit remains cancellable");
        }
        assert(!Directory.EnumerateFiles(Path.Combine(fixture, "Temp")).Any(), "Commit message temporary files are removed after selected commits");
        await CommitAutoAddTests.RunAsync(fixture, local, url, operations, runner, assert);
    }
}
