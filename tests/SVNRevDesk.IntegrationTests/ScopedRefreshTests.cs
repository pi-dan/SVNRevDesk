using System.Diagnostics;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class ScopedRefreshTests
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
        var repository = new RepositoryService(runner);
        var operations = new SvnOperations(runner);
        var token = CancellationToken.None;
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var seed = Path.Combine(fixture, "seed");
        var local = Path.Combine(fixture, "local");
        var peer = Path.Combine(fixture, "peer");
        const string alpha = "中文 @ 目录";
        await operations.CheckoutAsync(url, seed, false, token);
        Directory.CreateDirectory(Path.Combine(seed, alpha, "deep"));
        Directory.CreateDirectory(Path.Combine(seed, "beta"));
        File.WriteAllText(Path.Combine(seed, alpha, "direct.txt"), "initial\n");
        File.WriteAllText(Path.Combine(seed, alpha, "deep", "child.txt"), "initial\n");
        File.WriteAllText(Path.Combine(seed, "beta", "sibling.txt"), "initial\n");
        await operations.AddAsync([Path.Combine(seed, alpha), Path.Combine(seed, "beta")], token);
        await operations.CommitAsync([seed], "Scoped refresh fixture", token);
        await operations.CheckoutAsync(url, local, false, token);
        await operations.CheckoutAsync(url, peer, false, token);
        File.AppendAllText(Path.Combine(peer, alpha, "direct.txt"), "server\n");
        File.AppendAllText(Path.Combine(peer, alpha, "deep", "child.txt"), "server\n");
        File.AppendAllText(Path.Combine(peer, "beta", "sibling.txt"), "server\n");
        await operations.CommitAsync([peer], "Changes in separate branches", token);

        var session = await repository.OpenWorkingCopyAsync(local, token);
        var target = Path.Combine(local, alpha);
        var shallow = await repository.RefreshDirectoryAsync(session, new DirectoryRefresh(alpha), token);
        assert(shallow.Statuses.ContainsKey(Path.Combine(target, "direct.txt"))
            && !shallow.Statuses.ContainsKey(Path.Combine(target, "deep", "child.txt"))
            && shallow.Statuses.Keys.All(path => path == target || Path.GetDirectoryName(path) == target),
            "A manual directory check includes only the directory and immediate children");
        assert(shallow.LocalInfo.Keys.All(path => path == target || Path.GetDirectoryName(path) == target),
            "A scoped metadata query does not enumerate another branch or grandchildren");
        var entries = await repository.ReadDirectoryAsync(session, shallow, alpha, token);
        assert(entries.Single(entry => entry.Name == "direct.txt").State == FileState.ServerChanged,
            "A scoped check still detects server updates for the selected directory's files");

        var recursive = await repository.RefreshDirectoryAsync(session, new DirectoryRefresh(alpha, true), token);
        assert(recursive.Statuses.TryGetValue(Path.Combine(target, "deep", "child.txt"), out var child) && child.HasServerChanges
            && recursive.Statuses.Keys.All(path => path == target || RepositoryService.IsDescendant(path, target)),
            "A directory operation checks its descendants without scanning sibling contents");
        var root = await repository.RefreshDirectoryAsync(session, new DirectoryRefresh(""), token);
        assert(!root.Statuses.ContainsKey(Path.Combine(local, "beta", "sibling.txt")),
            "Manually refreshing the opened root uses shallow status queries");

        var unversioned = Path.Combine(local, "local-only");
        Directory.CreateDirectory(unversioned);
        File.WriteAllText(Path.Combine(unversioned, "new.txt"), "local\n");
        var localOnly = await repository.RefreshDirectoryAsync(session, new DirectoryRefresh("local-only"), token);
        assert((await repository.ReadDirectoryAsync(session, localOnly, "local-only", token)).Single().State == FileState.LocalAdded,
            "Scoped refresh supports unversioned directories without requesting a nonexistent server URL");
        var sparse = Path.Combine(fixture, "sparse");
        await operations.CheckoutAsync(url, sparse, true, token);
        var sparseSession = await repository.OpenWorkingCopyAsync(sparse, token);
        var sparseSnapshot = await repository.RefreshDirectoryAsync(sparseSession, new DirectoryRefresh(alpha), token);
        assert((await repository.ReadDirectoryAsync(sparseSession, sparseSnapshot, alpha, token))
            .Single(entry => entry.Name == "direct.txt").State == FileState.NotDownloaded,
            "Scoped refresh supports browsing a server directory absent from a sparse working copy");

        await operations.UpdateAsync(Path.Combine(peer, alpha), true, token);
        await operations.DeleteAsync([Path.Combine(peer, alpha)], token);
        await operations.CommitAsync([peer], "Delete the selected directory", token);
        var deleted = await repository.RefreshDirectoryAsync(session, new DirectoryRefresh(alpha, true), token);
        var deletedEntries = await repository.ReadDirectoryAsync(session, deleted, alpha, token);
        assert(deletedEntries.Count > 0 && deletedEntries.All(entry => entry.State == FileState.ServerDeleted),
            "A scoped check retains local files and identifies a server-deleted directory");
        var online = await repository.OpenUrlAsync(url, token);
        var onlineSnapshot = await repository.RefreshDirectoryAsync(online, new DirectoryRefresh("beta"), token);
        assert((await repository.ReadDirectoryAsync(online, onlineSnapshot, "beta", token)).Single().State == FileState.Unchecked,
            "Scoped checks also work for online-only browsing");
    }
}
