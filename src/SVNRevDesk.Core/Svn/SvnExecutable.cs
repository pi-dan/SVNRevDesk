namespace SVNRevDesk.Core.Svn;

public static class SvnExecutable
{
    public const string DefaultName = "svn.exe";

    public static async Task<string> ResolveAsync(string? configuredPath, string? applicationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var customPath = GetCustomPath(configuredPath, directory);
        if (customPath != null && File.Exists(customPath)) return customPath;
        await BundledSvn.EnsureInstalledAsync(directory, cancellationToken);
        return Path.Combine(directory, "Tools", "Svn", "bin", DefaultName);
    }

    public static string Resolve(string? configuredPath, string? applicationDirectory = null)
    {
        var directory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var bundledPath = Path.Combine(directory, "Tools", "Svn", "bin", DefaultName);
        var customPath = GetCustomPath(configuredPath, directory);
        if (customPath != null && File.Exists(customPath)) return customPath;
        // An absolute path from the previous computer may no longer exist after relocation.
        if (File.Exists(bundledPath)) return bundledPath;
        return customPath ?? DefaultName;
    }

    private static string? GetCustomPath(string? configuredPath, string applicationDirectory)
    {
        var requested = configuredPath?.Trim();
        if (string.IsNullOrEmpty(requested)
            || requested.Equals(DefaultName, StringComparison.OrdinalIgnoreCase)
            || requested.Equals("svn", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetFullPath(requested, applicationDirectory);
    }
}
