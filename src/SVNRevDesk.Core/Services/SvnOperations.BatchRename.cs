namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public async Task<FileMovePlan> PrepareBatchRenameAsync(IReadOnlyList<BatchRenameRow> rows, string localRoot, CancellationToken token)
    {
        if (rows.Count == 0 || rows.Any(row => row.Error.Length > 0)) throw new ArgumentException("请先处理预览中的名称错误。");
        if (rows.Select(row => NormalizeMovePath(row.Source)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new ArgumentException("源项目重复。");
        var directory = Path.GetDirectoryName(NormalizeMovePath(rows[0].Source))!;
        foreach (var row in rows)
        {
            var source = NormalizeMovePath(row.Source); var destination = NormalizeMovePath(row.Destination);
            if (!RepositoryService.IsDescendant(source, NormalizeMovePath(localRoot))
                || !SameMovePath(Path.GetDirectoryName(source)!, directory)
                || !SameMovePath(Path.GetDirectoryName(destination)!, directory))
                throw new ArgumentException("批量重命名只能处理当前工作副本同一目录下的项目。");
            if (SameMovePath(source, destination) && source != destination) throw new ArgumentException("暂不支持仅更改字母大小写。");
        }
        var changes = rows.Where(row => row.Changed).ToArray();
        if (changes.Length == 0) throw new ArgumentException("名称均未变化，请先设置重命名规则。");
        var plan = await PrepareFileMovesAsync(changes.Select(row => (row.Source, row.Destination)), localRoot, token);
        if (plan.Conflicts.Count > 0) throw new IOException("目标名称已被文件或 SVN 待提交项目占用，请修改规则。批量重命名不会覆盖已有项目。");
        return plan;
    }
}
