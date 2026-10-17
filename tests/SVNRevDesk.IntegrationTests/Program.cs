using System.Diagnostics;
using System.Text;
using SVNRevDesk.Core.Comparison;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

// No test framework dependency: this exercises a real, disposable file:// repository with svnadmin.
if (args.FirstOrDefault() == "--probe-bundled-svn") return await SvnExecutableTests.ProbeAsync(args.ElementAtOrDefault(1));
if (args.FirstOrDefault() == "--revisions")
{
    var count = 0;
    try { await RevisionOperationsTests.RunAsync(Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "revisions", Guid.NewGuid().ToString("N"))),
        (condition, description) => { if (!condition) throw new InvalidOperationException("FAIL: " + description); count++; Console.WriteLine("PASS: " + description); }); }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    Console.WriteLine($"All {count} revision assertions passed."); return 0;
}
if (args.FirstOrDefault() == "--portable-ci")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "portable-ci", Guid.NewGuid().ToString("N")));
    var count = 0;
    try { await PortableCiTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++; Console.WriteLine("PASS: " + description);
    }); }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    Console.WriteLine($"All {count} portable CI assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--ignore")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "ignore", Guid.NewGuid().ToString("N")));
    var count = 0;
    try { await IgnoreTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++; Console.WriteLine("PASS: " + description);
    }); }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    Console.WriteLine($"All {count} ignore assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--commit-selection")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "commit-selection", Guid.NewGuid().ToString("N")));
    var count = 0;
    try
    {
        await CommitSelectionTests.RunAsync(root, (condition, description) =>
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            count++;
            Console.WriteLine("PASS: " + description);
        });
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    Console.WriteLine($"All {count} commit-selection assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--file-move")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "file-move", Guid.NewGuid().ToString("N")));
    var count = 0;
    try { await FileMoveTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++;
        Console.WriteLine("PASS: " + description);
    }); }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    Console.WriteLine($"All {count} file-move assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--local-deletion")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "local-deletion", Guid.NewGuid().ToString("N")));
    var count = 0;
    await LocalDeletionTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++;
        Console.WriteLine("PASS: " + description);
    });
    Console.WriteLine($"All {count} local-deletion assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--file-transfer")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "file-transfer", Guid.NewGuid().ToString("N")));
    var count = 0;
    await FileTransferTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++;
        Console.WriteLine("PASS: " + description);
    });
    Console.WriteLine($"All {count} file-transfer assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--probe-bundled-winmerge")
    return await EmbeddedWinMergeTests.ProbeAsync(args.ElementAtOrDefault(1) ?? AppContext.BaseDirectory);
if (args.FirstOrDefault() == "--winmerge")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "winmerge-tests", Guid.NewGuid().ToString("N")));
    var count = 0;
    await EmbeddedWinMergeTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++;
        Console.WriteLine("PASS: " + description);
    });
    Console.WriteLine($"All {count} WinMerge assertions passed.");
    return 0;
}
if (args.FirstOrDefault() == "--backup-management")
{
    var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(".build", "backup-tests", Guid.NewGuid().ToString("N")));
    var count = 0;
    await BackupManagerTests.RunAsync(root, (condition, description) =>
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        count++;
        Console.WriteLine("PASS: " + description);
    });
    Console.WriteLine($"All {count} backup assertions passed.");
    return 0;
}
var testRoot = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "integration", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);
Console.WriteLine($"Test artifacts: {testRoot}");
using var runner = new SvnRunner();
var repository = new RepositoryService(runner);
var operations = new SvnOperations(runner);
var token = CancellationToken.None;
var assertions = 0;
var progressEvents = new List<SvnProgress>();
var progressWasAvailableAtCompletion = false;
runner.ProgressChanged += progress => progressEvents.Add(progress);
runner.CommandCompleted += _ => progressWasAvailableAtCompletion = progressEvents.Any(progress => progress.Stage == SvnProgressStage.Output);

void Assert(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    assertions++;
    Console.WriteLine("PASS: " + description);
}

async Task<IReadOnlyList<BrowserEntry>> Read(string directory, string relative = "")
{
    var session = await repository.OpenWorkingCopyAsync(directory, token);
    var snapshot = await repository.RefreshAsync(session, token);
    return await repository.ReadDirectoryAsync(session, snapshot, relative, token);
}

async Task ExpectState(string directory, string name, FileState state)
{
    var entry = (await Read(directory)).Single(e => e.Name == name);
    Assert(entry.State == state, $"{name}: {state.DisplayName()} (actual {entry.StateText})");
}

async Task Admin(params string[] arguments)
{
    var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start svnadmin");
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
}

try
{
    var repositoryPath = Path.Combine(testRoot, "repository");
    await Admin("create", repositoryPath);
    var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
    var seed = Path.Combine(testRoot, "seed");
    var local = Path.Combine(testRoot, "中文 @ 工作副本");
    var peer = Path.Combine(testRoot, "peer");
    var sparse = Path.Combine(testRoot, "sparse");
    var cache = Path.Combine(testRoot, "cache");
    await operations.CheckoutAsync(url, seed, false, token);
    var names = new[] { "equal.txt", "remote.txt", "local.txt", "both.txt", "missing.txt", "deleted.txt", "conflict.txt", "名字 @ 空格.txt" };
    foreach (var name in names) File.WriteAllText(Path.Combine(seed, name), "line one\nline two\n", new UTF8Encoding(false));
    Directory.CreateDirectory(Path.Combine(seed, "nested"));
    File.WriteAllText(Path.Combine(seed, "nested", "child.txt"), "child\n");
    await operations.AddAsync(names.Select(name => Path.Combine(seed, name)).Append(Path.Combine(seed, "nested")), token);
    await operations.CommitAsync([seed], "Initial files", token);
    await operations.CheckoutAsync(url, local, false, token);
    await operations.CheckoutAsync(url, peer, false, token);

    File.AppendAllText(Path.Combine(local, "local.txt"), "local change\n");
    File.AppendAllText(Path.Combine(local, "both.txt"), "local change\n");
    File.WriteAllText(Path.Combine(local, "conflict.txt"), "LOCAL\nline two\n");
    File.Delete(Path.Combine(local, "missing.txt"));
    File.WriteAllText(Path.Combine(local, "new.txt"), "unversioned\n");
    File.WriteAllText(Path.Combine(local, "added.txt"), "scheduled addition\n");
    await operations.AddAsync([Path.Combine(local, "added.txt")], token);
    File.AppendAllText(Path.Combine(peer, "remote.txt"), "server change\n");
    File.AppendAllText(Path.Combine(peer, "both.txt"), "server change\n");
    File.WriteAllText(Path.Combine(peer, "conflict.txt"), "SERVER\nline two\n");
    File.AppendAllText(Path.Combine(peer, "nested", "child.txt"), "server child\n");
    await operations.DeleteAsync([Path.Combine(peer, "deleted.txt")], token);
    await operations.CommitAsync([peer], "Server changes", token);

    await ExpectState(local, "equal.txt", FileState.Equal);
    await ExpectState(local, "remote.txt", FileState.ServerChanged);
    await ExpectState(local, "local.txt", FileState.LocalModified);
    await ExpectState(local, "both.txt", FileState.BothChanged);
    await ExpectState(local, "missing.txt", FileState.Missing);
    await ExpectState(local, "new.txt", FileState.LocalAdded);
    await ExpectState(local, "added.txt", FileState.LocalAdded);
    await ExpectState(local, "deleted.txt", FileState.ServerDeleted);
    var rootEntries = await Read(local);
    Assert(rootEntries.First().IsDirectory, "Directories precede files");
    Assert(rootEntries.Single(e => e.Name == "nested").Summary.Contains("服务器有更新"), "Directory summarizes descendant changes");
    Assert(rootEntries.Single(e => e.Name == "nested") is { State: FileState.Equal, HasDifferences: true },
        "Unchanged directory still reports changed descendants as differences");
    var localDirectoryDiff = await operations.DirectoryBaseDiffAsync(local, token);
    var serverDirectoryDiff = await operations.DirectoryDiffAsync(local, token);
    Assert(localDirectoryDiff.Contains("local.txt") && !localDirectoryDiff.Contains("remote.txt")
        && !localDirectoryDiff.Contains("child.txt"), "BASE directory comparison includes local changes and excludes remote-only changes");
    Assert(serverDirectoryDiff.Contains("remote.txt") && serverDirectoryDiff.Contains("child.txt"),
        "HEAD directory comparison includes remote-only changes");
    var remoteEntry = rootEntries.Single(e => e.Name == "remote.txt");
    Assert(remoteEntry.LocalRevision < remoteEntry.ServerRevision, "Local and server changed revisions are distinct");
    Assert(rootEntries.Single(e => e.Name == "equal.txt").State == FileState.Equal, "Unrelated repository revisions do not change equal files");
    Assert((await Read(local, "nested")).Single().Name == "child.txt", "Only immediate directory children are listed");
    Assert(rootEntries.Any(e => e.Name == "名字 @ 空格.txt" && e.State == FileState.Equal), "Unicode, spaces, and @ survive command/XML/path mapping");

    var beforeLock = (await repository.RefreshAsync(await repository.OpenWorkingCopyAsync(local, token), token)).Revision;
    await operations.LockAsync(Path.Combine(peer, "equal.txt"), "test lock", token);
    var locked = (await Read(local)).Single(e => e.Name == "equal.txt");
    Assert(!string.IsNullOrWhiteSpace(locked.Lock?.Owner), "Remote lock owner is visible");
    Assert(locked.Lock?.Comment == "test lock", "Lock comment is parsed");
    await operations.UnlockAsync(Path.Combine(peer, "equal.txt"), token);
    var afterLock = (await repository.RefreshAsync(await repository.OpenWorkingCopyAsync(local, token), token)).Revision;
    Assert(beforeLock == afterLock, "Lock changes are refreshed without a repository revision change");

    await operations.CheckoutAsync(url, sparse, true, token);
    await ExpectState(sparse, "remote.txt", FileState.NotDownloaded);
    progressEvents.Clear();
    await operations.UpdateAsync(Path.Combine(sparse, "remote.txt"), false, token);
    Assert(progressEvents.First().Stage == SvnProgressStage.Started
        && progressEvents.Last().Stage == SvnProgressStage.Completed
        && progressWasAvailableAtCompletion, "Update streams item notifications before command completion");
    await ExpectState(sparse, "remote.txt", FileState.Equal);
    Assert(!File.Exists(Path.Combine(sparse, "equal.txt")), "Single-file update does not fetch siblings");
    await operations.UpdateAsync(Path.Combine(sparse, "nested"), true, token);
    Assert(File.Exists(Path.Combine(sparse, "nested", "child.txt")), "Directory update downloads descendants");

    await operations.UpdateAsync(Path.Combine(local, "remote.txt"), false, token);
    await ExpectState(local, "remote.txt", FileState.Equal);
    await operations.UpdateAsync(Path.Combine(local, "conflict.txt"), false, token);
    await ExpectState(local, "conflict.txt", FileState.Conflict);
    await operations.ResolveAsync(Path.Combine(local, "conflict.txt"), "theirs-full", token);
    await ExpectState(local, "conflict.txt", FileState.Equal);

    var savedLocal = File.ReadAllText(Path.Combine(local, "both.txt"));
    var backup = await operations.ReplaceWithServerAsync(Path.Combine(local, "both.txt"), token);
    Assert(backup != null && File.ReadAllText(backup) == savedLocal, "Overwrite preserves original bytes in backup");
    await ExpectState(local, "both.txt", FileState.Equal);
    Assert(File.ReadAllText(Path.Combine(local, "both.txt")) == File.ReadAllText(Path.Combine(peer, "both.txt")), "Overwrite updates content and SVN metadata together");
    var unsupportedDestinationRejected = false;
    try { await operations.MoveAsync(Path.Combine(local, "名字 @ 空格.txt"), Path.Combine(local, "新名字 @ 空格.txt"), token); }
    catch (ArgumentException) { unsupportedDestinationRejected = true; }
    Assert(unsupportedDestinationRejected && File.Exists(Path.Combine(local, "名字 @ 空格.txt")), "Unsafe SVN destination normalization is rejected before mutation");
    await operations.MoveAsync(Path.Combine(local, "名字 @ 空格.txt"), Path.Combine(local, "新名字@ 空格.txt"), token);
    Assert(File.Exists(Path.Combine(local, "新名字@ 空格.txt")), "SVN move handles Unicode, spaces, and @");

    var online = await repository.OpenUrlAsync(url, token);
    var onlineSnapshot = await repository.RefreshAsync(online, token);
    Assert((await repository.ReadDirectoryAsync(online, onlineSnapshot, "", token)).All(e => e.State == FileState.Unchecked), "Online-only browsing never claims local equality");
    Assert((await repository.ReadDirectoryAsync(online, onlineSnapshot, "", token)).All(e => !e.HasDifferences),
        "Unassociated online directories are excluded from difference counts");
    progressEvents.Clear();
    var downloaded = await operations.DownloadForViewingAsync(RepositoryService.AppendUrl(url, "名字 @ 空格.txt"), "1", cache, token);
    Assert(File.ReadAllText(downloaded) == "line one\nline two\n", "Historical download preserves UTF-8 content");
    Assert(progressEvents.Last(progress => progress.Stage == SvnProgressStage.Transfer).BytesTransferred == new FileInfo(downloaded).Length
        && !progressEvents.Any(progress => progress.Stage == SvnProgressStage.Output), "Downloads report real byte counts without treating file contents as progress");
    var baseFile = await operations.DownloadBaseAsync(Path.Combine(local, "local.txt"), cache, token);
    var comparison = TextComparison.CompareFiles(baseFile, Path.Combine(local, "local.txt"));
    Assert(!comparison.Identical && comparison.Lines.Any(line => line.Changed), "Text comparison identifies local modifications");
    var identical = TextComparison.CompareFiles(baseFile, baseFile);
    Assert(identical.Identical && identical.Lines.All(line => !line.Changed), "Equal text produces no changed lines");
    var binaryLeft = Path.Combine(testRoot, "left.bin");
    var binaryRight = Path.Combine(testRoot, "right.bin");
    File.WriteAllBytes(binaryLeft, [0, 1, 2]); File.WriteAllBytes(binaryRight, [0, 1, 3]);
    Assert(TextComparison.CompareFiles(binaryLeft, binaryRight) is { IsBinary: true, Identical: false }, "Binary files use content comparison without text decoding");
    Assert((await operations.LogAsync(url, token)).Count >= 2, "Repository history loads");
    await operations.SetPropertyAsync(Path.Combine(local, "equal.txt"), "svn:needs-lock", "*", token);
    await ExpectState(local, "equal.txt", FileState.LocalModified);
    await operations.RevertAsync([Path.Combine(local, "equal.txt")], token);
    await ExpectState(local, "equal.txt", FileState.Equal);
    await operations.SetPropertyAsync(local, "svn:ignore", "*.ignored\n", token);
    File.WriteAllText(Path.Combine(local, "example.ignored"), "ignored");
    var ignoredEntry = (await Read(local)).Single(e => e.Name == "example.ignored");
    Assert(ignoredEntry.State == FileState.Ignored && !ignoredEntry.HasDifferences,
        "svn:ignore patterns remain literal and ignored files stay visible without counting as differences");
    Directory.CreateDirectory(Path.Combine(local, "local-only"));
    File.WriteAllText(Path.Combine(local, "local-only", "child.txt"), "local child");
    Assert((await Read(local, "local-only")).Single().State == FileState.LocalAdded, "Unversioned directories browse locally without requesting nonexistent server paths");
    var exported = Path.Combine(testRoot, "exported.txt");
    await operations.ExportAsync(RepositoryService.AppendUrl(url, "equal.txt"), exported, token);
    Assert(File.Exists(exported), "Export creates the exact destination file");

    var exporter = new RepositoryExporter(operations);
    var exportSelection = rootEntries.Where(entry => entry.Name is "nested" or "remote.txt" or "名字 @ 空格.txt").ToList();
    var batchDestination = Path.Combine(testRoot, "导出 @ 文件夹");
    Directory.CreateDirectory(batchDestination);
    var startedItems = new List<int>();
    var completedItems = new List<string>();
    await exporter.ExportAsync(exportSelection, batchDestination, "1", token,
        (index, count, _) => { if (count == 3) startedItems.Add(index); }, item => completedItems.Add(item.Entry.Name));
    Assert(exportSelection.All(entry => File.Exists(Path.Combine(batchDestination, entry.Name))
        || Directory.Exists(Path.Combine(batchDestination, entry.Name))), "Batch export handles mixed files/directories and preserves Unicode, spaces, and @");
    Assert(File.ReadAllText(Path.Combine(batchDestination, "remote.txt")) == "line one\nline two\n"
        && File.ReadAllText(Path.Combine(batchDestination, "nested", "child.txt")) == "child\n",
        "Batch export pins every item to the requested repository snapshot");
    Assert(!Directory.Exists(Path.Combine(batchDestination, "nested", ".svn")), "Exported directories retain structure without working-copy metadata");
    Assert(startedItems.SequenceEqual([1, 2, 3]) && completedItems.Count == 3, "Batch export reports per-item progress and completion");
    Assert(!Directory.EnumerateDirectories(batchDestination, ".svnclient-export-*").Any(), "Successful export removes its staging directories");
    var collidingDestination = Path.Combine(testRoot, "export-collision");
    Directory.CreateDirectory(collidingDestination);
    var existingExport = Path.Combine(collidingDestination, "名字 @ 空格.txt");
    File.WriteAllText(existingExport, "keep existing content");
    var collisionRejected = false;
    try { await exporter.ExportAsync(exportSelection, collidingDestination, "1", token); }
    catch (IOException) { collisionRejected = true; }
    Assert(collisionRejected && File.ReadAllText(existingExport) == "keep existing content"
        && Directory.EnumerateFileSystemEntries(collidingDestination).Count() == 1,
        "Export rejects all destination collisions before downloading or overwriting anything");
    var invalidExportRejected = false;
    try { RepositoryExporter.CreatePlan([rootEntries.Single(entry => entry.Name == "new.txt")], collidingDestination); }
    catch (ArgumentException) { invalidExportRejected = true; }
    Assert(invalidExportRejected, "Export rejects local-only items without a server version");
    var cancelledDestination = Path.Combine(testRoot, "export-cancelled");
    Directory.CreateDirectory(cancelledDestination);
    using (var cancellation = new CancellationTokenSource())
    {
        var wasCancelled = false;
        try
        {
            await exporter.ExportAsync(exportSelection, cancelledDestination, "1", cancellation.Token,
                itemCompleted: _ => cancellation.Cancel());
        }
        catch (OperationCanceledException) { wasCancelled = true; }
        Assert(wasCancelled && Directory.EnumerateFileSystemEntries(cancelledDestination).Count() == 1
            && Directory.Exists(Path.Combine(cancelledDestination, exportSelection[0].Name)),
            "Cancelling batch export retains completed items and stops before the next item");
    }
    var interruptedDestination = Path.Combine(testRoot, "export-interrupted");
    Directory.CreateDirectory(interruptedDestination);
    using (var cancellation = new CancellationTokenSource())
    {
        void CancelExport(SvnProgress progress) { if (progress.Command == "export" && progress.Stage == SvnProgressStage.Started) cancellation.Cancel(); }
        runner.ProgressChanged += CancelExport;
        var wasCancelled = false;
        try { await exporter.ExportAsync(exportSelection, interruptedDestination, "1", cancellation.Token); }
        catch (OperationCanceledException) { wasCancelled = true; }
        finally { runner.ProgressChanged -= CancelExport; }
        Assert(wasCancelled && !Directory.EnumerateFileSystemEntries(interruptedDestination).Any(),
            "An interrupted export does not publish incomplete files and cleans its private staging directory");
    }

    var legacyProfile = Path.Combine(testRoot, "legacy-svn-profile");
    var portableProfile = Path.Combine(testRoot, "portable-svn-profile");
    Directory.CreateDirectory(Path.Combine(legacyProfile, "auth", "fixture"));
    File.WriteAllText(Path.Combine(legacyProfile, "config"), "[miscellany]\n");
    File.WriteAllText(Path.Combine(legacyProfile, "auth", "fixture", "marker"), "isolated test profile");
    using (var portableRunner = new SvnRunner { ConfigurationDirectory = portableProfile, ConfigurationTemplateDirectory = legacyProfile })
    {
        await portableRunner.RunAsync(["info", "--xml", SvnRunner.Target(url)], token);
        Assert(File.Exists(Path.Combine(portableProfile, "config"))
            && File.ReadAllText(Path.Combine(portableProfile, "auth", "fixture", "marker")) == "isolated test profile",
            "An isolated SVN configuration directory imports the previous profile once");
        File.WriteAllText(Path.Combine(legacyProfile, "auth", "fixture", "marker"), "changed legacy profile");
        await portableRunner.RunAsync(["info", "--xml", SvnRunner.Target(url)], token);
        Assert(File.ReadAllText(Path.Combine(portableProfile, "auth", "fixture", "marker")) == "isolated test profile",
            "Later commands preserve portable SVN configuration instead of overwriting it from the legacy profile");
    }
    var nestedProfile = Path.Combine(legacyProfile, "portable-child");
    using (var nestedRunner = new SvnRunner { ConfigurationDirectory = nestedProfile, ConfigurationTemplateDirectory = legacyProfile })
    {
        await nestedRunner.RunAsync(["info", "--xml", SvnRunner.Target(url)], token);
        Assert(File.Exists(Path.Combine(nestedProfile, "auth", "fixture", "marker"))
            && !Directory.Exists(Path.Combine(nestedProfile, "portable-child")),
            "Profile migration excludes its destination when the application is inside the previous profile directory");
    }
    await operations.CopyBranchAsync(RepositoryService.AppendUrl(url, "nested"), RepositoryService.AppendUrl(url, "branch"), "Create branch", token);
    Assert((await Read(local)).Any(e => e.Name == "branch" && e.State == FileState.NotDownloaded), "New server directories appear in the local browser");
    var branchWorkingCopy = Path.Combine(testRoot, "branch-working-copy");
    await operations.CheckoutAsync(RepositoryService.AppendUrl(url, "nested"), branchWorkingCopy, false, token);
    await operations.SwitchAsync(RepositoryService.AppendUrl(url, "branch"), branchWorkingCopy, token);
    Assert((await repository.OpenWorkingCopyAsync(branchWorkingCopy, token)).Url.EndsWith("/branch"), "Branch switch updates the associated repository URL");
    var childBeforePreview = File.ReadAllText(Path.Combine(branchWorkingCopy, "child.txt"));
    File.AppendAllText(Path.Combine(peer, "nested", "child.txt"), "merge source change\n");
    await operations.CommitAsync([Path.Combine(peer, "nested", "child.txt")], "Change merge source", token);
    await operations.MergeAsync(RepositoryService.AppendUrl(url, "nested"), branchWorkingCopy, true, token);
    Assert(File.ReadAllText(Path.Combine(branchWorkingCopy, "child.txt")) == childBeforePreview, "Merge preview does not modify working files");
    await operations.MergeAsync(RepositoryService.AppendUrl(url, "nested"), branchWorkingCopy, false, token);
    Assert(File.ReadAllText(Path.Combine(branchWorkingCopy, "child.txt")).Contains("merge source change"), "Merge applies source changes to working files");
    await ExpectState(branchWorkingCopy, "child.txt", FileState.LocalModified);
    await operations.CleanupAsync(local, token);
    Assert((await repository.OpenWorkingCopyAsync(local, token)).LocalRoot == local, "Cleanup supports Unicode and @ in the working-copy root");
    await operations.CommitAsync([Path.Combine(local, "名字 @ 空格.txt"), Path.Combine(local, "新名字@ 空格.txt")], "*\n中文提交说明", token);
    Assert((await operations.LogAsync(url, token)).First().Message == "*\n中文提交说明", "Commit messages preserve Unicode, newlines, and literal wildcards");
    var renamedHistory = await operations.DownloadForViewingAsync(RepositoryService.AppendUrl(url, "新名字@ 空格.txt"), "1", cache, token, "HEAD");
    Assert(File.ReadAllText(renamedHistory) == "line one\nline two\n", "Historical comparison follows a renamed file through its peg revision");

    var largeFile = Path.Combine(peer, "progress-large.bin");
    var largeContent = new byte[2 * 1024 * 1024];
    Random.Shared.NextBytes(largeContent);
    File.WriteAllBytes(largeFile, largeContent);
    await operations.AddAsync([largeFile], token);
    await operations.CommitAsync([largeFile], "Large binary progress fixture", token);
    progressEvents.Clear();
    var largeDownload = await operations.DownloadForViewingAsync(RepositoryService.AppendUrl(url, "progress-large.bin"), "HEAD", cache, token);
    var transfers = progressEvents.Where(progress => progress.Stage == SvnProgressStage.Transfer).Select(progress => progress.BytesTransferred).ToList();
    Assert(transfers.Count >= 2 && transfers.First() == 0 && transfers.Last() == largeContent.Length
        && transfers.SequenceEqual(transfers.Order()), "Large download byte notifications increase monotonically to the exact size");
    Assert(File.ReadAllBytes(largeDownload).SequenceEqual(largeContent), "Streaming a binary download preserves every byte");
    progressEvents.Clear();
    await repository.OpenUrlAsync(url, token);
    Assert(!progressEvents.Any(progress => progress.Stage == SvnProgressStage.Output), "XML queries are not exposed as progress text");

    using (var cancellation = new CancellationTokenSource())
    {
        void CancelStartedCommand(SvnProgress progress) { if (progress.Stage == SvnProgressStage.Started) cancellation.Cancel(); }
        runner.ProgressChanged += CancelStartedCommand;
        var cancelled = false;
        try { await operations.UpdateAsync(sparse, true, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        finally { runner.ProgressChanged -= CancelStartedCommand; }
        Assert(cancelled, "Progress reporting retains manual cancellation of a running SVN process");
    }

    var traversalRejected = false;
    try { RepositoryService.GetLocalPath(await repository.OpenWorkingCopyAsync(local, token), "../escape"); }
    catch (InvalidOperationException) { traversalRejected = true; }
    Assert(traversalRejected, "Path mapping rejects traversal outside working copy");
    var portableData = Path.Combine(testRoot, "portable-application");
    var portableOperations = new SvnOperations(runner, Path.Combine(portableData, "Temp"), Path.Combine(portableData, "Backups"));
    var originalLocal = File.ReadAllText(Path.Combine(local, "local.txt"));
    var portableBackup = await portableOperations.ReplaceWithServerAsync(Path.Combine(local, "local.txt"), token);
    Assert(portableBackup != null && RepositoryService.IsDescendant(portableBackup, Path.Combine(portableData, "Backups"))
        && File.ReadAllText(portableBackup) == originalLocal, "Replacement backups are stored under the portable application directory");
    await portableOperations.SetPropertyAsync(Path.Combine(local, "local.txt"), "svn:needs-lock", "*", token);
    Assert(Directory.Exists(Path.Combine(portableData, "Temp")) && !Directory.EnumerateFiles(Path.Combine(portableData, "Temp")).Any(),
        "SVN command text files use the portable temporary directory and are removed after the command");
    await SvnExecutableTests.RunAsync(Path.Combine(testRoot, "bundled-svn"), Assert);
    await EmbeddedSvnTests.RunAsync(Path.Combine(testRoot, "embedded-svn"), Assert);
    await ScopedRefreshTests.RunAsync(Path.Combine(testRoot, "scoped-refresh"), Assert);
    await RevisionOperationsTests.RunAsync(Path.Combine(testRoot, "revisions"), Assert);
    await CommitSelectionTests.RunAsync(Path.Combine(testRoot, "commit-selection"), Assert);
    await LocalCreationTests.RunAsync(Path.Combine(testRoot, "local-creation"), Assert);
    await LocalDeletionTests.RunAsync(Path.Combine(testRoot, "local-deletion"), Assert);
    await OfficeDocumentTests.RunAsync(Path.Combine(testRoot, "office-documents"), Assert);
    await BackupManagerTests.RunAsync(Path.Combine(testRoot, "backup-management"), Assert);
    await EmbeddedWinMergeTests.RunAsync(Path.Combine(testRoot, "embedded-winmerge"), Assert);
    Console.WriteLine($"\nAll {assertions} assertions passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
