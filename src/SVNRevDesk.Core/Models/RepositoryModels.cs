using SVNRevDesk.Core.Formatting;

namespace SVNRevDesk.Core.Models;

public sealed record SvnLock(string Owner, string Comment, DateTimeOffset? Created);

public sealed record SvnInfo(
    string Path, string Url, string RepositoryRoot, string RepositoryId,
    bool IsDirectory, long Revision, long? ChangedRevision,
    string Author, string? WorkingCopyRoot);

public sealed record ServerEntry(
    string Name, bool IsDirectory, long? Size, long? ChangedRevision,
    string Author, DateTimeOffset? ChangedAt, SvnLock? Lock);

public sealed record WorkingStatus(
    string Path, string Item, string Properties, string RepositoryItem,
    string RepositoryProperties, long? BaseRevision, long? ChangedRevision,
    bool TreeConflict, bool Switched, SvnLock? LocalLock, SvnLock? ServerLock)
{
    public string? MovedFrom { get; init; }
    public string? MovedTo { get; init; }
    public bool Copied { get; init; }
    public bool IsLocallyIgnored { get; init; }
    public bool HasLocalChanges => Item is "modified" or "added" or "deleted" or "replaced" or "conflicted"
        || Properties is "modified" or "conflicted" || TreeConflict;

    public bool HasServerChanges => RepositoryItem is "modified" or "added" or "deleted" or "replaced"
        || RepositoryProperties == "modified";
}

public sealed record RepositorySession(
    string Url, string RepositoryRoot, string RepositoryId, string? LocalRoot);

public sealed record RepositorySnapshot(
    long Revision, DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, WorkingStatus> Statuses,
    IReadOnlyDictionary<string, SvnInfo> LocalInfo);

public sealed record BrowserEntry(
    string Name, string RelativePath, string Url, string? LocalPath,
    bool IsDirectory, FileState State, long? BaseRevision,
    long? LocalRevision, long? ServerRevision, long? Size,
    string Author, DateTimeOffset? ChangedAt, SvnLock? Lock,
    string Detail, string Summary)
{
    public bool IsUnversioned { get; init; }
    public string? LocalIgnoreRulePath { get; init; }
    public bool IsLocallyIgnored => LocalIgnoreRulePath != null;
    public string Kind => IsDirectory ? "文件夹" : "文件";
    public string StateText => State.DisplayName();
    public bool HasDifferences => State is not (FileState.Equal or FileState.Unchecked or FileState.Ignored)
        || IsDirectory && State == FileState.Equal && Summary != "已检查项无差异" && Summary.Length > 0;
    public string BaseRevisionText => FormatRevision(BaseRevision);
    public string LocalRevisionText => FormatRevision(LocalRevision);
    public string ServerRevisionText => FormatRevision(ServerRevision);
    public string SizeText => FileSizeFormatter.Format(Size);
    public string SizeDetail => Size.HasValue ? $"{Size.Value:N0} 字节（按 1024 换算）" : "大小未知";
    public string ChangedAtText => ChangedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string LockOwner => Lock?.Owner ?? "";
    public string LockDetail => Lock == null ? "未锁定" : $"锁定人：{Lock.Owner}\n时间：{Lock.Created?.ToLocalTime():yyyy-MM-dd HH:mm}\n说明：{Lock.Comment}";
    public bool ExistsLocally => LocalPath != null && (IsDirectory ? Directory.Exists(LocalPath) : File.Exists(LocalPath));
    private static string FormatRevision(long? revision) => revision.HasValue ? $"r{revision}" : "—";
}

public sealed record HistoryEntry(long Revision, string Author, DateTimeOffset? Date, string Message)
{
    public string DateText => Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
}
