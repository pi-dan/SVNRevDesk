using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Core.Comparison;

namespace SVNRevDesk.Desktop.Views;

public partial class ComparisonWindow : Window
{
    private readonly string leftPath;
    private readonly string rightPath;
    private readonly string externalTool;
    private readonly IReadOnlyList<DiffLine> rows;
    private readonly string applicationDirectory;
    private readonly CancellationTokenSource lifetime = new();

    public ComparisonWindow(Window owner, string leftPath, string rightPath, string leftTitle, string rightTitle,
        ComparisonResult comparison, string externalTool, string? applicationDirectory = null)
    {
        InitializeComponent();
        if (owner.IsVisible) Owner = owner;
        this.leftPath = leftPath; this.rightPath = rightPath; this.externalTool = externalTool;
        this.applicationDirectory = applicationDirectory ?? UserSettings.DataDirectory;
        ExternalButton.Content = string.IsNullOrWhiteSpace(externalTool) ? "WinMerge 比较" : "外部工具比较";
        ExternalButton.ToolTip = string.IsNullOrWhiteSpace(externalTool)
            ? "使用内置 WinMerge 比较，首次使用时准备工具；两侧文件只读。" : "使用设置中的外部比较工具。";
        Closed += (_, _) => lifetime.Cancel();
        rows = comparison.Lines;
        LeftLabel.Text = leftTitle;
        RightLabel.Text = rightTitle;
        SummaryLabel.Text = comparison.Summary;
        DiffGrid.ItemsSource = rows;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => SelectDifference(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => SelectDifference(1);
    private void DiffGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DiffGrid.Columns.Count != 4) return;
        var width = Math.Max(260, (e.NewSize.Width - 140) / 2);
        DiffGrid.Columns[1].Width = new DataGridLength(width);
        DiffGrid.Columns[3].Width = new DataGridLength(width);
    }
    private void SelectDifference(int direction)
    {
        var start = DiffGrid.SelectedIndex;
        if (start < 0) start = direction > 0 ? -1 : rows.Count;
        for (var i = start + direction; i >= 0 && i < rows.Count; i += direction)
            if (rows[i].Changed) { DiffGrid.SelectedIndex = i; DiffGrid.ScrollIntoView(rows[i]); break; }
    }

    private async void External_Click(object sender, RoutedEventArgs e)
    {
        ExternalButton.IsEnabled = false;
        var label = ExternalButton.Content;
        ExternalButton.Content = "正在准备比较工具…";
        try
        {
            var start = await ComparisonTool.PrepareAsync(this, externalTool, leftPath, rightPath,
                LeftLabel.Text, RightLabel.Text, applicationDirectory, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动文件比较工具。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested) MessageBox.Show(this, ex.Message, "无法启动比较工具", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ExternalButton.Content = label; ExternalButton.IsEnabled = true; }
    }
}
