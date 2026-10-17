using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Core.Svn;

internal static class BundledSvn
{
    private static readonly EmbeddedToolPackage Package = new("SVN", "SVNRevDesk.BundledSvn",
        Path.Combine("Tools", "Svn"), "bin/svn.exe", "SvnClient", "svn", legacyRelativeDirectory: "Svn");

    public static Task EnsureInstalledAsync(string applicationDirectory, CancellationToken cancellationToken) =>
        Package.EnsureInstalledAsync(applicationDirectory, cancellationToken);
}
