using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Services;

public sealed class UserSettings
{
    private const int CurrentSettingsVersion = 2;
    public int SettingsVersion { get; set; }
    public string SvnExecutable { get; set; } = "svn.exe";
    public string ExternalDiffExecutable { get; set; } = "";
    public bool AutoRefresh { get; set; } = true;
    public int RefreshSeconds { get; set; } = 3600;
    public bool AutoBackupCleanup { get; set; } = true;
    public int BackupRetentionDays { get; set; } = BackupManager.DefaultRetentionDays;
    public WindowCloseBehavior CloseBehavior { get; set; } = WindowCloseBehavior.Ask;
    public List<string> RecentLocations { get; set; } = [];
    public bool UseCardView { get; set; }
    public bool PreviewEnabled { get; set; }
    public string LastLocation { get; set; } = "";
    public bool LastLocationIsLocal { get; set; }
    public string LastRelativeDirectory { get; set; } = "";
    public List<string> ExpandedDirectories { get; set; } = [];
    public string LastExportDirectory { get; set; } = "";

    public static string DataDirectory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
    // Keep the original application name here so pre-portable settings can still be imported.
    private static string LegacyDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SvnClient");
    private string settingsPath = Path.Combine(DataDirectory, "settings.json");

    [JsonIgnore] public string StorageDirectory => Path.GetDirectoryName(settingsPath)!;
    [JsonIgnore] public string CacheDirectory => Path.Combine(StorageDirectory, "Cache");
    [JsonIgnore] public string BackupDirectory => Path.Combine(StorageDirectory, "Backups");
    [JsonIgnore] public string TemporaryDirectory => Path.Combine(StorageDirectory, "Temp");
    [JsonIgnore] public string SvnConfigurationDirectory => Path.Combine(StorageDirectory, "SvnConfig");
    [JsonIgnore] public string LocalIgnoreConfigurationPath { get; private set; } = LocalIgnoreStore.DefaultPath;
    [JsonIgnore] public string? LegacySvnConfigurationDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subversion");
    [JsonIgnore] public string? LoadNotice { get; private set; }

    public UserSettings() { }
    public UserSettings(string storageDirectory)
    {
        settingsPath = Path.Combine(Path.GetFullPath(storageDirectory), "settings.json");
        LocalIgnoreConfigurationPath = Path.Combine(Path.GetFullPath(storageDirectory), "local-ignores.json");
        LegacySvnConfigurationDirectory = null;
    }

    public static UserSettings Load(string? storageDirectory = null, string? legacyDirectory = null)
    {
        var directory = Path.GetFullPath(storageDirectory ?? DataDirectory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var legacyPath = Path.Combine(legacyDirectory ?? (storageDirectory == null ? LegacyDataDirectory : directory), "settings.json");
            var migrate = !File.Exists(path) && File.Exists(legacyPath);
            var sourcePath = migrate ? legacyPath : path;
            var settings = File.Exists(sourcePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(sourcePath)) ?? new() : new();
            if (settings.SettingsVersion < 1)
            {
                // Migrate the previous default once; later user choices (including 60 seconds) remain intact.
                if (settings.RefreshSeconds == 60) settings.RefreshSeconds = 600;
                settings.SettingsVersion = 1;
            }
            if (settings.SettingsVersion < CurrentSettingsVersion)
            {
                // Upgrade the old ten-minute default once; keep other custom intervals.
                if (settings.RefreshSeconds == 600) settings.RefreshSeconds = 3600;
                settings.SettingsVersion = CurrentSettingsVersion;
            }
            settings.settingsPath = path;
            if (settings.BackupRetentionDays is < 1 or > BackupManager.MaximumRetentionDays)
                settings.BackupRetentionDays = BackupManager.DefaultRetentionDays;
            if (!Enum.IsDefined(settings.CloseBehavior)) settings.CloseBehavior = WindowCloseBehavior.Ask;
            if (storageDirectory != null)
            {
                settings.LegacySvnConfigurationDirectory = null;
                settings.LocalIgnoreConfigurationPath = Path.Combine(directory, "local-ignores.json");
            }
            if (migrate)
            {
                try { settings.Save(); }
                catch (IOException ex) { settings.LoadNotice = $"已读取旧配置，但迁移到程序目录失败：{ex.Message}"; }
            }
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(directory) { LoadNotice = $"读取配置失败：{ex.Message}",
                LocalIgnoreConfigurationPath = storageDirectory == null ? LocalIgnoreStore.DefaultPath : Path.Combine(directory, "local-ignores.json") };
        }
    }

    public void Save()
    {
        try
        {
            // Explicit choices saved by this release must not be treated as old defaults.
            SettingsVersion = Math.Max(SettingsVersion, CurrentSettingsVersion);
            Directory.CreateDirectory(StorageDirectory);
            var temporary = settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"无法保存配置到程序目录：{StorageDirectory}。请将程序放在可写入的目录。", ex);
        }
    }

    public void Remember(string location)
    {
        RecentLocations.RemoveAll(item => item.Equals(location, StringComparison.OrdinalIgnoreCase));
        RecentLocations.Insert(0, location);
        if (RecentLocations.Count > 10) RecentLocations.RemoveRange(10, RecentLocations.Count - 10);
        Save();
    }
}
