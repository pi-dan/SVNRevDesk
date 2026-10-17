namespace SVNRevDesk.Core.Services;

public static class OfficeTemporaryFiles
{
    // Match only known owner/lock files; recovery files, backups and directories remain visible.
    public static bool IsLockFile(string path, bool isDirectory)
    {
        if (isDirectory) return false;
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return name.StartsWith("~$", StringComparison.Ordinal)
            || name.Length > 8 && name.StartsWith(".~lock.", StringComparison.OrdinalIgnoreCase) && name.EndsWith('#')
            || extension.Equals(".dwl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dwl2", StringComparison.OrdinalIgnoreCase);
    }
}
