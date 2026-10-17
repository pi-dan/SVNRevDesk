using System.Diagnostics;
using System.Text;
using SVNRevDesk.Core.Services;

internal static class BackupManagerTests
{
    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var manager = new BackupManager(root);
        var now = new DateTimeOffset(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Local));
        var summary = await manager.GetSummaryAsync(30, now: now);
        assert(summary.Count == 0 && summary.TotalBytes == 0 && !Directory.Exists(manager.DirectoryPath),
            "Inspecting absent backups returns zero without creating application data");
        Directory.CreateDirectory(manager.DirectoryPath);

        string Create(DateTimeOffset created, string prefix = "", string? content = "backup")
        {
            var path = Path.Combine(manager.DirectoryPath, Name(created, prefix));
            Directory.CreateDirectory(Path.Combine(path, "nested", "empty"));
            if (content != null) File.WriteAllText(Path.Combine(path, "nested", "saved.txt"), content, new UTF8Encoding(false));
            return path;
        }

        var old = Create(now.AddDays(-31), content: "old file");
        var deleted = Create(now.AddDays(-60), "deleted-", "deleted local content");
        File.SetAttributes(Path.Combine(deleted, "nested", "saved.txt"), FileAttributes.ReadOnly);
        var client = Create(now.AddDays(-45), "SvnClient-", "old DLL");
        var empty = Create(now.AddDays(-50), content: null);
        var recent = Create(now.AddDays(-29), content: "recent backup of an old working file");
        var recentFile = Path.Combine(recent, "nested", "saved.txt");
        File.SetLastWriteTimeUtc(recentFile, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(recent, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var boundary = Create(now.AddDays(-30), content: "exact cutoff");
        var future = Create(now.AddDays(1), content: "future clock");
        var unknown = Path.Combine(manager.DirectoryPath, "manually-saved.txt");
        File.WriteAllText(unknown, "keep manual file");
        File.SetLastWriteTimeUtc(unknown, now.AddYears(-10).UtcDateTime);
        var unknownDirectory = Path.Combine(manager.DirectoryPath, "personal documents");
        Directory.CreateDirectory(unknownDirectory);
        File.WriteAllText(Path.Combine(unknownDirectory, "keep.txt"), "keep custom directory");
        var invalid = Path.Combine(manager.DirectoryPath, "20261399-999999-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "keep.txt"), "invalid date");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "never clean outside Backups");
        var applicationDirectories = new[] { "Cache", "Svn", Path.Combine("Tools", "Svn"), Path.Combine("Tools", "WinMerge"), "Temp", "SvnConfig" };
        foreach (var name in applicationDirectories)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "keep.txt"), name);
        }

        summary = await manager.GetSummaryAsync(30, now: now);
        var expectedBytes = Directory.EnumerateFiles(manager.DirectoryPath, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        assert(summary.Count == 10 && summary.TotalBytes == expectedBytes && summary.ExpiredCount == 4
            && summary.ExpiredBytes == 36 && summary.RetainedCount == 3 && summary.Errors.Count == 0,
            "Backup statistics count complete trees, all backup kinds, empty directories and retained unknown content");
        assert((await manager.GetSummaryAsync(90, now: now)).ExpiredCount == 0,
            "A longer retention period immediately changes which backups expire");
        var result = await manager.CleanupExpiredAsync(30, now: now);
        assert(result.DeletedCount == 4 && result.DeletedBytes == 36 && result.Errors.Count == 0
            && new[] { old, deleted, client, empty }.All(path => !Directory.Exists(path)),
            "Expired working-file, deletion and client backups are removed, including read-only files and empty trees");
        assert(new[] { recent, boundary, future, unknownDirectory, invalid }.All(Directory.Exists) && File.Exists(unknown)
            && File.ReadAllText(recentFile) == "recent backup of an old working file",
            "Recent, exact-cutoff, future and unknown backups survive regardless of original file timestamps");
        assert(File.ReadAllText(sentinel) == "never clean outside Backups"
            && applicationDirectories.All(name => File.ReadAllText(Path.Combine(root, name, "keep.txt")) == name),
            "Backup cleanup never touches the working data, cache, active client or SVN configuration");
        assert((await manager.CleanupExpiredAsync(30, now: now)).DeletedCount == 0,
            "Repeating cleanup leaves remaining backups unchanged");

        var locked = Create(now.AddDays(-40));
        var unlocked = Create(now.AddDays(-40));
        using (var stream = new FileStream(Path.Combine(locked, "nested", "saved.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await manager.CleanupExpiredAsync(30, now: now);
            assert(result.DeletedCount == 1 && result.Errors.Count == 1 && Directory.Exists(locked) && !Directory.Exists(unlocked),
                "An occupied backup reports a failure while other expired backups still clean successfully");
        }
        assert((await manager.CleanupExpiredAsync(30, now: now)).DeletedCount == 1,
            "A previously occupied backup can be cleaned after its handle is released");
        var cancelledBackup = Create(now.AddDays(-40));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await manager.CleanupExpiredAsync(30, cancellation.Token, now); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && File.Exists(Path.Combine(cancelledBackup, "nested", "saved.txt")),
                "Cancelling a queued cleanup preserves backup contents");
        }
        foreach (var days in new[] { 0, -1, BackupManager.MaximumRetentionDays + 1 })
        {
            var rejected = false;
            try { await manager.CleanupExpiredAsync(days, now: now); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            assert(rejected && Directory.Exists(cancelledBackup), "An invalid retention period never deletes backups: " + days);
        }
        var concurrent = await Task.WhenAll(manager.CleanupExpiredAsync(30, now: now), manager.CleanupExpiredAsync(30, now: now));
        assert(concurrent.Sum(item => item.DeletedCount) == 1 && concurrent.All(item => item.Errors.Count == 0),
            "Concurrent automatic and manual cleanup are serialized without double deletion");

        var linkedBackup = Create(now.AddDays(-40), content: "keep entire linked backup");
        await CreateLinkAsync(Path.Combine(linkedBackup, "external link"), outside);
        var cleanSibling = Create(now.AddDays(-40));
        result = await manager.CleanupExpiredAsync(30, now: now);
        assert(result.DeletedCount == 1 && result.Errors.Count == 1 && Directory.Exists(linkedBackup)
            && File.Exists(Path.Combine(linkedBackup, "nested", "saved.txt")) && !Directory.Exists(cleanSibling)
            && File.ReadAllText(sentinel) == "never clean outside Backups",
            "A junction anywhere inside a backup preserves the whole backup and its external target");
        summary = await manager.GetSummaryAsync(30, now: now);
        assert(summary.ExpiredCount == 0 && summary.RetainedCount == 4 && summary.Errors.Count == 1,
            "Linked backups are excluded from the cleanable count and reported as retained");
        var linkedRoot = Path.Combine(root, "linked-application");
        Directory.CreateDirectory(linkedRoot);
        await CreateLinkAsync(Path.Combine(linkedRoot, "Backups"), outside);
        var rootRejected = false;
        try { await new BackupManager(linkedRoot).CleanupExpiredAsync(30, now: now); }
        catch (IOException) { rootRejected = true; }
        assert(rootRejected && File.ReadAllText(sentinel) == "never clean outside Backups",
            "Cleanup refuses a Backups directory redirected by a junction");

        var merge = Create(now, "WinMerge-", "old comparison tool");
        var allBackups = await manager.GetSummaryAsync(BackupManager.MaximumRetentionDays, now: now);
        var cleanable = new[] { recent, boundary, future, merge };
        assert(allBackups.ExpiredCount == 0 && allBackups.CleanableCount == 4
            && allBackups.CleanablePaths.Order().SequenceEqual(cleanable.Order())
            && allBackups.CleanableBytes == cleanable.Sum(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length)),
            "All-cleanup preview includes fresh/future working and WinMerge backups even when none are expired");
        var createdAfterConfirmation = Create(now, content: "created after cleanup preview");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await manager.CleanupAllAsync(allBackups.CleanablePaths, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && cleanable.All(Directory.Exists), "Cancelling all-backup cleanup before execution deletes nothing");
        }
        result = await manager.CleanupAllAsync(allBackups.CleanablePaths);
        assert(result.DeletedCount == 4 && result.DeletedBytes == allBackups.CleanableBytes && result.Errors.Count == 0
            && cleanable.All(path => !Directory.Exists(path)) && Directory.Exists(createdAfterConfirmation),
            "Confirmed all-backup cleanup ignores age and leaves subsequently created backups intact");
        assert(Directory.Exists(linkedBackup) && File.Exists(unknown) && Directory.Exists(unknownDirectory)
            && Directory.Exists(invalid) && File.ReadAllText(sentinel) == "never clean outside Backups"
            && applicationDirectories.All(name => File.ReadAllText(Path.Combine(root, name, "keep.txt")) == name),
            "All cleanup retains unknown/linked content and never touches working data, cache, or active tools");
        result = await manager.CleanupAllAsync(allBackups.CleanablePaths);
        assert(result.DeletedCount == 0 && result.Errors.Count == 0,
            "Already removed backup targets are skipped when a confirmed list becomes stale");

        var externalBackup = Path.Combine(outside, Name(now, ""));
        var nestedBackup = Path.Combine(unknownDirectory, Name(now, ""));
        foreach (var path in new[] { externalBackup, nestedBackup })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "keep.txt"), "not a direct application backup");
        }
        result = await manager.CleanupAllAsync([externalBackup, nestedBackup, linkedBackup, unknown]);
        assert(result.DeletedCount == 0 && result.Errors.Count == 3 && File.Exists(unknown)
            && Directory.Exists(linkedBackup) && new[] { externalBackup, nestedBackup }.All(Directory.Exists),
            "Manual cleanup revalidates direct-child scope and links even for explicitly supplied targets");
        rootRejected = false;
        try { await new BackupManager(linkedRoot).CleanupAllAsync([createdAfterConfirmation]); }
        catch (IOException) { rootRejected = true; }
        assert(rootRejected && Directory.Exists(createdAfterConfirmation) && File.Exists(sentinel),
            "All-backup cleanup also refuses a redirected Backups root");

        var remaining = await manager.GetSummaryAsync(30, now: now);
        var parallelAll = await Task.WhenAll(manager.CleanupAllAsync(remaining.CleanablePaths), manager.CleanupAllAsync(remaining.CleanablePaths));
        assert(parallelAll.Sum(item => item.DeletedCount) == 1 && parallelAll.All(item => item.Errors.Count == 0),
            "Concurrent all-backup cleanups share the existing lock and delete confirmed backups once");
    }

    private static string Name(DateTimeOffset created, string prefix) => prefix + created.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");

    private static async Task CreateLinkAsync(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        // Junction creation needs no administrator or Developer Mode permissions on Windows.
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{path.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create test junction");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await error + await output);
    }
}
