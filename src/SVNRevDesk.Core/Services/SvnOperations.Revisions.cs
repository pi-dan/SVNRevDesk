using System.Globalization;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public Task<string> UpdateToRevisionAsync(string path, bool directory, long revision, CancellationToken token)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision), "修订号必须是非负整数。");
        return runner.RunAsync(["update", "--parents", "--set-depth", directory ? "infinity" : "empty",
            "--revision", revision.ToString(CultureInfo.InvariantCulture), "--accept", "postpone", SvnRunner.Target(path)], token);
    }

    public async Task<string> ReverseMergeAsync(string path, long revision, bool dryRun, CancellationToken token)
    {
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision), "要回滚的提交修订号必须是正整数。");
        path = Path.GetFullPath(path);
        var info = SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", SvnRunner.Target(path)], token)).Single();
        if (info.WorkingCopyRoot == null) throw new InvalidOperationException("反向合并需要本地工作副本。");

        // Recheck immediately before both preview and execution. Never revert existing user changes.
        var statuses = SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--depth", "infinity",
            "--ignore-externals", SvnRunner.Target(path)], token));
        if (statuses.Any(status => status.HasLocalChanges || status.Item is "missing" or "obstructed" or "incomplete" or "unversioned"))
            throw new InvalidOperationException("回滚范围内存在未提交修改、未纳入版本控制的项目或异常状态。请先提交、移走或处理这些项目，再执行反向合并。");

        var arguments = new List<string> { "merge", "--change", "-" + revision.ToString(CultureInfo.InvariantCulture), "--accept", "postpone" };
        if (dryRun) arguments.Add("--dry-run");
        arguments.Add(SvnRunner.AtRevision(info.Url, "HEAD"));
        // SVN 1.14's one-source merge parses an explicit file@name as a peg, but keeps a
        // trailing escape @ in the literal destination. Its implicit basename handles @ correctly.
        var name = Path.GetFileName(path);
        if (info.IsDirectory) arguments.Add(".");
        else if (!name.Contains('@')) arguments.Add(name);
        else if (!string.Equals(Uri.UnescapeDataString(new Uri(info.Url).Segments.Last()), name, StringComparison.Ordinal))
            throw new InvalidOperationException("此文件名含 @，且与切换后的仓库文件名不同。请在其上级目录执行反向合并，以明确回滚目标。");
        return await runner.RunAsync(arguments, token, workingDirectory: info.IsDirectory ? path : Path.GetDirectoryName(path));
    }
}
