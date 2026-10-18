using System.Diagnostics;
using System.Text;
using SVNRevDesk.Core.Comparison;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class TextConflictTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var adminStart = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = root };
        adminStart.ArgumentList.Add("create"); adminStart.ArgumentList.Add("repository");
        using (var admin = Process.Start(adminStart)!)
        { var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync(); if (admin.ExitCode != 0) throw new IOException(await error); }
        var url = new Uri(Path.Combine(root, "repository") + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(root, "Temp"), Path.Combine(root, "Backups"));
        var token = CancellationToken.None;
        var wc = Path.Combine(root, "working-copy"); var peer = Path.Combine(root, "peer");
        Task Property(string directory, string name, string property, string value) => runner.RunAsync(["propset", property, value, SvnRunner.Target(Path.Combine(directory, name))]);
        await operations.CheckoutAsync(url, wc, false, token);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = Encoding.GetEncoding("GB18030");
        var names = new[] { "merge @.txt", "markers.txt", "changed.txt", "cancel.txt", "utf16.txt", "gb.txt", "binary.dat", "property.txt", "tree.txt", "empty.txt" };
        void Write(string directory, string name, string side)
        {
            var encoding = name == "utf16.txt" ? Encoding.Unicode : name == "gb.txt" ? gb : new UTF8Encoding(false);
            if (name == "binary.dat") File.WriteAllBytes(Path.Combine(directory, name), [0, (byte)side[0], 0, 255]);
            else File.WriteAllText(Path.Combine(directory, name), $"开始\n{side}\n结束\n", encoding);
        }
        foreach (var name in names) Write(wc, name, "original");
        await operations.AddAsync(names.Select(name => Path.Combine(wc, name)), token);
        await Property(wc, "utf16.txt", "svn:mime-type", "text/plain");
        await Property(wc, "binary.dat", "svn:mime-type", "application/octet-stream");
        await Property(wc, "property.txt", "review-note", "original");
        await operations.CommitAsync([wc], "initial", token);
        await operations.CheckoutAsync(url, peer, false, token);
        foreach (var name in names) { Write(wc, name, "local 本地"); Write(peer, name, "incoming 服务器"); }
        await Property(wc, "property.txt", "review-note", "mine");
        await Property(peer, "property.txt", "review-note", "theirs");
        await operations.RevertAsync([Path.Combine(peer, "tree.txt")], token);
        await operations.DeleteAsync([Path.Combine(peer, "tree.txt")], token);
        await operations.CommitAsync([peer], "incoming changes", token);
        await operations.UpdateAsync(wc, true, token);
        async Task<string> Status(string name) => SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--depth", "empty", SvnRunner.Target(Path.Combine(wc, name))])).Single().Item;
        async Task Rejected(Func<Task> action, string description)
        {
            var rejected = false;
            try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { rejected = true; }
            check(rejected, description);
        }
        var path = Path.Combine(wc, names[0]); var original = File.ReadAllBytes(path);
        var merge = await operations.PrepareTextMergeAsync(path, wc, token);
        check(File.ReadAllText(merge.BaseFile).Contains("original") && File.ReadAllText(merge.LocalFile).Contains("local 本地")
            && File.ReadAllText(merge.IncomingFile).Contains("incoming 服务器") && File.ReadAllBytes(merge.WorkingBackup).SequenceEqual(original)
            && File.ReadAllBytes(path).SequenceEqual(original) && await Status(names[0]) == "conflicted", "Preparing a text merge backs up the exact conflict versions and leaves the working file and SVN status intact");
        Write(peer, names[0], "later HEAD"); await operations.CommitAsync([Path.Combine(peer, names[0])], "later version", token);
        check(!File.ReadAllText(merge.IncomingFile).Contains("later HEAD"), "Merge inputs use the version that caused the conflict, not a later HEAD");
        await Rejected(() => operations.ApplyTextMergeAsync(merge, token), "A merge without an explicitly saved result cannot resolve the conflict");
        File.WriteAllText(merge.ResultFile, "合并结果\nlocal 本地\nincoming 服务器\n");
        await operations.ApplyTextMergeAsync(merge, token);
        check(File.ReadAllText(path) == File.ReadAllText(merge.ResultFile) && await Status(names[0]) == "modified"
            && File.ReadAllBytes(merge.WorkingBackup).SequenceEqual(original), "Applying a saved merge writes the result and resolves only the text conflict while retaining the original backup");
        check(await Status("cancel.txt") == "conflicted" && !Directory.EnumerateFiles(wc, ".svnrevdesk-merge-*").Any(), "Resolving one file leaves sibling conflicts untouched and cleans its staging file");

        merge = await operations.PrepareTextMergeAsync(Path.Combine(wc, "markers.txt"), wc, token);
        File.Copy(merge.WorkingBackup, merge.ResultFile);
        await Rejected(() => operations.ApplyTextMergeAsync(merge, token), "Unremoved conflict markers block writeback and SVN resolution");
        check(await Status("markers.txt") == "conflicted", "Rejected merged content retains the unresolved SVN conflict");
        merge = await operations.PrepareTextMergeAsync(Path.Combine(wc, "changed.txt"), wc, token);
        File.WriteAllText(merge.ResultFile, "draft"); File.WriteAllText(merge.Path, "external editor changed this");
        await Rejected(() => operations.ApplyTextMergeAsync(merge, token), "A file changed by another editor during merging is never overwritten");
        check(File.ReadAllText(merge.Path) == "external editor changed this" && await Status("changed.txt") == "conflicted", "A stale merge preserves the newer working content and the conflict");
        merge = await operations.PrepareTextMergeAsync(Path.Combine(wc, "cancel.txt"), wc, token);
        File.WriteAllText(merge.ResultFile, "saved draft"); var beforeCancel = File.ReadAllBytes(merge.Path);
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel(); var cancelled = false;
            try { await operations.ApplyTextMergeAsync(merge, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && File.ReadAllBytes(merge.Path).SequenceEqual(beforeCancel) && await Status("cancel.txt") == "conflicted", "Cancelling before writeback preserves the working copy and saved draft");
        }
        foreach (var (name, encoding) in new[] { ("utf16.txt", Encoding.Unicode), ("gb.txt", gb) })
        {
            merge = await operations.PrepareTextMergeAsync(Path.Combine(wc, name), wc, token);
            var result = encoding.GetPreamble().Concat(encoding.GetBytes("中文合并：本地 + 服务器\r\n")).ToArray();
            File.WriteAllBytes(merge.ResultFile, result); await operations.ApplyTextMergeAsync(merge, token);
            check(File.ReadAllBytes(merge.Path).SequenceEqual(result) && await Status(name) == "modified", $"Merged {encoding.WebName} Chinese text retains its exact encoding and line endings");
        }
        merge = await operations.PrepareTextMergeAsync(Path.Combine(wc, "empty.txt"), wc, token);
        File.WriteAllBytes(merge.ResultFile, []); await operations.ApplyTextMergeAsync(merge, token);
        check(new FileInfo(merge.Path).Length == 0 && await Status("empty.txt") == "modified", "An intentionally saved empty result is a valid resolution");
        foreach (var name in new[] { "binary.dat", "property.txt", "tree.txt" })
            await Rejected(async () => { await operations.PrepareTextMergeAsync(Path.Combine(wc, name), wc, token); }, $"Text merge rejects unsupported conflict type: {name}");
        await Rejected(() => operations.ApplyTextMergeAsync(merge, token), "A merge whose SVN conflict was already resolved cannot be applied again");
        check(File.ReadAllText(Path.Combine(peer, names[0])).Contains("later HEAD"), "Conflict resolution never commits or changes the repository version");

        await VerifyWinMergeAsync(Path.Combine(root, "merge-tool"), check);
    }

    public static async Task VerifyWinMergeAsync(string root, Action<bool, string> check, bool interactive = false)
    {
        Directory.CreateDirectory(root);
        var basis = Path.Combine(root, "base.txt"); var local = Path.Combine(root, "local.txt"); var incoming = Path.Combine(root, "incoming.txt"); var result = Path.Combine(root, "result.txt");
        File.WriteAllText(basis, "first\nsecond\nthird\nfourth\nfifth\n");
        File.WriteAllText(local, "local first\nsecond\nthird\nfourth\nfifth\n");
        File.WriteAllText(incoming, "first\nsecond\nthird\nfourth\nincoming fifth\n");
        var executable = await WinMergeExecutable.ResolveAsync(null, root);
        var start = WinMergeExecutable.CreateMergeStartInfo(executable, local, basis, incoming, result, root);
        check(start.ArgumentList.Contains("/wl") && start.ArgumentList.Contains("/wr") && !start.ArgumentList.Contains("/wm")
            && start.ArgumentList.TakeLast(5).SequenceEqual(new[] { local, basis, incoming, "/o", result }), "Three-way merge protects both sides, edits the common ancestor, and saves into an independent result path");
        if (interactive)
        {
            // Optional manual probe: WinMerge requires the user to save its edited middle buffer.
            using var process = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            check(File.Exists(result) && File.ReadAllText(result) == "local first\nsecond\nthird\nfourth\nincoming fifth\n"
                && File.ReadAllText(basis) == "first\nsecond\nthird\nfourth\nfifth\n", "The actual bundled WinMerge merges independent changes from both sides and saves the output without changing its source files");
        }
        else File.WriteAllText(result, "saved draft\n");
        var resume = WinMergeExecutable.CreateMergeStartInfo(executable, local, basis, incoming, result, root);
        check(!resume.ArgumentList.Contains("/am") && resume.ArgumentList.TakeLast(5).SequenceEqual(new[] { local, result, incoming, "/o", result }), "Reopening WinMerge continues the saved draft instead of resetting previous merge edits");
    }
}
