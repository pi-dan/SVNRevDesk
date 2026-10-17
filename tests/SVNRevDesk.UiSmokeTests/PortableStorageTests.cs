using System.IO;
using System.Text.Json;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;

namespace SVNRevDesk.UiSmokeTests;

internal static class PortableStorageTests
{
    public static void Run(string output)
    {
        var root = Path.Combine(output, "portable-settings-" + Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(root, "legacy");
        var portable = Path.Combine(root, "application");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), """
            { "SettingsVersion": 1, "RefreshSeconds": 120, "LastLocation": "https://example.invalid/svn/repository",
              "LastRelativeDirectory": "文档/资料", "UseCardView": true }
            """);
        var settings = UserSettings.Load(portable, legacy);
        if (!settings.AutoBackupCleanup || settings.BackupRetentionDays != 30)
            throw new InvalidOperationException("Existing configurations did not receive the default 30-day automatic backup policy");
        if (!File.Exists(Path.Combine(portable, "settings.json")) || settings.RefreshSeconds != 120
            || settings.LastRelativeDirectory != "文档/资料" || !settings.UseCardView || settings.LoadNotice != null)
            throw new InvalidOperationException("Legacy configuration did not migrate intact to the portable directory");
        settings.LastExportDirectory = Path.Combine(root, "downloads");
        settings.RefreshSeconds = 300;
        settings.AutoBackupCleanup = false;
        settings.BackupRetentionDays = 90;
        settings.PreviewEnabled = true;
        settings.Save();
        var loaded = UserSettings.Load(portable, legacy);
        if (loaded.RefreshSeconds != 300 || loaded.LastExportDirectory != settings.LastExportDirectory
            || loaded.AutoBackupCleanup || loaded.BackupRetentionDays != 90 || !loaded.PreviewEnabled)
            throw new InvalidOperationException("Portable configuration did not take priority or retain the export destination");
        using (var model = new MainViewModel(loaded))
        {
            if (loaded.CacheDirectory != Path.Combine(portable, "Cache")
                || loaded.BackupDirectory != Path.Combine(portable, "Backups")
                || loaded.TemporaryDirectory != Path.Combine(portable, "Temp")
                || model.Runner.ConfigurationDirectory != Path.Combine(portable, "SvnConfig")
                || model.Runner.ConfigurationTemplateDirectory != null || !model.Preview.IsEnabled)
                throw new InvalidOperationException("Application data was not rooted in the configured portable directory");
        }
        var moved = Path.Combine(root, "moved-application");
        Directory.CreateDirectory(moved);
        File.Copy(Path.Combine(portable, "settings.json"), Path.Combine(moved, "settings.json"));
        var relocated = UserSettings.Load(moved);
        if (relocated.LastRelativeDirectory != loaded.LastRelativeDirectory || relocated.CacheDirectory != Path.Combine(moved, "Cache")
            || relocated.AutoBackupCleanup || relocated.BackupRetentionDays != 90 || !relocated.PreviewEnabled)
            throw new InvalidOperationException("Moving the portable directory lost settings or retained the previous storage root");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(portable, "settings.json")));
        if (json.RootElement.TryGetProperty("StorageDirectory", out _) || json.RootElement.TryGetProperty("CacheDirectory", out _)
            || json.RootElement.TryGetProperty("LegacySvnConfigurationDirectory", out _))
            throw new InvalidOperationException("Derived paths were persisted and would prevent relocation");
        var previousWorkingDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            if (UserSettings.DataDirectory != Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)))
                throw new InvalidOperationException("Data directory followed the process working directory instead of the executable");
        }
        finally { Environment.CurrentDirectory = previousWorkingDirectory; }
        File.WriteAllText(Path.Combine(portable, "settings.json"), "{\"RefreshSeconds\":60}");
        var oldDefault = UserSettings.Load(portable);
        if (oldDefault.RefreshSeconds != 3600) throw new InvalidOperationException("The old refresh default was not migrated to one hour");
        oldDefault.RefreshSeconds = 60;
        oldDefault.Save();
        if (UserSettings.Load(portable).RefreshSeconds != 60)
            throw new InvalidOperationException("A later custom refresh interval was replaced");
        File.WriteAllText(Path.Combine(portable, "settings.json"), "{\"SettingsVersion\":1,\"RefreshSeconds\":600,\"AutoRefresh\":false}");
        var tenMinuteDefault = UserSettings.Load(portable);
        if (tenMinuteDefault.RefreshSeconds != 3600 || tenMinuteDefault.AutoRefresh)
            throw new InvalidOperationException("The ten-minute default was not upgraded while preserving the refresh switch");
        tenMinuteDefault.RefreshSeconds = 600;
        tenMinuteDefault.Save();
        if (UserSettings.Load(portable).RefreshSeconds != 600)
            throw new InvalidOperationException("A later explicit ten-minute interval was replaced");
        var newChoice = new UserSettings(portable) { RefreshSeconds = 600 };
        newChoice.Save();
        if (UserSettings.Load(portable).RefreshSeconds != 600)
            throw new InvalidOperationException("An explicit interval saved by a new settings instance was replaced");
        File.WriteAllText(Path.Combine(portable, "settings.json"), "{\"BackupRetentionDays\":0,\"AutoBackupCleanup\":false}");
        var invalidRetention = UserSettings.Load(portable);
        if (invalidRetention.BackupRetentionDays != 30 || invalidRetention.AutoBackupCleanup)
            throw new InvalidOperationException("Invalid backup retention did not recover to 30 days while retaining the cleanup switch");
        var blockedDirectory = Path.Combine(root, "blocked-directory");
        File.WriteAllText(blockedDirectory, "existing file prevents directory creation");
        var blocked = UserSettings.Load(blockedDirectory, legacy);
        if (blocked.LoadNotice == null || blocked.LastRelativeDirectory != "文档/资料"
            || File.ReadAllText(blockedDirectory) != "existing file prevents directory creation")
            throw new InvalidOperationException("Failed migration discarded settings, hid the failure, or overwrote an existing path");
        Console.WriteLine("PASS: legacy settings migration, portable data paths, export destination memory, relocation, and executable-relative storage.");
    }
}
