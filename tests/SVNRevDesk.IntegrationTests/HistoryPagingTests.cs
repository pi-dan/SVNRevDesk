using System.Diagnostics;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class HistoryPagingTests
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
        using var runner = new SvnRunner { Username = "alice" };
        var operations = new SvnOperations(runner);
        var token = CancellationToken.None;
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var wc = Path.Combine(root, "working-copy");
        await operations.CheckoutAsync(url, wc, false, token);
        var original = Path.Combine(wc, "original.txt");
        File.WriteAllText(original, "one\n");
        await operations.AddAsync([original], token); await operations.CommitAsync([wc], "initial\nfull message", token);
        runner.Username = "bob";
        File.AppendAllText(original, "two\n"); await operations.CommitAsync([wc], "second", token);
        var renamed = Path.Combine(wc, "renamed@file.txt");
        await operations.MoveAsync(original, renamed, token); await operations.CommitAsync([wc], "renamed", token);
        var other = Path.Combine(wc, "other.txt"); File.WriteAllText(other, "unrelated\n");
        await operations.AddAsync([other], token); await operations.CommitAsync([wc], "other branch work", token);
        File.AppendAllText(other, "five\n"); await operations.CommitAsync([wc], "fifth", token);

        var first = await operations.LogPageAsync(url, pageSize: 2, token: token);
        check(first.PegRevision == 5 && first.Entries.Select(e => e.Revision).SequenceEqual([5L, 4L]) && first.NextStartRevision == 3,
            "History paging starts at live HEAD and reports an older page");
        File.AppendAllText(other, "six\n"); await operations.CommitAsync([wc], "arrived during paging", token);
        var all = first.Entries.ToList(); var page = first;
        while (page.NextStartRevision != null)
        {
            page = await operations.LogPageAsync(url, page.NextStartRevision, page.PegRevision, 2, token);
            all.AddRange(page.Entries);
        }
        check(all.Select(e => e.Revision).Distinct().Count() == all.Count && all.Any(e => e.Revision == 1) && all.All(e => e.Revision <= 5),
            "Older pages reach repository creation without duplicates or newly arriving commits");
        check(all.Single(e => e.Revision == 1) is { Author: "alice", Message: "initial\nfull message", Date: not null }
            && all.Single(e => e.Revision == 2).Author == "bob", "Paged history preserves authors, timestamps and multiline messages");
        var fileUrl = url + "/renamed@file.txt";
        var fileFirst = await operations.LogPageAsync(fileUrl, pageSize: 2, token: token);
        var fileLast = await operations.LogPageAsync(fileUrl, fileFirst.NextStartRevision, fileFirst.PegRevision, 2, token);
        check(fileFirst.Entries.Select(e => e.Revision).SequenceEqual([3L, 2L]) && fileLast.Entries.Single().Revision == 1
            && fileLast.NextStartRevision == null, "Paging follows renamed @ paths with a fixed peg and handles gaps between commits");
        var one = await operations.LogPageAsync(fileUrl, 1, fileFirst.PegRevision, 1, token);
        check(one.Entries.Single().Revision == 1 && one.NextStartRevision == null, "An exactly full final history page does not require an empty extra fetch");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await operations.LogPageAsync(url, token: cancelled.Token); throw new InvalidOperationException("Cancellation was ignored"); }
        catch (OperationCanceledException) { check(true, "History reads honor cancellation before starting SVN"); }
        await Invalid(() => operations.LogPageAsync(url, -1, token: token));
        await Invalid(() => operations.LogPageAsync(url, pageSize: 0, token: token));
        await Invalid(() => operations.LogPageAsync(url, 7, 6, token: token));
        check(true, "Invalid paging bounds and page sizes are rejected");

        var scope = Path.Combine(wc, "scope");
        var nested = Path.Combine(scope, "nested"); Directory.CreateDirectory(nested);
        var fileA = Path.Combine(scope, "a.txt"); var fileB = Path.Combine(scope, "b.txt");
        File.WriteAllText(fileA, "a\n"); File.WriteAllText(fileB, "b\n");
        await operations.AddAsync([scope], token); await operations.CommitAsync([wc], "create selected targets", token); // r7
        File.AppendAllText(fileA, "eight\n"); await operations.CommitAsync([wc], "only a", token); // r8
        File.AppendAllText(other, "nine\n"); await operations.CommitAsync([wc], "outside selection", token); // r9
        File.AppendAllText(fileB, "ten\n"); await operations.CommitAsync([wc], "only b", token); // r10
        var child = Path.Combine(nested, "child.txt"); File.WriteAllText(child, "eleven\n");
        await operations.AddAsync([child], token); await operations.CommitAsync([wc], "nested child", token); // r11
        File.AppendAllText(fileA, "twelve\n"); File.AppendAllText(fileB, "twelve\n");
        await operations.CommitAsync([wc], "both selected files", token); // r12
        var aUrl = url + "/scope/a.txt"; var bUrl = url + "/scope/b.txt";
        var scopedFile = await operations.LogPageAsync(aUrl, token: token);
        check(scopedFile.Entries.Select(e => e.Revision).SequenceEqual([12L, 8L, 7L]),
            "A selected file lists only its own changes and excludes sibling and unrelated commits");
        var scopedDirectory = await operations.LogPageAsync(url + "/scope", token: token);
        check(scopedDirectory.Entries.Select(e => e.Revision).SequenceEqual([12L, 11L, 10L, 8L, 7L]),
            "A selected directory includes descendants and excludes changes outside its subtree");
        var union = await AllPages([aUrl, bUrl, aUrl], 2);
        check(union.SequenceEqual([12L, 10L, 8L, 7L]), "Multiselection merges and deduplicates only selected histories across page boundaries");
        union = await AllPages([aUrl, url + "/scope/nested/child.txt"], 2);
        check(union.SequenceEqual([12L, 11L, 8L, 7L]), "An exhausted newer target does not block older pages from another selected file");
        union = await AllPages([aUrl, url + "/scope"], 2);
        check(union.SequenceEqual([12L, 11L, 10L, 8L, 7L]), "Overlapping file and directory selections keep one row per affected revision");
        var beforeCreation = await operations.LogPageAsync(aUrl, 6, 12, token: token);
        check(beforeCreation.Entries.Count == 0 && beforeCreation.NextStartRevision == null,
            "Reading before a target's creation returns no unrelated revisions");

        async Task<long[]> AllPages(string[] targets, int size)
        {
            var revisions = new List<long>();
            var result = await operations.LogPageAsync(targets, pageSize: size, token: token);
            while (true)
            {
                revisions.AddRange(result.Entries.Select(entry => entry.Revision));
                if (result.NextStartRevision == null) return revisions.ToArray();
                result = await operations.LogPageAsync(targets, result.NextStartRevision, result.PegRevision, size, token);
            }
        }

        static async Task Invalid(Func<Task> action)
        {
            try { await action(); }
            catch (ArgumentOutOfRangeException) { return; }
            throw new InvalidOperationException("Expected invalid history request to be rejected");
        }
    }
}
