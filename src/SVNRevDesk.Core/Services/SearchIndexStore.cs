using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

internal sealed record SearchIndexDocument(int Version, string Key, long Revision, DateTimeOffset ServerCheckedAt,
    DateTimeOffset? LocalCheckedAt, SearchMount[] Mounts, SearchServerNode[] Nodes, BrowserEntry[] Entries);

internal sealed class SearchIndexStore
{
    private static readonly JsonSerializerOptions Options = new() { IgnoreReadOnlyProperties = true };
    private readonly RepositorySession session;
    private readonly string key;
    public string Path { get; }

    public static string CachePath(string cacheDirectory, RepositorySession session, string? username = null) =>
        System.IO.Path.Combine(cacheDirectory, "Search", Key(session, username) + ".json");

    private static string Key(RepositorySession session, string? username) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        session.RepositoryId + "\n" + session.Url.TrimEnd('/') + "\n" + (session.LocalRoot?.ToUpperInvariant() ?? "") + "\n" + username)));

    public SearchIndexStore(string cacheDirectory, RepositorySession session, string? username)
    {
        this.session = session; key = Key(session, username); Path = CachePath(cacheDirectory, session, username);
    }

    public async Task<SearchIndexDocument?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(Path)) return null;
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var document = await JsonSerializer.DeserializeAsync<SearchIndexDocument>(stream, Options, token);
        if (document == null || document.Version != 1 || document.Key != key || document.Revision < 0 || document.Mounts == null
            || document.Nodes == null || document.Entries == null || document.Mounts.Any(mount => mount == null)
            || !document.Mounts.Any(mount => mount.RelativePath == "" && mount.Url == session.Url)
            || document.Mounts.Select(mount => mount.RelativePath).Distinct(StringComparer.Ordinal).Count() != document.Mounts.Length)
            throw new InvalidDataException("搜索索引格式或仓库标识无效。");
        foreach (var mount in document.Mounts)
        {
            ValidateRelative(mount.RelativePath, true); ValidateUrl(mount.Url);
            if (!new Uri(session.RepositoryRoot.TrimEnd('/') + "/").IsBaseOf(new Uri(mount.Url.TrimEnd('/') + "/")))
                throw new InvalidDataException("搜索索引的切换路径超出仓库。");
        }
        foreach (var node in document.Nodes)
        {
            if (node == null) throw new InvalidDataException("搜索索引包含空项目。");
            ValidateRelative(node.RelativePath); ValidateUrl(node.Url);
            var mount = OwningMount(document.Mounts, node.RelativePath);
            if (node.Entry == null || node.Entry.Name != node.RelativePath.Split('/')[^1]
                || node.Url != RepositoryService.AppendUrl(mount.Url, RelativeTo(node.RelativePath, mount.RelativePath)))
                throw new InvalidDataException("搜索索引中的目录清单无效。");
        }
        foreach (var entry in document.Entries)
        {
            if (entry == null) throw new InvalidDataException("搜索索引包含空项目。");
            ValidateRelative(entry.RelativePath); ValidateUrl(entry.Url);
            var mount = OwningMount(document.Mounts, entry.RelativePath);
            if (entry.Name != entry.RelativePath.Split('/')[^1] || entry.Detail == null || entry.Summary == null
                || entry.Url != RepositoryService.AppendUrl(mount.Url, RelativeTo(entry.RelativePath, mount.RelativePath))
                || !string.Equals(entry.LocalPath, RepositoryService.GetLocalPath(session, entry.RelativePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("搜索索引中的本地路径无效。");
        }
        if (document.Nodes.Select(node => node.RelativePath).Distinct(StringComparer.Ordinal).Count() != document.Nodes.Length)
            throw new InvalidDataException("搜索索引包含重复路径。");
        return document;
    }

    public async Task SaveAsync(SearchIndexDocument document, CancellationToken token)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, document with { Version = 1, Key = key }, Options, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void ValidateRelative(string relative, bool allowEmpty = false)
    {
        if (allowEmpty && relative == "") return;
        if (string.IsNullOrEmpty(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains('\\')
            || relative.Split('/').Any(part => part is "" or "." or ".." || part.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("索引路径必须位于当前打开范围内。");
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "svn" or "svn+ssh" or "file")
            || uri.UserInfo.Length > 0) throw new InvalidDataException("搜索索引包含无效的仓库地址。");
    }

    internal static SearchMount OwningMount(IEnumerable<SearchMount> mounts, string relative) => mounts
        .Where(mount => relative == mount.RelativePath || DirectoryRefresh.IsInside(relative, mount.RelativePath))
        .MaxBy(mount => mount.RelativePath.Length)!;

    internal static string RelativeTo(string path, string parent) => path == parent ? "" : parent.Length == 0 ? path : path[(parent.Length + 1)..];
}
