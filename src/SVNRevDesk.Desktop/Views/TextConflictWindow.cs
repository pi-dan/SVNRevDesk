using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Views;

public sealed class TextConflictWindow : Window
{
    private readonly TextMergeSession session;
    private readonly Func<TextMergeSession, CancellationToken, Task> launch, apply;
    private readonly CancellationTokenSource lifetime;
    private readonly Button merge, accept, close;
    private readonly CheckBox reviewed;
    private readonly TextBlock status;
    private bool busy, applying, closed;
    public bool Applied { get; private set; }

    public TextConflictWindow(Window owner, TextMergeSession session, Func<TextMergeSession, CancellationToken, Task> launch,
        Func<TextMergeSession, CancellationToken, Task> apply, CancellationToken token)
    {
        this.session = session; this.launch = launch; this.apply = apply;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Owner = owner; Title = "文本冲突合并"; Width = 760; SizeToContent = SizeToContent.Height;
        MinWidth = 620; MaxHeight = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = session.Path, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "1. 打开 WinMerge，在中间窗格合并文本。\n左侧为本地修改，右侧为本次冲突的服务器修改；两侧只读。中间以基础版本为起点，自动合并能确定的修改；其余冲突需要逐处检查和编辑。\n\n2. 在 WinMerge 中保存中间窗格（Ctrl+S），然后关闭该 WinMerge 窗口。\n\n3. 回到此窗口确认后，才会写回工作文件并标记已解决；不会自动提交。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) });
        merge = new Button { Name = "LaunchTextMerge", Content = "打开 WinMerge 三方合并", HorizontalAlignment = HorizontalAlignment.Left };
        merge.Click += async (_, _) => await RunAsync(false); panel.Children.Add(merge);
        panel.Children.Add(new TextBlock { Text = "原文件、三个版本及合并草稿保存在：", Margin = new Thickness(0, 14, 0, 4), Foreground = Brushes.SlateGray });
        var backup = new TextBox { Name = "TextMergeBackup", Text = session.Directory, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(backup);
        reviewed = new CheckBox { Name = "ReviewedTextMerge", Content = "我已检查并保存全部合并结果，确认所有冲突均已处理", Margin = new Thickness(0, 18, 0, 12) };
        reviewed.Checked += (_, _) => UpdateControls(); reviewed.Unchecked += (_, _) => UpdateControls(); panel.Children.Add(reviewed);
        status = new TextBlock { Name = "TextMergeStatus", Text = "工作副本保持原样。请先打开 WinMerge 合并并保存结果。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        accept = new Button { Name = "ApplyTextMerge", Content = "写回并标记已解决", IsEnabled = false };
        accept.Click += async (_, _) => await RunAsync(true); buttons.Children.Add(accept);
        close = new Button { Name = "CancelTextMerge", Content = "暂不解决", IsCancel = true }; buttons.Children.Add(close);
        panel.Children.Add(buttons); Content = new Border { Background = Brushes.White, Child = panel };
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };
        Loaded += (_, _) => UpdateControls();
    }

    private void UpdateControls()
    {
        merge.IsEnabled = reviewed.IsEnabled = !busy;
        accept.IsEnabled = !busy && reviewed.IsChecked == true && File.Exists(session.ResultFile);
        close.IsEnabled = !applying;
    }

    private async Task RunAsync(bool writeBack)
    {
        if (busy || closed || writeBack && (reviewed.IsChecked != true || !File.Exists(session.ResultFile))) return;
        busy = true; applying = writeBack;
        if (!writeBack) reviewed.IsChecked = false;
        UpdateControls();
        status.Text = writeBack ? "正在复核冲突状态、写回文件并标记已解决…" : "WinMerge 合并中。请保存中间窗格并关闭 WinMerge；暂不解决可保留草稿。";
        try
        {
            if (writeBack)
            {
                await apply(session, lifetime.Token); Applied = true;
                if (!closed) DialogResult = true;
            }
            else
            {
                await launch(session, lifetime.Token);
                if (!closed) status.Text = File.Exists(session.ResultFile)
                    ? "已找到保存的合并草稿。请确认全部冲突已处理，再勾选并写回。"
                    : "未找到保存的合并结果，工作副本未改变。请重新打开 WinMerge，保存中间窗格后再关闭。";
            }
        }
        catch (OperationCanceledException) when (closed || lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!closed) status.Text = "操作未完成：" + ex.Message; }
        finally { busy = applying = false; if (!closed) UpdateControls(); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (applying && !Applied) e.Cancel = true;
    }
}
