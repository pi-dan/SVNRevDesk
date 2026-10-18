using System.Diagnostics;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

// SlikSVN's Windows command-line arguments use the system ANSI code page.
// Keep this real-repository suite portable to English Windows hosted runners;
// the full default suite retains its Chinese-path coverage for compatible hosts.
internal static class PortableCiTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        var repositoryPath = Path.Combine(root, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create"); start.ArgumentList.Add(repositoryPath);
        using (var process = Process.Start(start)!)
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var local = Path.Combine(root, "working-copy");
        var peer = Path.Combine(root, "peer");
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner, Path.Combine(root, "Temp"), Path.Combine(root, "Backups"));
        var repository = new RepositoryService(runner);
        var token = CancellationToken.None;
        await operations.CheckoutAsync(url, local, false, token);
        foreach (var name in new[] { "equal.txt", "remote.txt", "local.txt", "both.txt", "missing.txt", "deleted.txt", "space @ name.txt" })
            File.WriteAllText(Path.Combine(local, name), "original\n");
        Directory.CreateDirectory(Path.Combine(local, "nested"));
        File.WriteAllText(Path.Combine(local, "nested", "child.txt"), "child\n");
        await operations.AddAsync(Directory.EnumerateFileSystemEntries(local).Where(path => Path.GetFileName(path) != ".svn"), token);
        await operations.CommitAsync([local], "Initial portable CI fixture", token);
        await operations.CheckoutAsync(url, peer, false, token);
        File.AppendAllText(Path.Combine(local, "local.txt"), "local edit\n");
        File.AppendAllText(Path.Combine(local, "both.txt"), "local edit\n");
        File.Delete(Path.Combine(local, "missing.txt"));
        File.WriteAllText(Path.Combine(local, "new.txt"), "unversioned\n");
        File.AppendAllText(Path.Combine(peer, "remote.txt"), "remote edit\n");
        File.AppendAllText(Path.Combine(peer, "both.txt"), "remote edit\n");
        await operations.DeleteAsync([Path.Combine(peer, "deleted.txt")], token);
        await operations.CommitAsync([peer], "Server changes", token);

        async Task<IReadOnlyList<BrowserEntry>> Read()
        {
            var session = await repository.OpenWorkingCopyAsync(local, token);
            return await repository.ReadDirectoryAsync(session, await repository.RefreshAsync(session, token), "", token);
        }
        var entries = await Read();
        foreach (var (name, state) in new[] {
            ("equal.txt", FileState.Equal), ("remote.txt", FileState.ServerChanged),
            ("local.txt", FileState.LocalModified), ("both.txt", FileState.BothChanged),
            ("missing.txt", FileState.Missing), ("new.txt", FileState.LocalAdded),
            ("deleted.txt", FileState.ServerDeleted), ("space @ name.txt", FileState.Equal) })
            check(entries.Single(entry => entry.Name == name).State == state, $"Real repository state: {name} = {state}");

        await operations.UpdateAsync(Path.Combine(local, "remote.txt"), false, token);
        check(File.ReadAllText(Path.Combine(local, "remote.txt")).Contains("remote edit"), "Single-file update obtains the server content");
        check((await Read()).Single(entry => entry.Name == "local.txt").State == FileState.LocalModified, "Updating one file preserves unrelated local changes");
        var changes = await operations.ReadCommitChangesAsync([local], local, token);
        await operations.CommitSelectedAsync([changes.Single(change => change.Path == Path.Combine(local, "local.txt"))], "Selected change", local, token);
        entries = await Read();
        check(entries.Single(entry => entry.Name == "local.txt").State == FileState.Equal, "Selected commit publishes the chosen modification");
        check(entries.Single(entry => entry.Name == "both.txt").State == FileState.BothChanged, "Selected commit leaves unchecked changes untouched");
        var original = File.ReadAllText(Path.Combine(local, "both.txt"));
        var backup = await operations.ReplaceWithServerAsync(Path.Combine(local, "both.txt"), token);
        check(backup != null && File.ReadAllText(backup) == original, "Server replacement preserves the local content in a backup");
        await operations.MoveAsync(Path.Combine(local, "equal.txt"), Path.Combine(local, "renamed.txt"), token);
        check((await operations.ReadLocalDirectoryStatusAsync(local, local, token)).Any(status => status.Copied && status.Path.EndsWith("renamed.txt")), "SVN move retains copy history");
        var export = Path.Combine(root, "export");
        await operations.ExportAsync(RepositoryService.AppendUrl(url, "nested"), export, token);
        check(File.Exists(Path.Combine(export, "child.txt")) && !Directory.Exists(Path.Combine(export, ".svn")), "Directory export includes content without working-copy metadata");
        var ignores = new LocalIgnoreStore(Path.Combine(root, "local-ignores.json"));
        ignores.Set(Path.Combine(local, "new.txt"), true);
        var filtered = new LocalIgnoreRepositoryService(repository, ignores);
        var session = await filtered.OpenWorkingCopyAsync(local, token);
        var ignored = (await filtered.ReadDirectoryAsync(session, await filtered.RefreshAsync(session, token), "", token)).Single(entry => entry.Name == "new.txt");
        check(ignored.State == FileState.Ignored && !ignored.HasDifferences, "Machine-local ignored files stay visible without counting as differences");
        await OfficeDocumentTests.RunAsync(Path.Combine(root, "office"), check);
        await RevisionOperationsTests.RunAsync(Path.Combine(root, "revisions"), check);
        await FileManagementTests.RunAsync(Path.Combine(root, "file-management"), check);
        await BackupManagerTests.RunAsync(Path.Combine(root, "backup-management"), check);
        await EmbeddedSvnTests.RunAsync(Path.Combine(root, "embedded-svn"), check);
        await EmbeddedWinMergeTests.RunAsync(Path.Combine(root, "embedded-winmerge"), check);
    }
}
