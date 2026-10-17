using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SVNRevDesk.Core.Comparison;
using SVNRevDesk.Core.Services;

internal static class EmbeddedWinMergeTests
{
    public static async Task RunAsync(string root, Action<bool, string> assert)
    {
        var fresh = Path.Combine(root, "便携 @ 客户端");
        Directory.CreateDirectory(fresh);
        var legacyIni = Path.Combine(fresh, "WinMerge.ini");
        var ini = Path.Combine(fresh, "Tools", "WinMerge", "WinMerge.ini");
        const string preferences = "[WinMerge]\r\nSettings/TabSize=7\r\n";
        File.WriteAllText(legacyIni, preferences, Encoding.Unicode);
        var executable = await WinMergeExecutable.ResolveAsync("", fresh);
        var package = Path.GetDirectoryName(executable)!;
        assert(executable == Path.Combine(fresh, "Tools", "WinMerge", "WinMergeU.exe")
            && File.Exists(Path.Combine(package, "Languages", "ChineseSimplified.po"))
            && File.Exists(Path.Combine(package, "WinIMerge", "WinIMergeLib.dll"))
            && File.Exists(Path.Combine(package, "COPYING"))
            && File.Exists(Path.Combine(package, "Source", "winmerge-2.16.58.2-full-src.7z")),
            "Embedded WinMerge extracts the official client, Chinese UI, image support, license and matching source beside the EXE");
        assert(FileVersionInfo.GetVersionInfo(executable).FileVersion == "2.16.58.2",
            "The bundled WinMerge binary matches the pinned official version");
        assert(!File.Exists(legacyIni) && File.ReadAllText(ini) == preferences,
            "Existing application-root WinMerge settings migrate intact into Tools/WinMerge");
        var timestamp = File.GetLastWriteTimeUtc(executable);
        File.WriteAllText(Path.Combine(package, "user-note.txt"), "keep extra content");
        await WinMergeExecutable.ResolveAsync(null, fresh);
        assert(File.GetLastWriteTimeUtc(executable) == timestamp && !Directory.Exists(Path.Combine(fresh, "Backups")),
            "A complete WinMerge package is reused without rewriting binaries or creating backups");

        var inputs = Path.Combine(fresh, "输入 , @ 路径");
        Directory.CreateDirectory(inputs);
        var left = Path.Combine(inputs, "服务器 版本.txt");
        var right = Path.Combine(inputs, "本地 & 版本.txt");
        File.WriteAllText(left, "first line\n中文内容\n");
        File.Copy(left, right);
        var start = WinMergeExecutable.CreateStartInfo(executable, left, right, "服务器 r123", "本地文件", fresh);
        assert(start.ArgumentList.Contains("/wl") && start.ArgumentList.Contains("/wr")
            && start.ArgumentList.Contains(ini) && !start.ArgumentList.Contains(legacyIni)
            && start.ArgumentList.TakeLast(2).SequenceEqual(new[] { left, right })
            && start.Environment["TEMP"] == Path.Combine(fresh, "Temp", "WinMerge"),
            "Comparison arguments preserve titles and special paths, protect both inputs and keep preferences/temp portable");
        assert(await CompareAsync(executable, left, right, fresh) == 0,
            "The actual bundled WinMerge starts and reports identical UTF-8 files correctly");
        File.AppendAllText(right, "local edit\n");
        assert(await CompareAsync(executable, left, right, fresh) == 1
            && File.ReadAllText(left) == "first line\n中文内容\n"
            && File.ReadAllText(right) == "first line\n中文内容\nlocal edit\n",
            "WinMerge detects real content differences without altering either input");
        assert(File.Exists(ini) && !File.Exists(legacyIni),
            "WinMerge writes preferences in its tool directory without recreating an application-root INI");

        var savedPreferences = File.ReadAllBytes(ini);
        var missingLibrary = Path.Combine(package, "WinIMerge", "WinIMergeLib.dll");
        File.Delete(missingLibrary);
        await WinMergeExecutable.ResolveAsync("", fresh);
        assert(File.Exists(missingLibrary) && Directory.EnumerateFiles(Path.Combine(fresh, "Backups"), "user-note.txt", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path) == "keep extra content"),
            "Missing WinMerge dependencies trigger a full repair that preserves the old directory");
        assert(File.ReadAllBytes(ini).SequenceEqual(savedPreferences),
            "Tool repair preserves the active WinMerge settings byte for byte");
        assert(!Directory.EnumerateDirectories(fresh, ".winmerge-extract-*").Any()
            && !File.Exists(Path.Combine(fresh, ".winmerge-install.lock")),
            "WinMerge installation cleans its staging directory and process lock");
        var concurrentDirectory = Path.Combine(root, "concurrent");
        var resolved = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => WinMergeExecutable.ResolveAsync("", concurrentDirectory)));
        assert(resolved.Distinct().Count() == 1 && Directory.Exists(Path.Combine(concurrentDirectory, "Tools", "WinMerge")),
            "Concurrent comparison requests share one complete WinMerge installation");
        var customDirectory = Path.Combine(root, "custom application");
        Directory.CreateDirectory(customDirectory);
        var custom = Path.Combine(customDirectory, "custom-diff.exe");
        File.WriteAllText(custom, "test path only");
        assert(await WinMergeExecutable.ResolveAsync("custom-diff.exe", customDirectory) == custom
            && !Directory.Exists(Path.Combine(customDirectory, "Tools")),
            "A configured application-relative comparison tool takes priority and skips extraction");
        start = WinMergeExecutable.CreateStartInfo(custom, left, right, "left", "right", customDirectory);
        assert(start.ArgumentList.SequenceEqual(new[] { left, right }),
            "Custom comparison tools retain the existing two-file argument contract");
        var missingCustom = false;
        try { await WinMergeExecutable.ResolveAsync("missing.exe", customDirectory); }
        catch (FileNotFoundException) { missingCustom = true; }
        assert(missingCustom && !Directory.Exists(Path.Combine(customDirectory, "Tools")),
            "A missing custom tool reports the setting error instead of silently launching another tool");
        var cancelledDirectory = Path.Combine(root, "cancelled");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await WinMergeExecutable.ResolveAsync("", cancelledDirectory, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            assert(cancelled && !Directory.Exists(cancelledDirectory), "Cancelling WinMerge preparation leaves no partial installation");
        }
        var blockedDirectory = Path.Combine(root, "blocked");
        Directory.CreateDirectory(Path.Combine(blockedDirectory, "Tools"));
        File.WriteAllText(Path.Combine(blockedDirectory, "Tools", "WinMerge"), "preserve this file");
        var blocked = false;
        try { await WinMergeExecutable.ResolveAsync("", blockedDirectory); }
        catch (InvalidOperationException) { blocked = true; }
        assert(blocked && File.ReadAllText(Path.Combine(blockedDirectory, "Tools", "WinMerge")) == "preserve this file"
            && !Directory.EnumerateDirectories(blockedDirectory, ".winmerge-extract-*").Any(),
            "Failed WinMerge installation preserves existing paths and removes staged files");
        // New process verifies content hashes, even if every dependency file still exists.
        var corrupt = Path.Combine(package, "Filters", "VisualStudio_loose.flt");
        if (!File.Exists(corrupt)) corrupt = Directory.EnumerateFiles(Path.Combine(package, "Filters")).First();
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corrupt)));
        File.WriteAllText(corrupt, "modified dependency");
        assert(await ProbeInAnotherProcessAsync(fresh) == 0
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corrupt))) == expectedHash
            && Directory.EnumerateFiles(Path.Combine(fresh, "Backups"), Path.GetFileName(corrupt), SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path) == "modified dependency"),
            "A new process detects altered WinMerge files by hash and repairs them while retaining old contents");
        assert(File.ReadAllText(ini).Contains("Settings/TabSize=7", StringComparison.Ordinal) && !File.Exists(legacyIni),
            "A new process retains tool-directory preferences when repairing changed dependencies");
        var cleanup = await new BackupManager(fresh).GetSummaryAsync(1, now: DateTimeOffset.Now.AddDays(2));
        assert(cleanup.ExpiredCount == 2, "WinMerge upgrade backups participate in the configured retention policy");
    }

    public static async Task<int> ProbeAsync(string directory)
    {
        var executable = await WinMergeExecutable.ResolveAsync("", directory);
        var inputs = Path.Combine(directory, "Temp", "probe");
        Directory.CreateDirectory(inputs);
        var left = Path.Combine(inputs, "left.txt");
        var right = Path.Combine(inputs, "right.txt");
        File.WriteAllText(left, "中文 probe\n");
        File.WriteAllText(right, "different probe\n");
        return await CompareAsync(executable, left, right, directory) == 1 ? 0 : 1;
    }

    private static async Task<int> CompareAsync(string executable, string left, string right, string directory)
    {
        var start = WinMergeExecutable.CreateStartInfo(executable, left, right, "server", "local", directory);
        start.ArgumentList.Insert(0, "/noninteractive");
        start.ArgumentList.Insert(0, "/enableexitcode");
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start bundled WinMerge");
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return process.ExitCode;
    }

    private static async Task<int> ProbeInAnotherProcessAsync(string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SVNRevDesk.IntegrationTests.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("--probe-bundled-winmerge"); start.ArgumentList.Add(directory);
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start WinMerge probe");
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) Console.Error.WriteLine(await error);
        return process.ExitCode;
    }
}
