using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class LocalCreationTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> assert)
    {
        Directory.CreateDirectory(fixture);
        var repositoryPath = Path.Combine(fixture, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start) ?? throw new InvalidOperationException("Cannot start svnadmin"))
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var service = new RepositoryService(runner);
        var local = Path.Combine(fixture, "新建 @ 工作副本");
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var token = CancellationToken.None;
        await operations.CheckoutAsync(url, local, false, token);

        var folder = await LocalItemCreator.CreateAsync(local, local, new("中文 @ 目录", NewItemKind.Folder), token);
        var text = await LocalItemCreator.CreateAsync(local, folder, new("说明 @ 文件.txt", NewItemKind.Text, "新建内容\n"), token);
        var json = await LocalItemCreator.CreateAsync(local, local, new("配置.json", NewItemKind.Text, "{}\n"), token);
        var xml = await LocalItemCreator.CreateAsync(local, local, new("配置.xml", NewItemKind.Text, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root />\n"), token);
        var custom = await LocalItemCreator.CreateAsync(local, local, new("自定义.test", NewItemKind.EmptyFile), token);
        using var parsedJson = JsonDocument.Parse(File.ReadAllText(json));
        assert(Directory.Exists(folder) && File.ReadAllText(text) == "新建内容\n" && new FileInfo(custom).Length == 0,
            "Create local folders, UTF-8 text and arbitrary extensions with Unicode, spaces and @");
        assert(parsedJson.RootElement.ValueKind == JsonValueKind.Object && XDocument.Load(xml).Root?.Name == "root",
            "New JSON and XML files have valid initial contents");

        await operations.AddAsync([text], token);
        var statuses = await operations.ReadLocalDirectoryStatusAsync(folder, local, token);
        assert(statuses.Single(status => status.Path == text).Item == "added"
            && statuses.Single(status => status.Path == folder).Item == "added", "Optional SVN add schedules file and required new parent without committing");
        var rootStatuses = await operations.ReadLocalDirectoryStatusAsync(local, local, token);
        assert(rootStatuses.Single(status => status.Path == custom).Item == "unversioned", "Creation without SVN add retains an unversioned file");
        var snapshot = await service.RefreshAsync(await service.OpenWorkingCopyAsync(local, token), token);
        assert(snapshot.Revision == 0, "Creating and scheduling items leaves repository HEAD unchanged");
        var changes = await operations.ReadCommitChangesAsync([text], local, token);
        assert(changes.Any(change => change.Path == text && change.CanCommit)
            && changes.Single(change => change.Path == text).RequiredParents.Contains(folder),
            "Created scheduled files are selectable in commit with their parent dependency");

        var rejectedNames = new[] { "", " ", ".", "..", "../escape", "a/b", "a\\b", "a:b", "x?", "x.", "x ", "CON", "CON .txt", "nul.txt", "COM1.log", "LPT²", ".SVN", ".svnclient-new-user", new string('a', 256) };
        var allRejected = true;
        foreach (var name in rejectedNames)
        {
            try { LocalItemCreator.ValidateName(name); allRejected = false; }
            catch (ArgumentException) { }
        }
        assert(allRejected, "Names reject traversal, invalid characters, metadata and Windows reserved names");
        var duplicateRejected = false;
        try { await LocalItemCreator.CreateAsync(local, folder, new(Path.GetFileName(text), NewItemKind.Text, "overwrite"), token); }
        catch (IOException) { duplicateRejected = true; }
        assert(duplicateRejected && File.ReadAllText(text) == "新建内容\n", "Duplicate creation never overwrites an existing item");
        var outsideRejected = false;
        try { await LocalItemCreator.CreateAsync(local, fixture, new("escape.txt", NewItemKind.EmptyFile), token); }
        catch (ArgumentException) { outsideRejected = true; }
        assert(outsideRejected && !File.Exists(Path.Combine(fixture, "escape.txt")), "Creation stays inside the opened working copy");
        var metadataRejected = false;
        try { await LocalItemCreator.CreateAsync(local, Path.Combine(local, ".svn"), new("metadata.txt", NewItemKind.EmptyFile), token); }
        catch (ArgumentException) { metadataRejected = true; }
        assert(metadataRejected, "SVN metadata directories cannot be creation targets");
        var nested = Path.Combine(local, "nested-working-copy");
        await operations.CheckoutAsync(url, nested, false, token);
        var nestedRejected = false;
        try { await LocalItemCreator.CreateAsync(local, nested, new("nested.txt", NewItemKind.EmptyFile), token); }
        catch (ArgumentException) { nestedRejected = true; }
        assert(nestedRejected, "Nested working copies must be opened separately before creating items");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { await LocalItemCreator.CreateAsync(local, local, new("取消.txt", NewItemKind.Text, "cancelled creation"), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        assert(cancelled && !File.Exists(Path.Combine(local, "取消.txt"))
            && !Directory.EnumerateFileSystemEntries(local).Any(path => Path.GetFileName(path).StartsWith(".svnclient-new-", StringComparison.Ordinal)),
            "Cancelled creation leaves no destination or staging file");
    }
}
