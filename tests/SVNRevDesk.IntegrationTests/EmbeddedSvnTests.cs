using SVNRevDesk.Core.Svn;

internal static class EmbeddedSvnTests
{
    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var fresh = Path.Combine(root, "fresh application 中文");
        var client = await SvnExecutable.ResolveAsync("svn.exe", fresh);
        var package = Path.Combine(fresh, "Tools", "Svn");
        assert(client == Path.Combine(package, "bin", "svn.exe") && File.Exists(client)
            && File.Exists(Path.Combine(package, "bin", "msvcp140.dll"))
            && File.Exists(Path.Combine(package, "Licenses", "Subversion-License.txt")),
            "Embedded client extracts its EXE, native dependencies and licenses into Tools/Svn beside a fresh application");
        assert(!Directory.Exists(Path.Combine(fresh, "Svn")), "Fresh installation does not create the legacy SVN directory");
        using var runner = new SvnRunner { Executable = client };
        assert((await runner.RunAsync(["--version", "--quiet"])).StartsWith("1.14.5"),
            "A freshly extracted embedded SVN client really starts");

        var userNote = Path.Combine(package, "user-note.txt");
        File.WriteAllText(userNote, "preserve any existing extra file");
        var executableTime = File.GetLastWriteTimeUtc(client);
        await SvnExecutable.ResolveAsync("svn.exe", fresh);
        assert(File.GetLastWriteTimeUtc(client) == executableTime && File.Exists(userNote)
            && !Directory.Exists(Path.Combine(fresh, "Backups")),
            "A complete client is reused without rewriting binaries or touching extra files");

        var missingLibrary = Path.Combine(package, "bin", "SlikSvn-libssl-3-x64.dll");
        File.Delete(missingLibrary);
        await SvnExecutable.ResolveAsync("svn.exe", fresh);
        assert(File.Exists(missingLibrary) && Directory.EnumerateFiles(Path.Combine(fresh, "Backups"), "user-note.txt", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path) == "preserve any existing extra file"),
            "Missing DLLs trigger complete repair while retaining the previous client directory in Backups");
        assert(!Directory.EnumerateDirectories(fresh, ".svn-extract-*").Any() && !File.Exists(Path.Combine(fresh, ".svn-install.lock")),
            "Successful extraction and repair clean their staging directories and installation lock");

        var cancelledDirectory = Path.Combine(root, "cancelled application");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await SvnExecutable.ResolveAsync("svn.exe", cancelledDirectory, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && !Directory.Exists(cancelledDirectory),
                "Cancelled installation never creates or replaces application files");
        }

        var concurrentDirectory = Path.Combine(root, "concurrent application");
        var paths = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => SvnExecutable.ResolveAsync("svn.exe", concurrentDirectory)));
        assert(paths.Distinct().Count() == 1 && File.Exists(paths[0])
            && !Directory.EnumerateDirectories(concurrentDirectory, ".svn-extract-*").Any(),
            "Concurrent runners share one complete installation rather than overlapping extraction");

        var customDirectory = Path.Combine(root, "custom client application");
        Directory.CreateDirectory(customDirectory);
        var customClient = Path.Combine(customDirectory, "custom-svn.exe");
        File.Copy(client, customClient);
        assert(await SvnExecutable.ResolveAsync(customClient, customDirectory) == customClient
            && !Directory.Exists(Path.Combine(customDirectory, "Tools")),
            "A valid custom SVN path bypasses embedded-client extraction");

        var blockedDirectory = Path.Combine(root, "blocked application");
        Directory.CreateDirectory(Path.Combine(blockedDirectory, "Tools"));
        var blockedPath = Path.Combine(blockedDirectory, "Tools", "Svn");
        File.WriteAllText(blockedPath, "existing file must survive");
        var blocked = false;
        try { await SvnExecutable.ResolveAsync("svn.exe", blockedDirectory); }
        catch (InvalidOperationException) { blocked = true; }
        assert(blocked && File.ReadAllText(blockedPath) == "existing file must survive"
            && !Directory.EnumerateDirectories(blockedDirectory, ".svn-extract-*").Any(),
            "Installation failure preserves existing content and cleans staged files");

        await TestLegacyMigrationAsync(root, package, assert);
    }

    private static async Task TestLegacyMigrationAsync(string root, string package, Action<bool, string> assert)
    {
        void CopyPackage(string destination)
        {
            foreach (var source in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(package, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            File.WriteAllText(Path.Combine(destination, "user-note.txt"), "retain legacy content");
        }

        var application = Path.Combine(root, "legacy application 中文");
        var legacy = Path.Combine(application, "Svn");
        var target = Path.Combine(application, "Tools", "Svn");
        CopyPackage(legacy);
        var modified = File.GetLastWriteTimeUtc(Path.Combine(legacy, "bin", "svn.exe"));
        var client = await SvnExecutable.ResolveAsync("svn.exe", application);
        assert(client == Path.Combine(target, "bin", "svn.exe") && !Directory.Exists(legacy)
            && File.ReadAllText(Path.Combine(target, "user-note.txt")) == "retain legacy content"
            && File.GetLastWriteTimeUtc(client) == modified && !Directory.Exists(Path.Combine(application, "Backups")),
            "An intact legacy installation moves to Tools/Svn without rewriting binaries or discarding extra files");
        using var runner = new SvnRunner { Executable = client };
        assert((await runner.RunAsync(["--version", "--quiet"])).StartsWith("1.14.5"),
            "The SVN client starts with its dependencies after legacy directory migration");

        application = Path.Combine(root, "incomplete legacy application");
        legacy = Path.Combine(application, "Svn");
        CopyPackage(legacy);
        File.Delete(Path.Combine(legacy, "bin", "SlikSvn-libssl-3-x64.dll"));
        client = await SvnExecutable.ResolveAsync("svn.exe", application);
        assert(!Directory.Exists(legacy) && File.Exists(Path.Combine(Path.GetDirectoryName(client)!, "SlikSvn-libssl-3-x64.dll"))
            && Directory.EnumerateFiles(Path.Combine(application, "Backups"), "user-note.txt", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path) == "retain legacy content"),
            "An incomplete legacy client is repaired after migration with all previous content retained in Backups");

        application = Path.Combine(root, "two client locations");
        legacy = Path.Combine(application, "Svn");
        target = Path.Combine(application, "Tools", "Svn");
        CopyPackage(legacy);
        CopyPackage(target);
        File.WriteAllText(Path.Combine(target, "user-note.txt"), "retain target content");
        client = await SvnExecutable.ResolveAsync("svn.exe", application);
        assert(client == Path.Combine(target, "bin", "svn.exe")
            && File.ReadAllText(Path.Combine(legacy, "user-note.txt")) == "retain legacy content"
            && File.ReadAllText(Path.Combine(target, "user-note.txt")) == "retain target content"
            && !Directory.Exists(Path.Combine(application, "Backups")),
            "When both locations exist the new client is used and neither directory is overwritten");
        assert(await SvnExecutable.ResolveAsync(Path.Combine(legacy, "bin", "svn.exe"), application)
            == Path.Combine(legacy, "bin", "svn.exe"), "An explicitly configured legacy client retains custom-path priority");

        application = Path.Combine(root, "cancelled legacy migration");
        legacy = Path.Combine(application, "Svn");
        CopyPackage(legacy);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await SvnExecutable.ResolveAsync("svn.exe", application, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && File.ReadAllText(Path.Combine(legacy, "user-note.txt")) == "retain legacy content"
                && !Directory.Exists(Path.Combine(application, "Tools")),
                "Cancelling before migration preserves the legacy directory without creating Tools");
        }

        application = Path.Combine(root, "blocked legacy migration");
        legacy = Path.Combine(application, "Svn");
        CopyPackage(legacy);
        File.WriteAllText(Path.Combine(application, "Tools"), "retain blocking file");
        var blocked = false;
        try { await SvnExecutable.ResolveAsync("svn.exe", application); }
        catch (InvalidOperationException) { blocked = true; }
        assert(blocked && File.ReadAllText(Path.Combine(legacy, "user-note.txt")) == "retain legacy content"
            && File.ReadAllText(Path.Combine(application, "Tools")) == "retain blocking file",
            "A blocked migration preserves the complete old client and the file blocking the new directory");

        application = Path.Combine(root, "unrelated Svn folder");
        legacy = Path.Combine(application, "Svn");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "keep.txt"), "unrelated data");
        await SvnExecutable.ResolveAsync("svn.exe", application);
        assert(File.ReadAllText(Path.Combine(legacy, "keep.txt")) == "unrelated data",
            "A folder named Svn without an old tool installation is left untouched");
    }
}
