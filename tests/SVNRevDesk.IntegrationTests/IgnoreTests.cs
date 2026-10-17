using System.Diagnostics;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class IgnoreTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        Directory.CreateDirectory(fixture);
        var repositoryPath = Path.Combine(fixture, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create"); start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start)!)
        {
            var error = admin.StandardError.ReadToEndAsync(); await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var token = CancellationToken.None;
        using var runner = new SvnRunner();
        var store = new LocalIgnoreStore(Path.Combine(fixture, "user-a", "local-ignores.json"));
        var operations = new SvnOperations(runner, Path.Combine(fixture, "Temp"), localIgnores: store);
        var local = Path.Combine(fixture, "中文 @ 工作副本");
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await operations.CheckoutAsync(url, local, false, token);
        var tracked = Path.Combine(local, "tracked.txt"); File.WriteAllText(tracked, "tracked");
        var other = Path.Combine(local, "other"); Directory.CreateDirectory(other);
        await operations.AddAsync([tracked, other], token);
        await operations.SetPropertyAsync(local, "svn:ignore", "*.team\n", token);
        await runner.RunAsync(["propset", "custom:keep", "preserve", SvnRunner.Target(local)], token);
        await operations.CommitAsync([local], "fixture", token);
        var properties = await operations.PropertiesAsync(local, token);
        var commands = new List<string>();
        runner.ProgressChanged += progress => { if (progress.Stage == SvnProgressStage.Started) commands.Add(progress.Command); };
        var name = "中文 @ [1].txt";
        var file = Path.Combine(local, name); File.WriteAllText(file, "keep exact contents");
        var similar = Path.Combine(local, "中文 @ 1.txt"); File.WriteAllText(similar, "similar");
        var sameName = Path.Combine(other, name); File.WriteAllText(sameName, "other directory");
        var staleSelection = await operations.ReadCommitChangesAsync([file], local, token);
        commands.Clear();
        await operations.SetIgnoredAsync(file, local, true, token);
        check(!commands.Any(command => command is "propset" or "propdel" or "commit"), "Ignoring only reads SVN and never changes properties or commits");
        check(await Item(file) == "unversioned" && File.ReadAllText(file) == "keep exact contents", "Local ignore leaves actual SVN status and file bytes unchanged");
        check(store.Matches(file) && !store.Matches(similar) && !store.Matches(sameName), "Chinese, spaces, @ and brackets match only the exact absolute path");
        check(File.Exists(store.ConfigurationPath) && !RepositoryService.IsDescendant(store.ConfigurationPath, local), "Rules are persisted outside the working copy");
        check(await operations.PropertiesAsync(local, token) == properties, "Existing shared ignore and custom directory properties are byte-for-byte unchanged");
        var originalRules = File.ReadAllText(store.ConfigurationPath);
        await operations.SetIgnoredAsync(file, local, true, token);
        check(File.ReadAllText(store.ConfigurationPath) == originalRules, "Repeated ignore is idempotent");
        var service = new LocalIgnoreRepositoryService(new RepositoryService(runner), store);
        var session = await service.OpenWorkingCopyAsync(local, token);
        var snapshot = await service.RefreshAsync(session, token);
        var entries = await service.ReadDirectoryAsync(session, snapshot, "", token);
        check(entries.Single(entry => entry.LocalPath == file) is { State: FileState.Ignored, IsLocallyIgnored: true, HasDifferences: false }, "Listings retain locally ignored files with their ignored badge and no difference count");
        var changes = await operations.ReadCommitChangesAsync([local], local, token);
        check(!changes.Any(change => change.Path == file) && changes.Any(change => change.Path == similar) && changes.Any(change => change.Path == sameName), "Commit discovery excludes exactly the locally ignored file");
        check((await operations.ReadCommitChangesAsync([file], local, token)).Count == 0, "Selecting an ignored file directly cannot create commit candidates");
        await Rejected(() => operations.CommitSelectedAsync(staleSelection, "must fail", local, token), "A stale commit selection is rejected after its file is locally ignored");
        var restarted = new LocalIgnoreStore(store.ConfigurationPath);
        check(restarted.Matches(file), "Rules survive restarting with the same machine-local configuration");
        var otherStore = new LocalIgnoreStore(Path.Combine(fixture, "user-b", "local-ignores.json"));
        var independentEntries = otherStore.Apply(otherStore.Apply(snapshot), entries);
        check(independentEntries.Single(entry => entry.LocalPath == file) is { State: FileState.LocalAdded, IsLocallyIgnored: false },
            "A cache from another user cannot carry its local ignore state into an independent configuration");
        var otherOperations = new SvnOperations(runner, localIgnores: otherStore);
        check((await otherOperations.ReadCommitChangesAsync([file], local, token)).Any(change => change.Path == file), "Another user/computer configuration is unaffected even at the same working-copy path");
        var peer = Path.Combine(fixture, "peer"); await operations.CheckoutAsync(url, peer, false, token);
        var peerFile = Path.Combine(peer, name); File.WriteAllText(peerFile, "peer");
        check((await operations.ReadCommitChangesAsync([peerFile], peer, token)).Any(change => change.Path == peerFile), "Another checkout of the same repository is unaffected");
        await operations.SetIgnoredAsync(file, local, false, token);
        check(!store.Matches(file) && (await operations.ReadCommitChangesAsync([file], local, token)).Any(change => change.Path == file), "Unignore removes only local configuration and restores commit discovery");

        var folder = Path.Combine(local, "缓存 @ [目录]"); Directory.CreateDirectory(Path.Combine(folder, "child"));
        var child = Path.Combine(folder, "child", "keep.txt"); File.WriteAllText(child, "child contents");
        await operations.SetIgnoredAsync(folder, local, true, token);
        snapshot = await service.RefreshAsync(session, token);
        var children = await service.ReadDirectoryAsync(session, snapshot, Path.GetFileName(folder), token);
        check(children.Single() is { State: FileState.Ignored, IsLocallyIgnored: true }, "Ignoring a directory marks descendants locally without removing them");
        check(!(await operations.ReadCommitChangesAsync([local], local, token)).Any(change => change.Path == folder || RepositoryService.IsDescendant(change.Path, folder)), "Ignored directory descendants cannot enter commit candidates");
        await Rejected(() => operations.SetIgnoredAsync(child, local, false, token), "Inherited local ignore must be cancelled at the ancestor rather than silently changing siblings");
        await operations.SetIgnoredAsync(folder, local, false, token);
        check((await operations.ReadCommitChangesAsync([folder], local, token)).Any(change => change.Path == child), "Unignore rediscovers directory descendants");
        var shared = Path.Combine(local, "file.team"); File.WriteAllText(shared, "shared ignore");
        await Rejected(() => operations.SetIgnoredAsync(shared, local, false, token), "Local unignore never removes a shared SVN ignore rule");

        var newParent = Path.Combine(local, "new-parent"); Directory.CreateDirectory(newParent);
        var privateFile = Path.Combine(newParent, "private.txt"); File.WriteAllText(privateFile, "private");
        var publicFile = Path.Combine(newParent, "public.txt"); File.WriteAllText(publicFile, "public");
        await operations.SetIgnoredAsync(privateFile, local, true, token);
        check((await operations.ReadCommitChangesAsync([privateFile], local, token)).Count == 0, "A file inside an unversioned parent supports local ignore without adding its parent");
        var mixed = await operations.ReadCommitChangesAsync([privateFile, similar], local, token);
        check(mixed.Any(change => change.Path == similar) && !mixed.Any(change => change.Path == newParent || change.Path == privateFile),
            "Mixed commit scopes do not leak an ignored child's unversioned parent into the selection");
        await operations.AddAsync([newParent], token);
        check(await Item(privateFile) == "unversioned" && await Item(publicFile) == "added", "Adding a whole directory skips locally ignored descendants");
        await Rejected(() => operations.AddAsync([privateFile], token), "Explicit Add cannot bypass a local ignore");
        var selected = await operations.ReadCommitChangesAsync([newParent], local, token);
        await operations.CommitSelectedAsync(selected, "only public content", local, token);
        var listing = SvnXml.ReadList(await runner.RunAsync(["list", "--xml", RepositoryService.AppendUrl(url, "new-parent")], token));
        check(listing.Count == 1 && listing[0].Name == "public.txt", "A real server commit contains the selected public file and none of the ignored content");
        await operations.UpdateAsync(peer, true, token);
        check(File.Exists(Path.Combine(peer, "new-parent", "public.txt")) && !File.Exists(Path.Combine(peer, "new-parent", "private.txt"))
            && !Directory.EnumerateFiles(peer, "local-ignores.json", SearchOption.TopDirectoryOnly).Any(), "Another checkout receives public content but no ignored file or local configuration");
        check(await operations.PropertiesAsync(local, token) == properties, "Ignore, unignore, Add and selected commit preserve the original SVN ignore properties");
        await Rejected(() => operations.SetIgnoredAsync(tracked, local, true, token), "Tracked files cannot be locally ignored");
        await Rejected(() => operations.SetIgnoredAsync(local, local, true, token), "The opened root is protected");
        var outside = Path.Combine(fixture, "outside.txt"); File.WriteAllText(outside, "outside");
        await Rejected(() => operations.SetIgnoredAsync(outside, local, true, token), "Out-of-scope paths are protected");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var beforeCancel = File.ReadAllText(store.ConfigurationPath);
        try { await operations.SetIgnoredAsync(file, local, true, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { check(File.ReadAllText(store.ConfigurationPath) == beforeCancel, "Cancellation before saving leaves local rules unchanged"); }
        restarted.Set(file, true); store.Set(similar, true);
        var concurrent = new LocalIgnoreStore(store.ConfigurationPath);
        check(concurrent.Matches(file) && concurrent.Matches(similar), "Independent application instances merge local changes without losing each other's rules");
        check(!concurrent.Matches(file + ".other"), "Local paths are matched at item boundaries rather than by a name prefix");
        await otherOperations.AddAsync([file], token);
        await otherOperations.CommitAsync([file], "external client adds formerly ignored path", token);
        File.AppendAllText(file, " tracked edit");
        snapshot = await service.RefreshAsync(session, token);
        entries = await service.ReadDirectoryAsync(session, snapshot, "", token);
        check(entries.Single(entry => entry.LocalPath == file) is { State: FileState.LocalModified, IsLocallyIgnored: false }
            && (await operations.ReadCommitChangesAsync([file], local, token)).Any(change => change.Path == file),
            "A retained local rule never conceals tracked changes after another client adds the path");

        async Task<string?> Item(string path) => SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--no-ignore", "--depth", "empty", SvnRunner.Target(path)], token)).SingleOrDefault()?.Item;
        async Task Rejected(Func<Task> action, string description)
        {
            try { await action(); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { check(true, description); return; }
            throw new InvalidOperationException("Expected rejection: " + description);
        }
    }
}
