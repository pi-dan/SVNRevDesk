using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Views;

public sealed class BatchRenameWindow : Window
{
    private readonly IReadOnlyList<BatchRenameSource> sources;
    private readonly Func<IReadOnlyList<BatchRenameRow>, CancellationToken, Task<FileMovePlan>> prepare;
    private readonly CancellationTokenSource lifetime;
    private readonly TextBox find, replace, prefix, suffix, numberStart, digits;
    private readonly CheckBox number, extensions;
    private readonly DataGrid grid;
    private readonly TextBlock status;
    private readonly Button accept;
    private readonly StackPanel rules;
    private IReadOnlyList<BatchRenameRow> rows = [];
    private bool checking, closed;
    public FileMovePlan? Plan { get; private set; }

    public BatchRenameWindow(Window owner, IReadOnlyList<BatchRenameSource> sources,
        Func<IReadOnlyList<BatchRenameRow>, CancellationToken, Task<FileMovePlan>> prepare, CancellationToken token)
    {
        Owner = owner; Title = "批量重命名"; Width = 1020; Height = 670; MinWidth = 850; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.sources = sources; this.prepare = prepare; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var panel = new DockPanel(); rules = new StackPanel(); DockPanel.SetDock(rules, Dock.Top); panel.Children.Add(rules);
        rules.Children.Add(new TextBlock { Text = "预览新名称后再执行。受版本控制的项目保留 SVN 历史；完成后仍需手动提交。", TextWrapping = TextWrapping.Wrap });
        rules.Children.Add(new TextBlock { Text = "顺序：替换 → 添加前缀 / 后缀 → 追加编号。替换区分大小写；目录名整体参与处理。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 4, 0, 8) });
        var first = new WrapPanel(); rules.Children.Add(first);
        find = Field(first, "查找", "RenameFind"); replace = Field(first, "替换为", "RenameReplace");
        var second = new WrapPanel(); rules.Children.Add(second);
        prefix = Field(second, "前缀", "RenamePrefix"); suffix = Field(second, "后缀", "RenameSuffix");
        var third = new WrapPanel { Margin = new Thickness(0, 7, 0, 12) }; rules.Children.Add(third);
        number = new CheckBox { Name = "RenameNumber", Content = "追加编号", Margin = new Thickness(0, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center }; third.Children.Add(number);
        numberStart = Field(third, "起始", "RenameStart", "1", 65); digits = Field(third, "位数", "RenameDigits", "3", 50);
        extensions = new CheckBox { Name = "PreserveExtension", Content = "保留文件扩展名", IsChecked = true, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; third.Children.Add(extensions);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        status = new TextBlock { Name = "RenameStatus", TextWrapping = TextWrapping.Wrap, MaxHeight = 95, Margin = new Thickness(0, 10, 0, 8) }; bottom.Children.Add(status);
        bottom.Children.Add(new TextBlock { Text = "已有目标、重复名称和仅大小写变化均会阻止执行。不会覆盖文件；执行中止时已完成的改名会保留。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        accept = new Button { Name = "AcceptRename", Content = "按预览执行重命名", IsDefault = true, IsEnabled = false }; buttons.Children.Add(accept);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true }); bottom.Children.Add(buttons);
        grid = new DataGrid { Name = "RenamePreview", IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserSortColumns = false };
        foreach (var column in new[] { ("原名称", nameof(BatchRenameRow.OldName)), ("新名称", nameof(BatchRenameRow.NewName)), ("检查结果", nameof(BatchRenameRow.Status)) })
            grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 240 });
        panel.Children.Add(grid); Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = panel };
        foreach (var box in new[] { find, replace, prefix, suffix, numberStart, digits }) box.TextChanged += (_, _) => UpdatePreview();
        foreach (var box in new[] { number, extensions }) { box.Checked += (_, _) => UpdatePreview(); box.Unchecked += (_, _) => UpdatePreview(); }
        accept.Click += async (_, _) => await AcceptAsync();
        Closed += (_, _) => { closed = true; lifetime.Cancel(); if (!checking) lifetime.Dispose(); };
        UpdatePreview();
    }

    private static TextBox Field(Panel panel, string label, string name, string text = "", double width = 255)
    {
        panel.Children.Add(new TextBlock { Text = label, Width = width < 100 ? 40 : 65, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Name = name, Text = text, Width = width, Margin = new Thickness(0, 3, 15, 3) }; panel.Children.Add(box); return box;
    }

    private void UpdatePreview()
    {
        try
        {
            var useNumbers = number.IsChecked == true;
            var start = 1; var width = 3;
            if (useNumbers && (!int.TryParse(numberStart.Text, out start) || !int.TryParse(digits.Text, out width)))
                throw new ArgumentException("请输入有效的起始编号和位数。");
            rows = BatchRename.Preview(sources, new(find.Text, replace.Text, prefix.Text, suffix.Text, useNumbers, start, width, extensions.IsChecked == true));
            grid.ItemsSource = rows;
            var errors = rows.Count(row => row.Error.Length > 0); var changes = rows.Count(row => row.Changed);
            status.Text = $"共 {rows.Count} 项，将重命名 {changes} 项，名称不变 {rows.Count - changes} 项，错误 {errors} 项。";
            accept.IsEnabled = changes > 0 && errors == 0;
        }
        catch (Exception ex) { rows = []; grid.ItemsSource = rows; status.Text = ex.Message; accept.IsEnabled = false; }
    }

    private async Task AcceptAsync()
    {
        if (checking || !accept.IsEnabled) return;
        checking = true; rules.IsEnabled = accept.IsEnabled = false; status.Text = "正在复核文件与 SVN 状态…";
        try
        {
            var plan = await prepare(rows, lifetime.Token);
            if (!closed && !lifetime.IsCancellationRequested) { Plan = plan; DialogResult = true; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!closed) { status.Text = ex.Message; status.ToolTip = ex.Message; } }
        finally
        {
            checking = false;
            if (closed) lifetime.Dispose();
            else { rules.IsEnabled = true; accept.IsEnabled = rows.Count > 0 && rows.All(row => row.Error.Length == 0) && rows.Any(row => row.Changed); }
        }
    }
}
