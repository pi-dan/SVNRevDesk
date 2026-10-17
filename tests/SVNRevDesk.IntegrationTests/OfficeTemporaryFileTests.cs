using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;

internal static class OfficeTemporaryFileTests
{
    public static async Task RunAsync(string local, SvnOperations operations, SvnRunner runner, Action<bool, string> assert)
    {
        var token = CancellationToken.None;
        var root = Path.Combine(local, "Office 文件显示");
        Directory.CreateDirectory(root);
        var names = new[] { "~$合同.docx", "~$统计表.xlsx", "~$汇报.pptx", ".~lock.合同.docx#", "图纸.dwl", "图纸.DWL2" };
        var locks = names.Select(name => Path.Combine(root, name)).ToArray();
        foreach (var path in locks) File.WriteAllText(path, "Office lock fixture");
        var visibleNames = new[] { "合同.docx", "统计表.xlsx", "汇报.pptx", "恢复.asd", "备份.wbk",
            "图纸.dwg", "图纸.sv$", "图纸.bak", ".~lock.说明.docx", ".~lock.#", "图纸.dwl.txt", "依赖.lock" };
        foreach (var name in visibleNames)
            File.WriteAllText(Path.Combine(root, name), "ordinary file fixture");
        var folders = new[] { "~$普通目录", ".~lock.普通目录#", "普通目录.dwl2" }.Select(name => Path.Combine(root, name)).ToArray();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "保留.txt"), "ordinary directory fixture");
        }

        var changes = await operations.ReadCommitChangesAsync([root], local, token);
        assert(!changes.Any(change => locks.Contains(change.Path))
            && folders.All(folder => changes.Any(change => change.Path == folder && change.IsDirectory))
            && visibleNames.All(name => changes.Any(change => change.Path == Path.Combine(root, name))),
            "Unversioned commit discovery hides Microsoft/LibreOffice/AutoCAD locks while retaining recovery files and similar names");

        // Simulate a repository that already contains these files to exercise remote listings and modifications too.
        await operations.AddAsync([root], token);
        await operations.CommitAsync([root], "Office lock display fixture", token);
        foreach (var path in locks) File.AppendAllText(path, " changed");
        var original = locks.ToDictionary(path => path, File.ReadAllBytes);
        var repository = new RepositoryService(runner);
        var session = await repository.OpenWorkingCopyAsync(local, token);
        var snapshot = await repository.RefreshAsync(session, token);
        var relative = Path.GetRelativePath(local, root).Replace('\\', '/');
        var entries = await repository.ReadDirectoryAsync(session, snapshot, relative, token);
        assert(!entries.Any(entry => names.Contains(entry.Name))
            && folders.All(folder => entries.Any(entry => entry.LocalPath == folder && entry.IsDirectory))
            && visibleNames.All(name => entries.Any(entry => entry.Name == name)),
            "Real listings hide known software locks without hiding documents, CAD autosaves, backups, similar names or directories");

        var cachedLocks = locks.Select(path => new BrowserEntry(Path.GetFileName(path), relative + "/" + Path.GetFileName(path),
            RepositoryService.AppendUrl(session.Url, relative + "/" + Path.GetFileName(path)), path, false,
            FileState.LocalModified, snapshot.Revision, snapshot.Revision, snapshot.Revision, 1, "", null, null, "", ""));
        var preview = RepositoryService.ReadLocalDirectory(session, snapshot, relative, entries.Concat(cachedLocks).ToList(), true);
        assert(!preview.Any(entry => names.Contains(entry.Name))
            && folders.All(folder => preview.Any(entry => entry.LocalPath == folder && entry.IsDirectory)),
            "Local refresh removes lock files from both filesystem results and older cached server rows");
        assert(RepositoryService.GetSummary(snapshot, root) == "已检查项无差异"
            && (await operations.ReadCommitChangesAsync([root], local, token)).Count == 0,
            "Known software lock modifications do not affect directory difference counts or commit selections");
        assert(locks.All(path => File.ReadAllBytes(path).SequenceEqual(original[path]))
            && snapshot.Statuses.Values.Count(status => locks.Contains(status.Path) && status.HasLocalChanges) == names.Length,
            "Hiding locks preserves their bytes and existing SVN status without deleting or changing ignore properties");
    }
}
