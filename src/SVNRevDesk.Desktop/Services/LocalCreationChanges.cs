using System.IO;

namespace SVNRevDesk.Desktop.Services;

// FileSystemWatcher callbacks can arrive after creation has finished. Remember the created file's
// signature so those notifications do not query the server; a later edit still triggers a refresh.
public sealed class LocalCreationChanges
{
    private sealed record FileStamp(bool Directory, long Length, DateTime ModifiedAt);
    private readonly object gate = new();
    private readonly Dictionary<string, FileStamp?> created = new(StringComparer.OrdinalIgnoreCase);

    public void Begin(string path) { lock (gate) created[path] = null; }

    public void Complete(string path)
    {
        lock (gate)
        {
            var stamp = ReadStamp(path);
            if (stamp == null) created.Remove(path);
            else created[path] = stamp;
        }
    }

    public bool ShouldIgnore(string path)
    {
        if (Path.GetFileName(path).StartsWith(".svnclient-new-", StringComparison.OrdinalIgnoreCase)) return true;
        lock (gate)
        {
            if (!created.TryGetValue(path, out var stamp)) return false;
            if (stamp == null || stamp == ReadStamp(path)) return true;
            created.Remove(path);
            return false;
        }
    }

    public void Clear() { lock (gate) created.Clear(); }

    private static FileStamp? ReadStamp(string path)
    {
        try
        {
            if (Directory.Exists(path)) return new(true, 0, Directory.GetLastWriteTimeUtc(path));
            var file = new FileInfo(path);
            return file.Exists ? new(false, file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
