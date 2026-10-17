using System.Globalization;

namespace SVNRevDesk.Core.Services;

public sealed record BackupSummary(int Count, long TotalBytes, int ExpiredCount, long ExpiredBytes,
    int RetainedCount, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<string> CleanablePaths { get; init; } = [];
    public int CleanableCount => CleanablePaths.Count;
    public long CleanableBytes { get; init; }
}

public sealed record BackupCleanupResult(int DeletedCount, long DeletedBytes, IReadOnlyList<string> Errors);

// Only application-generated backup names are eligible. File timestamps belong to the
// original working files, so they must never determine when a backup expires.
public sealed class BackupManager
{
    public const int DefaultRetentionDays = 30;
    public const int MaximumRetentionDays = 3650;
    private readonly SemaphoreSlim access = new(1, 1);
    public string DirectoryPath { get; }

    public BackupManager(string storageDirectory)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(storageDirectory), "Backups");
    }

    public Task<BackupSummary> GetSummaryAsync(int retentionDays, CancellationToken token = default,
        DateTimeOffset? now = null) => RunAsync(() => ReadSummary(Cutoff(retentionDays, now), token), token);

    public Task<BackupCleanupResult> CleanupExpiredAsync(int retentionDays, CancellationToken token = default,
        DateTimeOffset? now = null) => RunAsync(() => Cleanup(Cutoff(retentionDays, now), token), token);

    public Task<BackupCleanupResult> CleanupAllAsync(IReadOnlyList<string> backupPaths, CancellationToken token = default)
    {
        // Delete the backups included in the confirmation; newly created backups remain.
        var selected = backupPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return RunAsync(() => Cleanup(null, token, selected), token);
    }

    private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken token)
    {
        await access.WaitAsync(token);
        try { return await Task.Run(operation, token); }
        finally { access.Release(); }
    }

    private BackupSummary ReadSummary(DateTimeOffset cutoff, CancellationToken token)
    {
        var count = 0;
        var expiredCount = 0;
        var retainedCount = 0;
        long totalBytes = 0, expiredBytes = 0, cleanableBytes = 0;
        var cleanablePaths = new List<string>();
        var errors = new List<string>();
        foreach (var path in ReadBackupPaths())
        {
            token.ThrowIfCancellationRequested();
            count++;
            try
            {
                var contents = ReadContents(path, token);
                totalBytes += contents.Bytes;
                if (CreatedAt(path) is { } created)
                {
                    cleanablePaths.Add(path);
                    cleanableBytes += contents.Bytes;
                    if (created < cutoff) { expiredCount++; expiredBytes += contents.Bytes; }
                }
                else retainedCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                retainedCount++;
                errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
            }
        }
        return new(count, totalBytes, expiredCount, expiredBytes, retainedCount, errors)
        { CleanablePaths = cleanablePaths.ToArray(), CleanableBytes = cleanableBytes };
    }

    private BackupCleanupResult Cleanup(DateTimeOffset? cutoff, CancellationToken token, IReadOnlyList<string>? backupPaths = null)
    {
        var count = 0;
        long bytes = 0;
        var errors = new List<string>();
        EnsureSafePath(DirectoryPath);
        foreach (var path in backupPaths ?? ReadBackupPaths())
        {
            token.ThrowIfCancellationRequested();
            if (CreatedAt(path) is not { } created || (cutoff is { } expiry && created >= expiry)) continue;
            try
            {
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), DirectoryPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("只能清理备份目录中的直接子项，已保留。");
                if (!Directory.Exists(path) && !File.Exists(path)) continue;
                // Inspect the complete backup before deleting anything. Links, junctions and
                // unreadable subtrees preserve the whole backup rather than following a target.
                var contents = ReadContents(path, token);
                foreach (var file in contents.Files)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureSafePath(file);
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(file);
                }
                foreach (var directory in contents.Directories.AsEnumerable().Reverse())
                {
                    token.ThrowIfCancellationRequested();
                    EnsureSafePath(directory);
                    // Never use recursive deletion: recheck every path and leave new files alone.
                    Directory.Delete(directory, recursive: false);
                }
                count++;
                bytes += contents.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
            }
        }
        return new(count, bytes, errors);
    }

    private string[] ReadBackupPaths()
    {
        EnsureSafePath(DirectoryPath);
        return Directory.Exists(DirectoryPath) ? Directory.GetFileSystemEntries(DirectoryPath) : [];
    }

    private BackupContents ReadContents(string path, CancellationToken token)
    {
        var files = new List<string>();
        var directories = new List<string>();
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            EnsureSafePath(current);
            if (Directory.Exists(current))
            {
                directories.Add(current);
                foreach (var child in Directory.GetFileSystemEntries(current)) pending.Push(child);
            }
            else
            {
                var file = new FileInfo(current);
                bytes += file.Length;
                files.Add(current);
            }
        }
        return new(files, directories, bytes);
    }

    private void EnsureSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.Equals(DirectoryPath, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("路径超出了备份目录，已保留。");
        for (string? current = fullPath; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("包含符号链接或目录联接，已保留。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static DateTimeOffset Cutoff(int retentionDays, DateTimeOffset? now)
    {
        if (retentionDays is < 1 or > MaximumRetentionDays)
            throw new ArgumentOutOfRangeException(nameof(retentionDays), $"备份保留天数须为 1–{MaximumRetentionDays}。");
        return (now ?? DateTimeOffset.Now).AddDays(-retentionDays);
    }

    private static DateTimeOffset? CreatedAt(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("deleted-", StringComparison.OrdinalIgnoreCase)) name = name[8..];
        else if (name.StartsWith("pasted-", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        else if (name.StartsWith("moved-", StringComparison.OrdinalIgnoreCase)) name = name[6..];
        else if (name.StartsWith("SvnClient-", StringComparison.OrdinalIgnoreCase)) name = name[10..];
        else if (name.StartsWith("WinMerge-", StringComparison.OrdinalIgnoreCase)) name = name[9..];
        if (name.Length != 48 || name[15] != '-' || !Guid.TryParseExact(name[16..], "N", out _)
            || !DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var created)) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Local));
    }

    private sealed record BackupContents(List<string> Files, List<string> Directories, long Bytes);
}
