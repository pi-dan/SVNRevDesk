namespace SVNRevDesk.Core.Services;

public sealed record BatchRenameSource(string Path, bool IsDirectory);
public sealed record BatchRenameOptions(string Find = "", string Replace = "", string Prefix = "", string Suffix = "",
    bool Number = false, int Start = 1, int Digits = 3, bool PreserveExtension = true);
public sealed record BatchRenameRow(string Source, string Destination, string Error)
{
    public string OldName => System.IO.Path.GetFileName(Source);
    public string NewName => Error.Length > 0 && !System.IO.Path.IsPathFullyQualified(Destination) ? Destination : System.IO.Path.GetFileName(Destination);
    public bool Changed => !Source.Equals(Destination, StringComparison.Ordinal);
    public string Status => Error.Length > 0 ? Error : Changed ? "可重命名" : "名称不变";
}

public static class BatchRename
{
    public static IReadOnlyList<BatchRenameRow> Preview(IReadOnlyList<BatchRenameSource> sources, BatchRenameOptions options)
    {
        if (options.Start < 0 || options.Digits is < 1 or > 9) throw new ArgumentException("起始编号须为非负整数，编号位数须为 1 到 9。");
        var rows = new List<BatchRenameRow>();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = Path.GetFullPath(sources[index].Path);
            var name = Path.GetFileName(source);
            var extension = options.PreserveExtension && !sources[index].IsDirectory ? Path.GetExtension(name) : "";
            var stem = extension.Length == 0 ? name : name[..^extension.Length];
            if (options.Find.Length > 0) stem = stem.Replace(options.Find, options.Replace, StringComparison.Ordinal);
            var number = options.Number ? ((long)options.Start + index).ToString("D" + options.Digits, System.Globalization.CultureInfo.InvariantCulture) : "";
            var renamed = options.Prefix + stem + options.Suffix + number + extension;
            var error = "";
            try { LocalItemCreator.ValidateName(renamed); }
            catch (ArgumentException ex) { error = ex.Message; }
            // Keep invalid paths out of Path.Combine; they remain visible in the preview.
            if (error.Length > 0) { rows.Add(new(source, renamed, error)); continue; }
            var destination = Path.Combine(Path.GetDirectoryName(source)!, renamed);
            if (!source.Equals(destination, StringComparison.Ordinal) && source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                error = "暂不支持仅更改字母大小写，请使用其他新名称。";
            else if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase) && (File.Exists(destination) || Directory.Exists(destination)))
                error = "目标名称已占用（包含所选源项目），请换一个名称。";
            rows.Add(new(source, destination, error));
        }
        var duplicates = rows.GroupBy(row => row.Destination, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(row => duplicates.Contains(row.Destination) ? row with { Error = "多个项目生成了同一名称。" } : row).ToArray();
    }
}
