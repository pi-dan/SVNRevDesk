using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SVNRevDesk.Desktop.Services;

public static class FileExplorer
{
    public static string FindExistingPath(string? localPath)
    {
        if (localPath == null) throw new InvalidOperationException("尚未关联本地工作副本，请先关联或检出到本地。");
        var path = Path.GetFullPath(localPath);
        while (!File.Exists(path) && !Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? throw new DirectoryNotFoundException("本地目录不存在，或磁盘当前不可用。");
        return path;
    }

    public static string Show(string? localPath)
    {
        var existing = FindExistingPath(localPath);
        if (!existing.Equals(Path.GetFullPath(localPath!), StringComparison.OrdinalIgnoreCase))
        {
            // A sparse or missing item has no icon to select; open its nearest existing directory.
            Process.Start(new ProcessStartInfo(existing) { UseShellExecute = true })?.Dispose();
            return existing;
        }

        // Shell item IDs avoid Explorer command-line quoting issues with commas and Unicode names.
        Marshal.ThrowExceptionForHR(SHParseDisplayName(existing, IntPtr.Zero, out var item, 0, out _));
        try
        {
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally { Marshal.FreeCoTaskMem(item); }
        return existing;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr item,
        uint attributes, out uint foundAttributes);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}
