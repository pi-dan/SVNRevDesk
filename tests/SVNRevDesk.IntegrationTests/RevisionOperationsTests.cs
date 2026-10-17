using System.Diagnostics;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class RevisionOperationsTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var repository = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create"); start.ArgumentList.Add(repository);
        using (var admin = Process.Start(start)!)
        {
            var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var token = CancellationToken.None;
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var wc = Path.Combine(root, "working @ copy");
        await operations.CheckoutAsync(url, wc, false, token);
        var file = Path.Combine(wc, "file@sample.txt");
        var removed = Path.Combine(wc, "removed.txt");
        var added = Path.Combine(wc, "added.txt");
        const string original = "first\na\nb\nc\nd\ne\nf\ng\nlast\n";
        File.WriteAllText(file, original); File.WriteAllText(removed, "restore this file");
        await operations.AddAsync([file, removed], token);
        await operations.CommitAsync([wc], "initial", token); // r1
        File.WriteAllText(file, original.Replace("first", "target change"));
        File.WriteAllText(added, "remove this addition");
        await operations.AddAsync([added], token); await operations.DeleteAsync([removed], token);
        await runner.RunAsync(["propset", "custom:test", "target", SvnRunner.Target(file)], token);
        await operations.CommitAsync([wc], "target commit", token); // r2
        File.WriteAllText(file, File.ReadAllText(file).Replace("last", "later change"));
        await operations.CommitAsync([wc], "later commit", token); // r3

        await operations.UpdateToRevisionAsync(wc, true, 1, token);
        check(File.ReadAllText(file) == original && File.Exists(removed) && !File.Exists(added), "Directory update to r1 restores historical files recursively");
        await operations.UpdateToRevisionAsync(file, false, 2, token);
        check(File.ReadAllText(file).Contains("target change") && !File.Exists(added), "Selected file update to r2 leaves sibling paths at their existing revisions");
        await operations.UpdateAsync(wc, true, token);
        var atHead = File.ReadAllText(file);
        File.WriteAllText(file, atHead.Replace("d\n", "local edit\n"));
        await operations.UpdateToRevisionAsync(file, false, 2, token);
        check(File.ReadAllText(file).Contains("local edit") && !File.ReadAllText(file).Contains("later change"), "Revision updates preserve nonconflicting local edits");
        await operations.RevertAsync([wc], token); await operations.UpdateAsync(wc, true, token);
        await Rejected(() => operations.UpdateToRevisionAsync(wc, true, -1, token));
        await Rejected(() => operations.ReverseMergeAsync(wc, 0, false, token));
        await Rejected(() => operations.UpdateToRevisionAsync(wc, true, 9999, token));
        check(File.ReadAllText(file) == atHead, "Invalid or nonexistent revision requests leave working files intact");

        var preview = await operations.ReverseMergeAsync(wc, 2, true, token);
        check(!string.IsNullOrWhiteSpace(preview) && File.ReadAllText(file) == atHead && !File.Exists(removed) && File.Exists(added)
            && !(await Status()).Any(status => status.HasLocalChanges), "Reverse merge preview reports changes without changing files or SVN status");
        File.WriteAllText(file, "user data");
        await Rejected(() => operations.ReverseMergeAsync(wc, 2, false, token));
        check(File.ReadAllText(file) == "user data", "Execution rechecks modifications made after preview and preserves user data");
        await operations.RevertAsync([file], token);
        await runner.RunAsync(["propset", "custom:local", "keep", SvnRunner.Target(file)], token);
        await Rejected(() => operations.ReverseMergeAsync(wc, 2, false, token));
        check((await operations.PropertiesAsync(file, token)).Contains("custom:local"), "Property-only local changes block reverse merge without reverting properties");
        await operations.RevertAsync([file], token);
        var unversioned = Path.Combine(wc, "user.txt"); File.WriteAllText(unversioned, "keep");
        await Rejected(() => operations.ReverseMergeAsync(wc, 2, false, token));
        check(File.ReadAllText(unversioned) == "keep", "Unversioned files block a directory rollback without being removed");
        File.Delete(unversioned);
        File.Delete(file);
        await Rejected(() => operations.ReverseMergeAsync(wc, 2, false, token));
        await operations.RevertAsync([file], token);
        check(File.Exists(file), "Missing tracked files require recovery before reverse merge");

        await operations.ReverseMergeAsync(wc, 2, false, token);
        check(File.ReadAllText(file) == original.Replace("last", "later change"), "Reverse merge undoes only r2 while preserving the later r3 change in the same file");
        check(File.Exists(removed) && !File.Exists(added) && !(await operations.PropertiesAsync(file, token)).Contains("custom:test"), "Reverse merge restores deletions, removes additions and reverses property changes");
        var head = SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", "-r", "HEAD", SvnRunner.Target(url)], token)).Single();
        check(head.Revision == 3 && (await Status()).Any(status => status.HasLocalChanges), "Reverse merge creates pending local changes without committing to the server");
        await operations.RevertAsync([wc], token);
        // Revert can leave a formerly added file unversioned; remove only our disposable fixture.
        if (File.Exists(removed)) File.Delete(removed);
        await operations.UpdateAsync(wc, true, token);
        await operations.ReverseMergeAsync(file, 3, true, token);
        check(File.ReadAllText(file) == atHead, "A file containing @ supports a read-only reverse merge preview");
        await operations.ReverseMergeAsync(file, 3, false, token);
        check(File.ReadAllText(file) == original.Replace("first", "target change") && File.Exists(added), "File reverse merge changes only the chosen file and handles @ literally");

        await HistoryPagingTests.RunAsync(Path.Combine(root, "history-paging"), check);

        async Task<IReadOnlyList<SVNRevDesk.Core.Models.WorkingStatus>> Status() => SvnXml.ReadStatus(
            await runner.RunAsync(["status", "--xml", SvnRunner.Target(wc)], token));
        async Task Rejected(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SvnException) { return; }
            throw new InvalidOperationException("Expected revision operation to be rejected");
        }
    }
}
