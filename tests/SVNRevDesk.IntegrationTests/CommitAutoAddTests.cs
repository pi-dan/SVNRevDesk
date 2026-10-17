using System.Text;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class CommitAutoAddTests
{
    public static async Task RunAsync(string fixture, string local, string url, SvnOperations operations,
        SvnRunner runner, Action<bool, string> assert)
    {
        var token = CancellationToken.None;
        var config = Path.Combine(fixture, "AutoAddConfig");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config"), "[miscellany]\nglobal-ignores = [Tt]humbs.db *.tmp\n  skip-client.*\n", new UTF8Encoding(false));
        runner.ConfigurationDirectory = config;
        await operations.SetPropertyAsync(local, "svn:global-ignores", "*.private", token);
        var root = Path.Combine(local, "未添加 @ 目录");
        var nested = Path.Combine(root, "子目录");
        Directory.CreateDirectory(nested);
        var chosen = Path.Combine(nested, "选择 @ 文件.txt");
        var uncheckedFile = Path.Combine(nested, "未选择.txt");
        File.WriteAllText(chosen, "new selected content\n");
        File.WriteAllText(uncheckedFile, "new unchecked content\n");
        foreach (var name in new[] { "Thumbs.db", "skip-client.log", "ignored.private" })
            File.WriteAllText(Path.Combine(nested, name), "ignored\n");

        var commands = new List<string>();
        void Record(SvnProgress progress) { if (progress.Stage == SvnProgressStage.Started) commands.Add(progress.Command); }
        runner.ProgressChanged += Record;
        var revision = (await operations.LogAsync(url, token)).First().Revision;
        var discovered = await operations.ReadCommitChangesAsync([root], local, token);
        runner.ProgressChanged -= Record;
        assert(discovered.Count == 4 && discovered.All(change => change.NeedsAddition && change.CanCommit)
            && discovered.Single(change => change.Path == chosen).RequiredParents.Count == 2,
            "Unversioned directories expand into selectable files with all required parent dependencies");
        assert(!discovered.Any(change => change.Extension is ".db" or ".log" or ".private"),
            "Unversioned traversal respects multiline client ignores, glob character classes and inherited global ignores");
        assert(commands.All(command => command is "status" or "info" or "proplist" or "log")
            && (await operations.LogAsync(url, token)).First().Revision == revision,
            "Reading the commit dialog neither schedules additions nor changes the server revision");
        var selectedScope = await operations.ReadCommitChangesAsync([chosen], local, token);
        assert(selectedScope.Count == 3 && selectedScope.All(change => change.Path != uncheckedFile),
            "Selecting an unversioned file inspects only that file and its required new parents");
        var rejected = false;
        try { await operations.CommitSelectedAsync(selectedScope.Where(change => change.Path == chosen).ToList(), "Missing new parents", local, token); }
        catch (ArgumentException) { rejected = true; }
        assert(rejected && (await operations.ReadCommitChangesAsync([root], local, token)).All(change => change.NeedsAddition),
            "Missing parent selections are rejected before scheduling any additions");
        await operations.CommitSelectedAsync(selectedScope, "Automatically add selected nested file", local, token);
        var remaining = await operations.ReadCommitChangesAsync([root], local, token);
        assert(remaining.Count == 1 && remaining[0].Path == uncheckedFile && remaining[0].NeedsAddition,
            "Automatic shallow addition and exact commit leave unchecked siblings unversioned");
        var chosenDownload = await operations.DownloadForViewingAsync(RepositoryService.AppendUrl(url, "未添加 @ 目录/子目录/选择 @ 文件.txt"),
            "HEAD", Path.Combine(fixture, "AutoAddCache"), token);
        assert(File.ReadAllText(chosenDownload) == "new selected content\n"
            && (await operations.LogAsync(url, token)).First().Revision == revision + 1,
            "Automatic additions and selected content reach the server together in one revision");

        var directoryOnly = Path.Combine(local, "只选目录");
        Directory.CreateDirectory(directoryOnly);
        var keptChild = Path.Combine(directoryOnly, "未勾选子项.txt");
        File.WriteAllText(keptChild, "keep unversioned\n");
        var directoryChanges = await operations.ReadCommitChangesAsync([directoryOnly], local, token);
        await operations.CommitSelectedAsync(directoryChanges.Where(change => change.Path == directoryOnly).ToList(), "Only add the directory", local, token);
        assert((await operations.ReadCommitChangesAsync([directoryOnly], local, token)).Single().Path == keptChild
            && (await operations.ReadCommitChangesAsync([keptChild], local, token)).Single().NeedsAddition,
            "Checking a new directory alone adds and commits the directory without its unchecked files");
        await operations.CommitSelectedAsync(await operations.ReadCommitChangesAsync([uncheckedFile, keptChild], local, token), "Select all remaining new files", local, token);
        assert((await operations.ReadCommitChangesAsync([root, directoryOnly], local, token)).Count == 0,
            "Multiple checked unversioned files are automatically added and committed together");

        var cancelPath = Path.Combine(local, "取消自动添加.txt");
        File.WriteAllText(cancelPath, "preserve during cancellation\n");
        var cancelSelection = await operations.ReadCommitChangesAsync([cancelPath], local, token);
        using (var cancellation = new CancellationTokenSource())
        {
            void CancelCommit(SvnProgress progress) { if (progress.Command == "commit" && progress.Stage == SvnProgressStage.Started) cancellation.Cancel(); }
            runner.ProgressChanged += CancelCommit;
            var cancelled = false;
            try { await operations.CommitSelectedAsync(cancelSelection, "Cancel after adding", local, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            finally { runner.ProgressChanged -= CancelCommit; }
            assert(cancelled && File.ReadAllText(cancelPath) == "preserve during cancellation\n"
                && (await operations.ReadCommitChangesAsync([cancelPath], local, token)).Single().Status.Item == "added",
                "Cancelling the commit after addition preserves the file and its scheduled-add state for retry");
        }

        var nestedWorkingCopy = Path.Combine(local, "nested-working-copy");
        await operations.CheckoutAsync(url, nestedWorkingCopy, false, token);
        var nestedChanges = await operations.ReadCommitChangesAsync([local], local, token);
        assert(nestedChanges.Single(change => change.Path == nestedWorkingCopy).CanCommit == false
            && !nestedChanges.Any(change => RepositoryService.IsDescendant(change.Path, nestedWorkingCopy)),
            "Unversioned discovery does not enter or automatically add a nested working copy");
        assert(!Directory.EnumerateFiles(Path.Combine(fixture, "Temp")).Any(),
            "Automatic addition and commit clean up their target/message temporary files");
        await OfficeTemporaryFileTests.RunAsync(local, operations, runner, assert);
    }
}
