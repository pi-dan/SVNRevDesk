using System.IO;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Services;

// Clipboard snapshots must survive application exit, because Explorer may paste them afterwards.
public static class ClipboardFileCache
{
    public static Task CleanupAsync(string temporaryDirectory, IReadOnlyList<string> clipboardPaths) => Task.Run(() =>
    {
        var root = Path.GetFullPath(Path.Combine(temporaryDirectory, "Clipboard"));
        if (!Directory.Exists(root)) return;
        try
        {
            LocalFileTransfer.ValidatePath(root);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || Directory.GetCreationTimeUtc(directory) >= DateTime.UtcNow.AddDays(-7)
                    || clipboardPaths.Any(path => path.Equals(directory, StringComparison.OrdinalIgnoreCase)
                        || RepositoryService.IsDescendant(Path.GetFullPath(path), directory))) continue;
                try
                {
                    var files = new List<string>();
                    var directories = new List<string>();
                    var pending = new Stack<string>();
                    pending.Push(directory);
                    // Inspect the whole snapshot before deleting anything; never follow links.
                    while (pending.TryPop(out var path))
                    {
                        LocalFileTransfer.ValidatePath(path);
                        if (!RepositoryService.IsDescendant(path, root)) throw new IOException("剪贴板缓存路径超出了临时目录。");
                        if (Directory.Exists(path))
                        {
                            directories.Add(path);
                            foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
                        }
                        else files.Add(path);
                    }
                    foreach (var path in files)
                    {
                        LocalFileTransfer.ValidatePath(path);
                        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                        File.Delete(path);
                    }
                    foreach (var path in directories.AsEnumerable().Reverse())
                    {
                        LocalFileTransfer.ValidatePath(path);
                        Directory.Delete(path, recursive: false);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* In-use or changed snapshots are retained. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cleanup must not prevent copying. */ }
    });
}
