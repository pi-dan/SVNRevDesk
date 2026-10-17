using System.IO;
using System.Text.Json;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Services;

// Only the last session is persisted. File contents and credentials are not part of this cache.
public sealed record SavedDirectory(long Revision, DateTimeOffset CheckedAt, IReadOnlyList<BrowserEntry> Entries, string Summary)
{
    public bool HasServerListing { get; init; } = true;
}

public sealed record SavedBrowsingSession(int Version, RepositorySession Session, RepositorySnapshot Snapshot,
    DateTimeOffset LastFullRefreshAt, IReadOnlyDictionary<string, SavedDirectory> Directories);

public sealed class BrowsingCacheStore(string cacheDirectory)
{
    private const int CurrentVersion = 1;
    private readonly string path = Path.Combine(cacheDirectory, "browsing-session.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };

    public SavedBrowsingSession? Load(string location, bool isLocal)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var saved = JsonSerializer.Deserialize<SavedBrowsingSession>(stream, JsonOptions);
        if (saved == null || saved.Version != CurrentVersion || saved.Session == null || saved.Snapshot == null
            || saved.Directories == null || saved.Snapshot.Statuses == null || saved.Snapshot.LocalInfo == null
            || !saved.Directories.ContainsKey("")) return null;

        var session = saved.Session;
        if (!IsRepositoryUrl(session.Url) || !IsRepositoryUrl(session.RepositoryRoot)
            || string.IsNullOrWhiteSpace(session.RepositoryId)) return null;
        if (isLocal)
        {
            if (session.LocalRoot == null || !Directory.Exists(location)
                || !Path.GetFullPath(location).TrimEnd('\\').Equals(session.LocalRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
        }
        else if (session.LocalRoot != null || !Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !uri.Equals(new Uri(session.Url))) return null;

        // Validate paths before they can become navigation or operation targets.
        foreach (var (relative, directory) in saved.Directories)
        {
            if (!IsRelativePath(relative) || directory?.Entries == null || directory.Summary == null) return null;
            foreach (var entry in directory.Entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name) || entry.Summary == null || entry.Detail == null
                    || entry.Name.Contains('/') || entry.Name.Contains('\\')
                    || entry.Name is "." or ".." || !IsRepositoryUrl(entry.Url)
                    || entry.RelativePath != (relative.Length == 0 ? entry.Name : relative + "/" + entry.Name)
                    || !string.Equals(entry.LocalPath, RepositoryService.GetLocalPath(session, entry.RelativePath), StringComparison.OrdinalIgnoreCase)) return null;
            }
        }
        if (session.LocalRoot != null && saved.Snapshot.Statuses.Keys.Concat(saved.Snapshot.LocalInfo.Keys)
            .Any(key => !key.Equals(session.LocalRoot, StringComparison.OrdinalIgnoreCase)
                && !RepositoryService.IsDescendant(key, session.LocalRoot))) return null;
        if (saved.Snapshot.Statuses.Values.Any(status => status == null)
            || saved.Snapshot.LocalInfo.Values.Any(info => info == null)) return null;

        // JSON dictionaries do not retain their comparer; Windows local paths are case insensitive.
        var snapshot = saved.Snapshot with
        {
            Statuses = saved.Snapshot.Statuses.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            LocalInfo = saved.Snapshot.LocalInfo.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
        return saved with { Snapshot = snapshot };
    }

    public void Save(SavedBrowsingSession session)
    {
        Directory.CreateDirectory(cacheDirectory);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, session with { Version = CurrentVersion }, JsonOptions);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsRepositoryUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "svn" or "svn+ssh" or "file" && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsRelativePath(string path) => !Path.IsPathRooted(path) && !path.Contains('\\')
        && path.Split('/').All(part => part is not ("." or ".."))
        && (path.Length == 0 || path.Split('/').All(part => part.Length > 0));
}
