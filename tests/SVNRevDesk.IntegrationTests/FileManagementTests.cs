using System.Diagnostics;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class FileManagementTests
{
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var repoPath = Path.Combine(root, "repository");
        var process = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        process.ArgumentList.Add("create"); process.ArgumentList.Add(repoPath);
        using (var admin = Process.Start(process)!)
        {
            var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new IOException(await error);
        }
        using var runner = new SvnRunner();
        var ops = new SvnOperations(runner);
        var service = new RepositoryService(runner);
        var token = CancellationToken.None;
        var url = new Uri(repoPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var wc = Path.Combine(root, "working-copy");
        await ops.CheckoutAsync(url, wc, false, token);
        Directory.CreateDirectory(Path.Combine(wc, "docs", "deep"));
        Directory.CreateDirectory(Path.Combine(wc, "other"));
        File.WriteAllText(Path.Combine(wc, "docs", "deep", "report@one.txt"), "original\n");
        File.WriteAllText(Path.Combine(wc, "other", "report.txt"), "outside scope\n");
        await ops.AddAsync([Path.Combine(wc, "docs"), Path.Combine(wc, "other")], token);
        await ops.CommitAsync([wc], "search fixture", token);
        var session = await service.OpenWorkingCopyAsync(wc, token);
        var localFolder = Path.Combine(wc, "local-folder"); Directory.CreateDirectory(localFolder);
        File.WriteAllText(Path.Combine(localFolder, "report-local.txt"), "unversioned\n");
        File.WriteAllText(Path.Combine(wc, "~$report.docx"), "office temporary\n");
        var ignores = new LocalIgnoreStore(Path.Combine(root, "ignores.json")); ignores.Set(localFolder, true);

        async Task<(RepositorySearchResult Summary, BrowserEntry[] Entries)> Search(RepositorySession target, string scope, string query, bool ignored = false, int limit = 5000)
        {
            var entries = new List<BrowserEntry>();
            await using var search = new RepositorySearchIndex(target, new SvnSearchIndexSource(runner), Path.Combine(root, "Cache"), ignores);
            await search.RefreshAsync();
            var summary = await search.SearchAsync(scope, query, ignored, new InlineProgress<RepositorySearchProgress>(p => entries.AddRange(p.Matches)), token, limit);
            return (summary, entries.ToArray());
        }

        var found = await Search(session, "docs", "REPORT");
        check(found.Entries.Select(e => e.RelativePath).SequenceEqual(["docs/deep/report@one.txt"]), "Search recurses within the selected subtree and matches names without case sensitivity");
        found = await Search(session, "", "deep/report@");
        check(found.Entries.Single().Name == "report@one.txt", "Search matches relative path segments and literal @ names");
        found = await Search(session, "", @"deep\report@");
        check(found.Entries.Single().Name == "report@one.txt", "Search accepts Windows path separators");
        found = await Search(session, "", "report");
        check(found.Entries.Length == 2 && !found.Entries.Any(e => e.Name.StartsWith("~$")), "Default search omits ignored subtrees, SVN metadata and Office lock files");
        found = await Search(session, "", "report", true);
        check(found.Entries.Length == 3 && found.Entries.Any(e => e.RelativePath == "local-folder/report-local.txt"), "Search can include ignored and unversioned descendants");
        found = await Search(session, "", "report", true, 1);
        check(found.Summary is { LimitReached: true, MatchCount: 1 } && found.Entries.Length == 1, "Search stops at its result limit and marks the results as incomplete");
        var online = await service.OpenUrlAsync(url, token);
        found = await Search(online, "", "report");
        check(found.Entries.Length == 2 && found.Entries.All(e => e.LocalPath == null), "Online search returns server-only files without a working copy");
        var sparse = Path.Combine(root, "sparse"); await ops.CheckoutAsync(url, sparse, true, token);
        found = await Search(await service.OpenWorkingCopyAsync(sparse, token), "", "report");
        check(found.Entries.Length == 2 && found.Entries.All(e => !e.ExistsLocally), "Sparse search discovers files in never-downloaded directories without downloading them");
        check(!Directory.Exists(Path.Combine(sparse, "docs")), "Read-only searching leaves sparse working copy depth and content unchanged");
        using (var cancelled = new CancellationTokenSource())
        {
            var pages = 0;
            await using var search = new RepositorySearchIndex(session, new SvnSearchIndexSource(runner), Path.Combine(root, "Cache"), ignores);
            await search.RefreshAsync();
            await Reject<OperationCanceledException>(() => search.SearchAsync("", "report", true,
                new InlineProgress<RepositorySearchProgress>(_ => { pages++; cancelled.Cancel(); }), cancelled.Token));
            check(pages == 1, "Search cancellation stops after the current indexed result batch");
        }
        await Reject<ArgumentException>(() => Search(session, "../other", "report"));
        await Reject<ArgumentException>(() => Search(session, "", "  "));
        check(true, "Search rejects traversal scopes and empty queries");

        // A switched subtree must be searched using its actual URL, not its parent's repository path.
        await ops.CopyBranchAsync(url + "/docs", url + "/branch", "branch fixture", token);
        await ops.UpdateAsync(wc, true, token);
        File.WriteAllText(Path.Combine(wc, "branch", "branch-only.txt"), "switched\n");
        await ops.AddAsync([Path.Combine(wc, "branch", "branch-only.txt")], token); await ops.CommitAsync([wc], "branch file", token);
        await ops.UpdateAsync(wc, true, token);
        await ops.SwitchAsync(url + "/branch", Path.Combine(wc, "docs"), token);
        found = await Search(session, "docs", "branch-only");
        check(found.Entries.Single().RelativePath == "docs/branch-only.txt" && found.Entries.Single().Url.Contains("/branch/"), "Search honors switched directory mappings");
        var nested = Path.Combine(wc, "nested-copy"); await ops.CheckoutAsync(url + "/other", nested, false, token);
        found = await Search(session, "", "nested-copy/report");
        check(found.Entries.Length == 0, "Search does not traverse nested working copies");

        var renameFolder = Path.Combine(wc, "rename"); Directory.CreateDirectory(renameFolder);
        var a = Path.Combine(renameFolder, "draft-a.txt"); var b = Path.Combine(renameFolder, "draft-b.txt");
        var folder = Path.Combine(renameFolder, "draft-folder"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "child.txt"), "child\n");
        File.WriteAllText(a, "a\n"); File.WriteAllText(b, "b\n");
        await ops.AddAsync([renameFolder], token); await ops.CommitAsync([wc], "rename fixture", token);
        File.AppendAllText(a, "local edit\n");
        var unversioned = Path.Combine(renameFolder, "draft-new.txt"); File.WriteAllText(unversioned, "new\n");
        BatchRenameSource[] sources = [new(a, false), new(b, false), new(folder, true), new(unversioned, false)];
        var rows = BatchRename.Preview(sources, new("draft-", "final-", "pre-", "-", true, 7, 2));
        check(rows.Select(r => r.NewName).SequenceEqual(["pre-final-a-07.txt", "pre-final-b-08.txt", "pre-final-folder-09", "pre-final-new-10.txt"]), "Batch preview combines replacement, affixes and ordered numbering while preserving extensions");
        check(sources.All(s => File.Exists(s.Path) || Directory.Exists(s.Path)), "Rename preview makes no filesystem or SVN changes");
        var noExtension = BatchRename.Preview([new(a, false)], new(Find: ".txt", Replace: ".md", PreserveExtension: false));
        check(noExtension.Single().NewName == "draft-a.md", "The extension can optionally participate in the rename rule");
        var duplicate = BatchRename.Preview([new(a, false), new(b, false)], new("draft-a", "draft-b"));
        check(duplicate.All(r => r.Error.Length > 0), "Duplicate generated names are marked in every affected preview row");
        check(BatchRename.Preview([new(a, false)], new("draft", "DRAFT")).Single().Error.Length > 0, "Unsupported case-only renames are explicitly rejected");
        foreach (var prefix in new[] { "../", "bad:", "CON.", ".svn/" })
            check(BatchRename.Preview([new(a, false)], new(Prefix: prefix)).Single().Error.Length > 0, "Invalid generated names are blocked: " + prefix);
        await Reject<ArgumentException>(() => ops.PrepareBatchRenameAsync(BatchRename.Preview(sources, new()), wc, token));
        check(true, "An unchanged batch cannot execute");

        var plan = await ops.PrepareBatchRenameAsync(rows, wc, token);
        var completed = await ops.MoveFilesAsync(plan, new FileMoveOptions(), token);
        check(completed == 4 && rows.All(r => !File.Exists(r.Source) && !Directory.Exists(r.Source)), "Batch execution renames versioned files, a directory and an unversioned file together");
        check(File.ReadAllText(rows[0].Destination) == "a\nlocal edit\n" && File.ReadAllText(Path.Combine(rows[2].Destination, "child.txt")) == "child\n", "Batch renaming preserves local edits and directory content");
        var statuses = await ops.ReadLocalDirectoryStatusAsync(renameFolder, wc, token);
        check(statuses.Any(s => s.Path == rows[0].Destination && s.Copied) && statuses.Any(s => s.Path == a && s.Item == "deleted"), "Versioned batch renames retain SVN move history and remain uncommitted");
        check(statuses.Any(s => s.Path == rows[3].Destination && s.Item == "unversioned"), "Unversioned files stay unversioned after batch rename");
        var unchangedHead = (await service.RefreshDirectoryAsync(session, new(""), token)).Revision;
        await ops.CommitAsync([renameFolder], "commit batch rename", token);
        var history = await ops.LogPageAsync(RepositoryService.AppendUrl(url, "rename/" + rows[0].NewName), token: token);
        check(history.Entries.Any(e => e.Message == "rename fixture") && history.PegRevision == unchangedHead + 1, "Committed batch renames follow the original file's history");

        var currentSource = rows[0].Destination;
        var safe = BatchRename.Preview([new(currentSource, false)], new(Prefix: "safe-"));
        var stalePlan = await ops.PrepareBatchRenameAsync(safe, wc, token);
        File.WriteAllText(safe[0].Destination, "late arrival");
        await Reject<IOException>(() => ops.MoveFilesAsync(stalePlan, new FileMoveOptions(), token));
        check(File.Exists(currentSource) && File.ReadAllText(safe[0].Destination) == "late arrival", "Execution rechecks occupied targets and preserves both files");
        var absentTarget = rows[1].Destination; await ops.DeleteAsync([absentTarget], token);
        await Reject<IOException>(() => ops.PrepareBatchRenameAsync([new(currentSource, absentTarget, "")], wc, token));
        check(File.Exists(currentSource), "SVN scheduled-delete targets cannot be overwritten by batch rename");
        await Reject<ArgumentException>(() => ops.PrepareBatchRenameAsync([new(wc, wc + "-renamed", "")], wc, token));
        await Reject<ArgumentException>(() => ops.PrepareBatchRenameAsync([new(currentSource, Path.Combine(wc, "outside-parent.txt"), "")], wc, token));
        await Reject<ArgumentException>(() => ops.PrepareBatchRenameAsync([new(currentSource, Path.Combine(root, "outside-wc.txt"), "")], wc, token));
        check(true, "Batch rename rejects roots, cross-directory and outside-working-copy destinations");
        var cancelledRows = BatchRename.Preview([new(currentSource, false)], new(Prefix: "cancel-"));
        var cancelledPlan = await ops.PrepareBatchRenameAsync(cancelledRows, wc, token);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); await Reject<OperationCanceledException>(() => ops.MoveFilesAsync(cancelledPlan, new FileMoveOptions(), cancelled.Token));
        }
        check(File.Exists(currentSource) && !File.Exists(cancelledRows[0].Destination), "Cancellation before rename leaves source content unchanged");
        var remaining = Path.Combine(renameFolder, "partial-source.txt"); File.WriteAllText(remaining, "keep remaining\n");
        var partialRows = BatchRename.Preview([new(rows[3].Destination, false), new(remaining, false)], new(Prefix: "partial-"));
        var partialPlan = await ops.PrepareBatchRenameAsync(partialRows, wc, token);
        using (var partial = new CancellationTokenSource())
        {
            await Reject<OperationCanceledException>(() => ops.MoveFilesAsync(partialPlan, new FileMoveOptions(), partial.Token,
                new InlineProgress<FileMoveProgress>(p => { if (p.CompletedItems == 1) partial.Cancel(); })));
        }
        check(File.Exists(partialRows[0].Destination) && File.ReadAllText(partialRows[1].Source) == "keep remaining\n"
            && !File.Exists(partialRows[1].Destination), "Partial cancellation retains completed renames and all remaining source files");
    }

    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }
}
