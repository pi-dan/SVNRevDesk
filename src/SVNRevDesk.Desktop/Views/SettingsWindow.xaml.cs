using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Formatting;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly BackupManager backups;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? summaryCancellation;
    private BackupSummary? summary;
    private bool initializing = true;
    private bool cleaning;
    private bool closed;

    public string SvnExecutable => SvnPathBox.Text.Trim();
    public string ExternalDiffExecutable => DiffPathBox.Text.Trim();
    public int RefreshSeconds { get; private set; }
    public bool AutoBackupCleanup => AutoCleanupBox.IsChecked == true;
    public int BackupRetentionDays { get; private set; }
    public WindowCloseBehavior CloseBehavior => CloseBehaviorBox.SelectedValue is WindowCloseBehavior behavior
        ? behavior : WindowCloseBehavior.Ask;

    public SettingsWindow(Window? owner, UserSettings settings, BackupManager backups, Action<string>? log = null)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        this.backups = backups;
        this.log = log;
        SvnPathBox.Text = settings.SvnExecutable;
        DiffPathBox.Text = settings.ExternalDiffExecutable;
        RefreshMinutesBox.Text = (settings.RefreshSeconds / 60m).ToString(CultureInfo.CurrentCulture);
        AutoCleanupBox.IsChecked = settings.AutoBackupCleanup;
        RetentionDaysBox.Text = settings.BackupRetentionDays.ToString();
        CloseBehaviorBox.SelectedValue = settings.CloseBehavior;
        BackupPathText.Text = backups.DirectoryPath;
        initializing = false;
    }

    public string? ValidateOptions()
    {
        if (SvnExecutable.Length == 0) return "请填写 SVN 路径；使用内置客户端可填写 svn.exe。";
        if (!decimal.TryParse(RefreshMinutesBox.Text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign
            | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.CurrentCulture, out var minutes)
            || minutes < 0.25m || minutes > int.MaxValue / 60m)
            return "刷新间隔须为不小于 0.25 的分钟数，可填写小数。";
        if (!TryReadRetention(out var days)) return $"备份保留天数须为 1–{BackupManager.MaximumRetentionDays} 的整数。";
        // Keep the persisted unit in seconds so existing configurations retain their interval.
        RefreshSeconds = (int)decimal.Round(minutes * 60m, 0, MidpointRounding.AwayFromZero);
        BackupRetentionDays = days;
        return null;
    }

    private bool TryReadRetention(out int days) => int.TryParse(RetentionDaysBox.Text, out days)
        && days is >= 1 and <= BackupManager.MaximumRetentionDays;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = ValidateOptions() ?? "";
        if (ValidationText.Text.Length == 0) DialogResult = true;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshBackupsAsync();
    private async void RefreshBackups_Click(object sender, RoutedEventArgs e) => await RefreshBackupsAsync();
    private async void RetentionDaysBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initializing && !cleaning) await UpdateSummaryAsync(debounce: true);
    }

    public Task RefreshBackupsAsync() => UpdateSummaryAsync(debounce: false);

    private async Task UpdateSummaryAsync(bool debounce)
    {
        if (closed) return;
        summaryCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        summaryCancellation = cancellation;
        summary = null;
        CleanBackupsButton.IsEnabled = false;
        CleanAllBackupsButton.IsEnabled = false;
        BackupDetailsText.Text = "";
        BackupSummaryText.Text = "正在统计备份…";
        ExpiredSummaryText.Text = "";
        try
        {
            var validRetention = TryReadRetention(out var days);
            if (!validRetention) days = BackupManager.DefaultRetentionDays;
            if (debounce) await Task.Delay(300, cancellation.Token);
            var result = await backups.GetSummaryAsync(days, cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            summary = result;
            BackupSummaryText.Text = $"共 {result.Count} 项备份，占用 {FileSizeFormatter.Format(result.TotalBytes)}";
            ExpiredSummaryText.Text = validRetention
                ? $"超过 {days} 天：{result.ExpiredCount} 项，可清理 {FileSizeFormatter.Format(result.ExpiredBytes)}"
                : "请输入有效的保留天数以统计过期备份。";
            var retained = result.RetainedCount > 0 ? $"{result.RetainedCount} 项未识别、含链接或无法读取的内容会保留。\n" : "";
            BackupDetailsText.Text = retained + string.Join("\n", result.Errors.Take(3));
            CleanBackupsButton.IsEnabled = !cleaning && validRetention && result.ExpiredCount > 0;
            CleanAllBackupsButton.IsEnabled = !cleaning && result.CleanableCount > 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!closed && !cancellation.IsCancellationRequested) BackupSummaryText.Text = $"无法统计备份：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(summaryCancellation, cancellation)) summaryCancellation = null;
        }
    }

    private async void CleanBackups_Click(object sender, RoutedEventArgs e) => await CleanBackupsAsync(allBackups: false);

    private async void CleanAllBackups_Click(object sender, RoutedEventArgs e) => await CleanBackupsAsync(allBackups: true);

    private async Task CleanBackupsAsync(bool allBackups)
    {
        var validRetention = TryReadRetention(out var days);
        if (cleaning || (!allBackups && !validRetention)) return;
        await RefreshBackupsAsync();
        if (closed || summary is not { } available) return;
        var count = allBackups ? available.CleanableCount : available.ExpiredCount;
        var bytes = allBackups ? available.CleanableBytes : available.ExpiredBytes;
        if (count == 0) return;
        var title = allBackups ? "清理全部备份" : "清理过期备份";
        var scope = allBackups ? "全部可清理备份（包括尚未过期的备份）" : $"超过 {days} 天的备份";
        var message = $"将永久删除{scope}，共 {count} 项，约 {FileSizeFormatter.Format(bytes)}。\n\n"
            + "备份可能包含尚未提交的本地修改。需要保留的内容请先移至其他目录。是否清理？";
        if (MessageBox.Show(this, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        SetCleaning(true);
        CleanupMessageText.Text = allBackups ? "正在清理全部备份…" : "正在清理过期备份…";
        try
        {
            var result = allBackups
                ? await backups.CleanupAllAsync(available.CleanablePaths, lifetime.Token)
                : await backups.CleanupExpiredAsync(days, lifetime.Token);
            if (closed) return;
            CleanupMessageText.Text = $"已清理 {result.DeletedCount} 项，释放 {FileSizeFormatter.Format(result.DeletedBytes)}。";
            if (result.Errors.Count > 0) CleanupMessageText.Text += $" {result.Errors.Count} 项未能完整清理，请查看操作记录。";
            log?.Invoke(CleanupMessageText.Text);
            foreach (var error in result.Errors) log?.Invoke("备份清理：" + error);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!closed) { CleanupMessageText.Text = $"备份清理未完成：{ex.Message}"; log?.Invoke(CleanupMessageText.Text); }
        }
        finally
        {
            if (!closed) { SetCleaning(false); await RefreshBackupsAsync(); }
        }
    }

    private void SetCleaning(bool value)
    {
        cleaning = value;
        SaveButton.IsEnabled = RetentionDaysBox.IsEnabled = AutoCleanupBox.IsEnabled = RefreshBackupsButton.IsEnabled = !value;
        CleanBackupsButton.IsEnabled = !value && TryReadRetention(out _) && summary?.ExpiredCount > 0;
        CleanAllBackupsButton.IsEnabled = !value && summary?.CleanableCount > 0;
        CleanupProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(backups.DirectoryPath);
            Process.Start(new ProcessStartInfo(backups.DirectoryPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开备份目录失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        summaryCancellation?.Cancel();
    }
}
