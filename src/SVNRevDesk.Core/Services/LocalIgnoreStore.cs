using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

// Machine/user-local absolute paths: never store these in working-copy properties or files.
public sealed class LocalIgnoreStore
{
    private sealed record Configuration(int Version, string[] Paths);
    private string[] paths = [];
    public string ConfigurationPath { get; }
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVNRevDesk", "local-ignores.json");
    public LocalIgnoreStore(string configurationPath)
    {
        ConfigurationPath = Path.GetFullPath(configurationPath);
        Reload();
    }

    public void Reload()
    {
        if (!File.Exists(ConfigurationPath)) { paths = []; return; }
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(ConfigurationPath));
        if (config is not { Version: 1, Paths: not null } || config.Paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new IOException("本机忽略配置无效，请检查：" + ConfigurationPath);
        paths = config.Paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(path => path.Length).ToArray();
    }

    public string? FindRule(string path)
    {
        path = Normalize(path);
        return paths.FirstOrDefault(rule => Same(path, rule) || RepositoryService.IsDescendant(path, rule));
    }
    public bool Matches(string path) => FindRule(path) != null;
    public bool HasRules => paths.Length > 0;
    public bool Affects(string path) => Matches(path) || paths.Any(rule => RepositoryService.IsDescendant(rule, Normalize(path)));

    public void Set(string path, bool ignored)
    {
        path = Normalize(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigurationPath.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\SVNRevDesk.LocalIgnores." + hash);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("另一实例正在保存本机忽略配置，请稍后重试。");
            Reload();
            if (!ignored && FindRule(path) is { } ancestor && !Same(path, ancestor))
                throw new InvalidOperationException("此项目继承上级目录的本机忽略，请在该目录上取消忽略：" + ancestor);
            var updated = paths.Where(rule => !Same(rule, path)).ToList();
            if (ignored) updated.Add(path);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
            var temporary = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Configuration(1, updated.ToArray()), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, ConfigurationPath, overwrite: true);
                paths = updated.OrderByDescending(rule => rule.Length).ToArray();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    public WorkingStatus Apply(WorkingStatus status) => status with
    {
        IsLocallyIgnored = status.Item is "unversioned" or "ignored" && Matches(status.Path)
    };

    public RepositorySnapshot Apply(RepositorySnapshot snapshot) => snapshot with
    {
        Statuses = snapshot.Statuses.ToDictionary(pair => pair.Key, pair => Apply(pair.Value), StringComparer.OrdinalIgnoreCase)
    };

    public IReadOnlyList<BrowserEntry> Apply(RepositorySnapshot snapshot, IReadOnlyList<BrowserEntry> entries) => entries.Select(entry =>
    {
        var status = entry.LocalPath == null ? null : snapshot.Statuses.GetValueOrDefault(entry.LocalPath);
        var rule = entry.LocalPath == null || entry.ServerRevision != null || entry.BaseRevision != null
            || status != null && status.Item is not ("unversioned" or "ignored") ? null : FindRule(entry.LocalPath);
        if (rule != null) return entry with { State = FileState.Ignored, LocalIgnoreRulePath = rule,
            Detail = LocalDetail, IsUnversioned = status?.Item != "ignored" };
        return !entry.IsLocallyIgnored ? entry : entry with { LocalIgnoreRulePath = null,
            State = FileStateClassifier.Classify(entry.ServerRevision != null, entry.ExistsLocally, status, true),
            Detail = RepositoryService.GetDetail(status, true) };
    }).ToList();

    public const string LocalDetail = "仅本机忽略，不参与本软件的添加或提交；配置保存在当前 Windows 用户的本机目录中，不上传 SVN。";
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
