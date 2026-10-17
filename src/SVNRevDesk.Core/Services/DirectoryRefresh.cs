namespace SVNRevDesk.Core.Services;

public sealed record DirectoryRefresh(string RelativePath, bool IncludeDescendants = false)
{
    public bool ContainsDirectory(string relativePath) => relativePath == RelativePath
        || IncludeDescendants && IsInside(relativePath, RelativePath);

    public static bool IsInside(string path, string parent) => parent.Length == 0
        ? path.Length > 0 : path.StartsWith(parent + "/", StringComparison.Ordinal);
}
