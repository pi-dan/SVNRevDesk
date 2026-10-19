using System.Xml.Linq;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations(SvnRunner runner, string? temporaryDirectory = null, string? backupDirectory = null, LocalIgnoreStore? localIgnores = null)
{
    public Task<string> UpdateAsync(string path, bool directory, CancellationToken token) => runner.RunAsync(
        ["update", "--parents", "--set-depth", directory ? "infinity" : "empty", "--accept", "postpone", SvnRunner.Target(path)], token);

    public async Task<string> UpdateDirectoryFilesAsync(string path, CancellationToken token)
    {
        // A sparse directory at depth=empty needs to be expanded to files before SVN can
        // download its immediate children. Existing deeper working copies must keep their
        // ambient depth, so use a non-depth-changing update in that case.
        var info = XDocument.Parse(await runner.RunAsync(["info", "--xml", "--depth", "empty", SvnRunner.Target(path)], token));
        var depth = info.Descendants("depth").SingleOrDefault()?.Value;
        var arguments = depth == "empty"
            ? new[] { "update", "--parents", "--set-depth", "files", "--ignore-externals", "--accept", "postpone", SvnRunner.Target(path) }
            : new[] { "update", "--parents", "--depth", "files", "--ignore-externals", "--accept", "postpone", SvnRunner.Target(path) };
        return await runner.RunAsync(arguments, token);
    }

    public async Task<string> CheckoutAsync(string url, string directory, bool sparse, CancellationToken token)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // SVN treats checkout destinations as literal paths, unlike revision-aware targets.
        // Using '.' also avoids SVN canonicalizing a space immediately before '@' in that path.
        return await runner.RunAsync(["checkout", "--depth", sparse ? "empty" : "infinity", SvnRunner.Target(url), "."],
            token, workingDirectory: directory);
    }

    public Task<string> CommitAsync(IEnumerable<string> paths, string message, CancellationToken token) =>
        RunWithTextFileAsync(new[] { "commit" }.Concat(paths.Select(SvnRunner.Target)), message, token);

    public Task<string> AddAsync(IEnumerable<string> paths, CancellationToken token)
    {
        var targets = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        localIgnores?.Reload();
        return localIgnores != null && targets.Any(localIgnores.Affects)
            ? AddWithLocalIgnoresAsync(targets, token)
            : runner.RunAsync(new[] { "add", "--parents" }.Concat(targets.Select(SvnRunner.Target)), token);
    }
    public Task<string> DeleteAsync(IEnumerable<string> paths, CancellationToken token) => runner.RunAsync(
        new[] { "delete" }.Concat(paths.Select(SvnRunner.Target)), token);
    public Task<string> RevertAsync(IEnumerable<string> paths, CancellationToken token) => runner.RunAsync(
        new[] { "revert", "--depth", "infinity" }.Concat(paths.Select(SvnRunner.Target)), token);
    public Task<string> MoveAsync(string source, string destination, CancellationToken token)
    {
        ValidateLiteralDestination(destination);
        return runner.RunAsync(["move", SvnRunner.Target(source), destination], token);
    }
    public Task<string> LockAsync(string path, string comment, CancellationToken token) => RunWithTextFileAsync(
        ["lock", SvnRunner.Target(path)], comment, token);
    public Task<string> UnlockAsync(string path, CancellationToken token) => runner.RunAsync(["unlock", SvnRunner.Target(path)], token);
    public Task<string> CleanupAsync(string path, CancellationToken token) => runner.RunAsync(["cleanup", "."], token, workingDirectory: path);
    public Task<string> ResolveAsync(string path, string choice, CancellationToken token) => runner.RunAsync(
        ["resolve", "--accept", choice, SvnRunner.Target(path)], token);

    public async Task<IReadOnlyList<HistoryEntry>> LogAsync(string target, CancellationToken token) => SvnXml.ReadLog(
        await runner.RunAsync(["log", "--xml", "--limit", "100", SvnRunner.Target(target)], token));
    public Task<string> BlameAsync(string url, CancellationToken token) => runner.RunAsync(["blame", SvnRunner.Target(url)], token);
    public Task<string> PropertiesAsync(string target, CancellationToken token) => runner.RunAsync(
        ["proplist", "--verbose", SvnRunner.Target(target)], token);
    public async Task<string> SetPropertyAsync(string target, string name, string value, CancellationToken token)
    {
        return await RunWithTextFileAsync(["propset", name, SvnRunner.Target(target)], value, token);
    }
    public Task<string> ExportAsync(string target, string destination, CancellationToken token)
    {
        ValidateLiteralDestination(destination);
        return runner.RunAsync(["export", SvnRunner.Target(target), destination], token);
    }

    public Task<string> ExportRevisionAsync(string url, string revision, string destinationName, string workingDirectory, CancellationToken token) =>
        runner.RunAsync(["export", "-r", revision, SvnRunner.AtRevision(url, revision), destinationName], token, workingDirectory: workingDirectory);
    public Task<string> DirectoryDiffAsync(string path, CancellationToken token) => runner.RunAsync(
        ["diff", "--summarize", "-r", "HEAD", SvnRunner.Target(path)], token);

    public Task<string> DirectoryBaseDiffAsync(string path, CancellationToken token) => runner.RunAsync(
        // Without a revision argument SVN diff treats its target as a literal working-copy path.
        ["diff", "--summarize", "."], token, workingDirectory: path);

    public Task<string> CopyBranchAsync(string sourceUrl, string destinationUrl, string message, CancellationToken token) => RunWithTextFileAsync(
        ["copy", SvnRunner.Target(sourceUrl), destinationUrl], message, token);
    public Task<string> SwitchAsync(string url, string path, CancellationToken token) => runner.RunAsync(
        ["switch", "--accept", "postpone", SvnRunner.Target(url), "."], token, workingDirectory: path);
    public Task<string> MergeAsync(string url, string path, bool dryRun, CancellationToken token) => runner.RunAsync(
        (dryRun ? new[] { "merge", "--dry-run", "--accept", "postpone" } : new[] { "merge", "--accept", "postpone" })
            .Concat([SvnRunner.Target(url), "."]), token, workingDirectory: path);

    public async Task<string> DownloadForViewingAsync(string url, string revision, string cacheRoot, CancellationToken token,
        string? pegRevision = null)
    {
        var directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = Uri.UnescapeDataString(new Uri(url).Segments.Last().TrimEnd('/'));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "" or "." or "..") name = "server-file";
        var destination = Path.Combine(directory, name);
        await runner.RunAsync(["cat", "-r", revision, SvnRunner.AtRevision(url, pegRevision ?? revision)], token, destination);
        return destination;
    }

    public async Task<string> DownloadBaseAsync(string path, string cacheRoot, CancellationToken token)
    {
        var directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(path));
        await runner.RunAsync(["cat", "-r", "BASE", SvnRunner.Target(path)], token, destination);
        return destination;
    }

    public async Task<string?> ReplaceWithServerAsync(string path, CancellationToken token)
    {
        var statuses = SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--verbose",
            "--depth", "empty", SvnRunner.Target(path)], token));
        var status = statuses.FirstOrDefault();
        if (status?.Item is "added" or "unversioned" or "replaced" or "obstructed"
            || status?.TreeConflict == true)
            throw new InvalidOperationException("新增、替换、阻塞或目录树冲突文件不能直接覆盖，请先处理其工作副本状态。");

        string? backup = null;
        if (File.Exists(path))
        {
            if (backupDirectory == null) backup = path + $".svnclient-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            else
            {
                var directory = Path.Combine(backupDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                backup = Path.Combine(directory, Path.GetFileName(path));
            }
            File.Copy(path, backup);
        }
        // Keep the backup even if revert/update fails. Never overwrite .svn metadata ourselves.
        await RevertAsync([path], token);
        await UpdateAsync(path, false, token);
        var after = SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--depth", "empty", SvnRunner.Target(path)], token));
        if (after.Any(s => s.HasLocalChanges || s.Item == "missing"))
            throw new InvalidOperationException($"更新后文件仍需处理，请检查状态。备份：{backup ?? "无"}");
        return backup;
    }

    private static void ValidateLiteralDestination(string path)
    {
        // SVN 1.14 canonicalizes the part before the last '@' even for literal destinations.
        // On Windows it drops a preceding space. Reject this case before making any change.
        var at = path.LastIndexOf('@');
        if (at > 0 && char.IsWhiteSpace(path[at - 1]))
            throw new ArgumentException("当前 SVN 版本不能可靠处理目标路径中紧靠 @ 前面的空格，请去掉该空格后重试。源文件不受此限制。");
    }

    private async Task<string> RunWithTextFileAsync(IEnumerable<string> arguments, string text, CancellationToken token)
    {
        var directory = temporaryDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var textFile = Path.Combine(directory, "svnclient-text-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // Preserve multiline text, Unicode, and literal wildcard characters in messages/properties.
            await File.WriteAllTextAsync(textFile, text, new System.Text.UTF8Encoding(false), token);
            return await runner.RunAsync(arguments.Concat(["--file", textFile, "--encoding", "UTF-8"]), token);
        }
        finally
        {
            if (File.Exists(textFile)) File.Delete(textFile);
        }
    }
}
