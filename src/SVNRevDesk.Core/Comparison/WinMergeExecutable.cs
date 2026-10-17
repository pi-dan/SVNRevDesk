using System.Diagnostics;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Core.Comparison;

public static class WinMergeExecutable
{
    public const string DefaultName = "WinMergeU.exe";
    private static readonly EmbeddedToolPackage Package = new("WinMerge", "SVNRevDesk.BundledWinMerge",
        Path.Combine("Tools", "WinMerge"), DefaultName, "WinMerge", "winmerge", preservedFiles: ["WinMerge.ini"]);

    public static async Task<string> ResolveAsync(string? configuredPath, string? applicationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var requested = configuredPath?.Trim();
        if (!string.IsNullOrEmpty(requested) && !requested.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            var custom = Path.GetFullPath(requested, directory);
            if (!File.Exists(custom)) throw new FileNotFoundException("自定义比较工具不存在，请修改设置或留空使用内置 WinMerge。", custom);
            return custom;
        }
        await Package.EnsureInstalledAsync(directory, cancellationToken);
        PrepareSettingsPath(directory);
        return Path.Combine(directory, "Tools", "WinMerge", DefaultName);
    }

    public static string PrepareSettingsPath(string applicationDirectory)
    {
        var directory = Path.GetFullPath(applicationDirectory);
        var path = Path.Combine(directory, "Tools", "WinMerge", "WinMerge.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var legacy = Path.Combine(directory, "WinMerge.ini");
        if (File.Exists(legacy) && !File.Exists(path))
        {
            try { File.Move(legacy, path); }
            // Another instance may have completed the same migration first.
            catch (IOException) when (File.Exists(path)) { }
        }
        return path;
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string leftPath, string rightPath,
        string leftTitle, string rightTitle, string applicationDirectory)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (Path.GetFileName(executable).Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            // Keep preferences in the tool directory and prevent edits to either comparison input.
            start.ArgumentList.Add("/inifile"); start.ArgumentList.Add(PrepareSettingsPath(applicationDirectory));
            start.ArgumentList.Add("/s-");
            start.ArgumentList.Add("/u"); start.ArgumentList.Add("/wl"); start.ArgumentList.Add("/wr");
            var temporaryDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "Temp", "WinMerge");
            Directory.CreateDirectory(temporaryDirectory);
            start.ArgumentList.Add("/cfg"); start.ArgumentList.Add("Settings/UseSystemTempPath=0");
            start.ArgumentList.Add("/cfg"); start.ArgumentList.Add("Settings/CustomTempPath=" + temporaryDirectory);
            start.Environment["TEMP"] = temporaryDirectory;
            start.Environment["TMP"] = temporaryDirectory;
            start.ArgumentList.Add("/dl"); start.ArgumentList.Add(leftTitle);
            start.ArgumentList.Add("/dr"); start.ArgumentList.Add(rightTitle);
        }
        start.ArgumentList.Add(leftPath); start.ArgumentList.Add(rightPath);
        return start;
    }
}
