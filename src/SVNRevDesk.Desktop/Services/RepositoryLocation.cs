using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SVNRevDesk.Desktop.Services;

public sealed record SavedRepositoryTab(string Location, bool IsLocal, string RelativeDirectory, List<string> ExpandedDirectories);
public sealed record FavoriteLocation(string Name, string Location, bool IsLocal, string RelativeDirectory = "");
public sealed record WorkingCopyLink(string LocalPath, string Url, string RepositoryId);

public static class RepositoryLocation
{
    public static (string Location, bool IsLocal) Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new ArgumentException("请输入工作副本路径或仓库 URL。");
        if (!text.Contains("://", StringComparison.Ordinal) && Path.IsPathFullyQualified(text))
            return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)), true);
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "svn" or "svn+ssh" or "file")
        {
            if (uri.UserInfo.Length > 0) throw new ArgumentException("请使用不含账号密码的仓库 URL，并在“本次会话登录”中设置凭据。");
            return (uri.AbsolutePath == "/" ? uri.AbsoluteUri : uri.AbsoluteUri.TrimEnd('/'), false);
        }
        if (!Path.IsPathFullyQualified(text)) throw new ArgumentException("请输入完整的本地路径，或受支持的 SVN URL。");
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)), true);
    }

    public static string Key(string location, bool isLocal) => isLocal
        ? "local:" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)).ToUpperInvariant()
        : "url:" + Parse(location).Location;

    public static string CacheDirectory(string root, string location, bool isLocal) => Path.Combine(root, "Sessions",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(location, isLocal)))));

    public static string DisplayName(string location, bool isLocal)
    {
        var name = isLocal ? Path.GetFileName(location) : Uri.UnescapeDataString(new Uri(location).Segments.Last().Trim('/'));
        return string.IsNullOrEmpty(name) ? location : name;
    }
}
