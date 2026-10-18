using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SVNRevDesk.Core.Comparison;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (2 or 3) || (args.Length == 3 && args[2] is not ("--startup-cache" or "--backup-management" or "--window-close" or "--file-preview" or "--file-clipboard" or "--directory-actions" or "--winmerge" or "--ignore" or "--creation" or "--sessions" or "--revision-picker" or "--file-management")))
        {
            Console.Error.WriteLine("Usage: UiSmokeTests WORKING_COPY OUTPUT_DIRECTORY [--startup-cache|--backup-management|--window-close|--file-preview|--file-clipboard|--directory-actions|--winmerge|--ignore|--creation|--sessions|--revision-picker]");
            return 1;
        }
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var bindingErrors = new StringWriter();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(bindingErrors));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SVNRevDesk;component/Themes/AppStyles.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SVNRevDesk;component/Themes/FileIcons.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SVNRevDesk;component/Assets/AppIcon.xaml")
        });
        var window = new MainWindow(new UserSettings(Path.Combine(output, "settings")));
        var model = (MainViewModel)window.DataContext;
        Exception? failure = null;
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                if (args.Length == 3 && args[2] == "--file-management")
                {
                    await FileManagementUiTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0) throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] == "--revision-picker")
                {
                    await RevisionPickerTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0) throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] == "--sessions")
                {
                    await SessionUiTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0) throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] is "--ignore" or "--creation")
                {
                    if (args[2] == "--creation") await CreationTests.RunAsync(output, RenderContent);
                    else await IgnoreUiTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] == "--winmerge")
                {
                    await WinMergeComparisonTests.RunAsync(output, Path.GetFullPath(args[0]));
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] == "--directory-actions")
                {
                    await DirectoryActionTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] == "--file-clipboard")
                {
                    await FileClipboardTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                if (args.Length == 3 && args[2] == "--window-close")
                {
                    await CloseBehaviorTests.RunAsync(output, RenderContent);
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                PortableStorageTests.Run(output);
                if (args.Length == 3 && args[2] == "--file-preview")
                {
                    await FilePreviewTests.RunAsync(window, output, RenderContent);
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                await BackupManagementTests.RunAsync(output, RenderContent);
                if (args.Length == 3 && args[2] == "--backup-management") return;
                if (args.Length == 3)
                {
                    await StartupCacheTests.RunAsync(output, Path.GetFullPath(args[0]),
                        (view, path) => RenderContent(view, path, 1360, 780));
                    if (bindingErrors.ToString().Length > 0)
                        throw new InvalidOperationException("WPF binding errors:\n" + bindingErrors);
                    return;
                }
                await CloseBehaviorTests.RunAsync(output, RenderContent);
                await model.RunAsync("UI smoke test", token => model.OpenAsync(Path.GetFullPath(args[0]), true, token));
                Console.WriteLine("Loaded working copy.");
                var fileGrid = (DataGrid)window.FindName("FileGrid");
                var fileCards = (ListBox)window.FindName("FileCards");
                if (window.Icon == null || !fileGrid.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "show-in-explorer"))
                    || !fileCards.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "show-in-explorer"))
                    || !fileGrid.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "export"))
                    || !fileCards.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "export")))
                    throw new InvalidOperationException("Application icon or Explorer menu entry is missing");
                var selected = model.Entries.First(entry => !entry.IsDirectory);
                var updateButton = (Button)window.FindName("UpdateSelectionButton");
                var compareButton = (Button)window.FindName("CompareSelectionButton");
                if (updateButton.IsEnabled || compareButton.IsEnabled)
                    throw new InvalidOperationException("Selection actions should be disabled before selecting a target");
                fileGrid.SelectedItems.Add(selected);
                if (!updateButton.IsEnabled || !model.CanCommitSelection)
                    throw new InvalidOperationException("Local selection did not enable update and commit actions");
                RenderContent(window, Path.Combine(output, "main-window.png"), 1360, 780);
                RenderContent(window, Path.Combine(output, "main-window-narrow.png"), 1000, 620);
                var row = (DataGridRow)fileGrid.ItemContainerGenerator.ContainerFromItem(selected);
                if (((SolidColorBrush)row.Background).Color != Color.FromRgb(230, 239, 251))
                    throw new InvalidOperationException("Selected row does not use the light selection background");
                model.ViewMode = MainViewModel.CardViewName;
                RenderContent(window, Path.Combine(output, "main-window-cards.png"), 1360, 780);
                if (!fileCards.SelectedItems.Contains(selected) || fileGrid.Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("View switching lost selection or kept the detail grid visible");
                var second = model.Entries.First(entry => entry != selected);
                fileCards.SelectedItems.Add(second);
                if (fileGrid.SelectedItems.Count != 2) throw new InvalidOperationException("Card multiselection did not reach the shared operation targets");
                if (!model.CanExecute("export")) throw new InvalidOperationException("Mixed file/directory selection did not enable batch export");
                model.SetSelection([model.Entries.First(entry => entry.ServerRevision == null)]);
                if (model.CanExecute("export")) throw new InvalidOperationException("Export accepted an item absent from the server");
                model.SetSelection(fileCards.SelectedItems.Cast<SVNRevDesk.Core.Models.BrowserEntry>().ToList());
                model.FilterText = selected.Name;
                if (model.EntriesView.Cast<object>().Count() != 1 || fileCards.Items.Count != fileGrid.Items.Count)
                    throw new InvalidOperationException("Views do not share the name filter");
                model.FilterText = "";
                model.FilterText = "__no_such_file__";
                if (model.VisibleEntryCount != 0 || model.CanUpdateSelection
                    || ((FrameworkElement)window.FindName("EmptyState")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Empty filter did not clear selection actions or show the empty state");
                RenderContent(window, Path.Combine(output, "empty-filter.png"), 1000, 620);
                model.FilterText = "";
                await VerifyProgressAsync(window, output);
                await VerifyConfirmationProgressAsync(window);
                await DialogProgressTests.RunAsync(output, RenderContent);
                VerifyExplorerPaths(output);
                var left = Path.Combine(output, "left.txt");
                var right = Path.Combine(output, "right.txt");
                File.WriteAllText(left, "SVNRevDesk\nserver line\nunchanged\n");
                File.WriteAllText(right, "SVNRevDesk\nlocal line\nunchanged\nnew line\n");
                var compare = new ComparisonWindow(window, left, right, "服务器版本", "本地版本", TextComparison.CompareFiles(left, right), "");
                Console.WriteLine("Created comparison window.");
                RenderContent(compare, Path.Combine(output, "file-comparison.png"), 1160, 690);
                await WinMergeComparisonTests.RunAsync(output, Path.GetFullPath(args[0]));
                await FilePreviewTests.RunAsync(window, output, RenderContent);
                RenderIconGallery(Path.Combine(output, "file-icons.png"));
                var errors = bindingErrors.ToString();
                if (errors.Length > 0) throw new InvalidOperationException("WPF binding errors:\n" + errors);
                if (model.Entries.Count == 0 || model.Roots.Count == 0) throw new InvalidOperationException("Empty browser view");
                // Check navigation through the same view model used by tree and double-click events.
                await model.RunAsync("Navigate", token => model.NavigateAsync("nested", token));
                if (model.Entries.Single().Name != "child.txt") throw new InvalidOperationException("Directory navigation failed");
                model.SaveSession();
                using var reopened = new MainViewModel(UserSettings.Load(Path.Combine(output, "settings")));
                await reopened.RunAsync("Restore", reopened.RestoreLastSessionAsync);
                if (reopened.CurrentRelative != "nested" || !reopened.IsCardView || reopened.FindNode("nested")?.IsSelected != true)
                    throw new InvalidOperationException("Saved directory, tree selection, or view mode was not restored");
                reopened.Settings.LastRelativeDirectory = "nested/deleted-directory";
                await reopened.RunAsync("Restore removed directory", reopened.RestoreLastSessionAsync);
                if (reopened.CurrentRelative != "nested") throw new InvalidOperationException("Removed directory did not fall back to its nearest parent");
                await reopened.RunAsync("Online", token => reopened.OpenAsync(reopened.Session!.Url, false, token));
                await reopened.RunAsync("Online navigate", token => reopened.NavigateAsync("nested", token));
                reopened.SaveSession();
                using var online = new MainViewModel(UserSettings.Load(Path.Combine(output, "settings")));
                await online.RunAsync("Restore online", online.RestoreLastSessionAsync);
                if (online.Session!.LocalRoot != null || online.CurrentRelative != "nested")
                    throw new InvalidOperationException("Online-only browsing position was not restored");
                online.SetSelection([online.Entries.Single()]);
                if (online.CanUpdateSelection || online.CanCommitSelection || online.CanCompareSelection
                    || !online.CanExecute("open") || !online.CanExecute("export"))
                    throw new InvalidOperationException("Online action availability did not distinguish local writes from server reads");
                online.DifferencesOnly = true;
                if (online.VisibleEntryCount != 0 || online.EmptyStateTitle != "关联本地后查看差异")
                    throw new InvalidOperationException("Unchecked online entries were treated as confirmed differences");
                online.DifferencesOnly = false;
                await online.RunAsync("Associate existing working copy", token => online.AssociateWorkingCopyAsync(Path.GetFullPath(args[0]), token));
                if (online.CurrentRelative != "nested" || online.Session!.LocalRoot == null)
                    throw new InvalidOperationException("Associating a working copy lost the current online directory");
                await model.RunAsync("Online preview", token => model.OpenAsync(model.Session!.Url, false, token));
                RenderContent(window, Path.Combine(output, "online-browser.png"), 1000, 620);
                await VerifyDeepRestoreAsync(output);
                await BrowsingTests.RunAsync(output, (view, path) => RenderContent(view, path, 1360, 780));
                await StartupCacheTests.RunAsync(output, Path.GetFullPath(args[0]), (view, path) => RenderContent(view, path, 1360, 780));
                CommitWindowTests.Run(output, RenderContent);
                await CreationTests.RunAsync(output, RenderContent);
                await FileClipboardTests.RunAsync(output, RenderContent);
                await DirectoryActionTests.RunAsync(output, RenderContent);
                errors = bindingErrors.ToString();
                if (errors.Length > 0) throw new InvalidOperationException("WPF binding errors:\n" + errors);
                Console.WriteLine($"PASS: action availability/empty states, association retains location, icon/menu, progress/elapsed time/cancellation, Explorer paths, light selection, views/selection/filtering, session restoration, and WPF rendering. Images: {output}");
            }
            catch (Exception ex) { failure = ex; }
            finally { model.Dispose(); frame.Continue = false; }
        });
        Dispatcher.PushFrame(frame);
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static void RenderContent(Window window, string path, int width, int height)
    {
        // Render the actual WPF controls offscreen; no native window or desktop screenshot is needed.
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task VerifyConfirmationProgressAsync(MainWindow window)
    {
        var model = (MainViewModel)window.DataContext;
        var progress = (FrameworkElement)window.FindName("OperationProgress");
        var handler = typeof(MainWindow).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var executed = false;
        var busyDuringExecution = false;
        var idleDuringConfirmation = false;
        var status = model.StatusMessage;
        var elapsed = model.ProgressElapsedText;
        Func<CancellationToken, Task> operation = _ =>
        {
            executed = true;
            busyDuringExecution = model.IsBusy && progress.Visibility == Visibility.Visible;
            return Task.CompletedTask;
        };
        Func<bool> cancel = () =>
        {
            idleDuringConfirmation = !model.IsBusy && progress.Visibility == Visibility.Collapsed
                && model.ProgressElapsedText == elapsed;
            return false;
        };
        await (Task)handler.Invoke(window, new object[] { "删除所选", operation, false, cancel })!;
        if (!idleDuringConfirmation || executed || model.IsBusy || progress.Visibility != Visibility.Collapsed
            || model.StatusMessage != status || model.ProgressElapsedText != elapsed)
            throw new InvalidOperationException("Cancelling confirmation started the operation, progress timer or completion message");

        idleDuringConfirmation = false;
        Func<bool> accept = () =>
        {
            idleDuringConfirmation = !model.IsBusy && progress.Visibility == Visibility.Collapsed;
            return true;
        };
        await (Task)handler.Invoke(window, new object[] { "删除所选", operation, false, accept })!;
        if (!idleDuringConfirmation || !executed || !busyDuringExecution || model.IsBusy
            || progress.Visibility != Visibility.Collapsed || model.StatusMessage != "删除所选完成")
            throw new InvalidOperationException("Confirmed operation did not start and finish progress at the correct time");
        Console.WriteLine("PASS: confirmation keeps progress hidden; cancellation preserves status and elapsed time; acceptance starts progress only for execution.");
    }

    private static async Task VerifyProgressAsync(MainWindow window, string output)
    {
        var model = (MainViewModel)window.DataContext;
        var progress = (FrameworkElement)window.FindName("OperationProgress");
        await model.RunAsync("下载服务器文件", async token =>
        {
            var entry = model.Entries.First(entry => !entry.IsDirectory && entry.ServerRevision != null);
            model.SetProgressTarget(entry.Url);
            var path = await model.Operations.DownloadForViewingAsync(entry.Url, model.Snapshot!.Revision.ToString(), Path.Combine(output, "progress-cache"), token);
            if (new FileInfo(path).Length == 0 || !model.ProgressTransferText.StartsWith("已下载 ", StringComparison.Ordinal))
                throw new InvalidOperationException("Real download progress did not reach the view model");
            await Task.Delay(2200, token);
            if (progress.Visibility != Visibility.Visible || model.ProgressElapsedText == "已用时 00:00:00"
                || ((TextBlock)window.FindName("ProgressElapsed")).Text == "已用时 00:00:00"
                || !FindVisualChild<ProgressBar>(progress)!.IsIndeterminate)
                throw new InvalidOperationException("Busy indicator or elapsed timer is not active");
            RenderContent(window, Path.Combine(output, "operation-progress.png"), 1360, 780);
        });
        if (progress.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Progress indicator remains visible after completion");

        await model.RunAsync("Cancellation check", async token =>
        {
            _ = window.Dispatcher.BeginInvoke(model.Cancel);
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        });
        if (model.IsBusy || progress.Visibility != Visibility.Collapsed || !model.StatusMessage.StartsWith("操作已取消", StringComparison.Ordinal))
            throw new InvalidOperationException("Cancelled operation did not stop and hide the progress indicator");

        try { await model.RunAsync("Failure check", _ => Task.FromException(new IOException("Progress failure fixture"))); }
        catch (IOException) { }
        if (model.IsBusy || progress.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Failure left the progress indicator running");
    }

    private static void VerifyExplorerPaths(string output)
    {
        var folder = Path.Combine(output, "Explorer 中文, @ fixture");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "测试, @.txt");
        File.WriteAllText(file, "Explorer path fixture");
        if (FileExplorer.FindExistingPath(file) != file || FileExplorer.FindExistingPath(folder) != folder
            || FileExplorer.FindExistingPath(Path.Combine(folder, "missing", "child.txt")) != folder)
            throw new InvalidOperationException("Explorer path resolution did not retain special names or find the nearest local parent");
        var rejected = false;
        try { FileExplorer.FindExistingPath(null); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Explorer accepted an unassociated server-only item");
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static void RenderIconGallery(string path)
    {
        var examples = new (string Name, bool Directory)[]
        {
            ("文档目录", true), ("照片.JPG", false), ("说明.txt", false), ("发布包.zip", false),
            ("需求.docx", false), ("数据.xlsx", false), ("演示.pptx", false), ("手册.pdf", false),
            ("MainWindow.cs", false), ("settings.json", false), ("音乐.mp3", false), ("视频.mp4", false),
            ("SVNRevDesk.exe", false), ("cache.sqlite", false), ("字体.ttf", false), ("未知文件.xyz", false)
        };
        var panel = new WrapPanel { Margin = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(244, 246, 250)) };
        foreach (var example in examples)
        {
            var kind = FileTypeIcons.GetKind(example.Name, example.Directory);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
            row.Children.Add(new Image
            {
                Source = (ImageSource)Application.Current.FindResource("FileIcon" + kind),
                Width = 24, Height = 28, Margin = new Thickness(0, 0, 12, 0)
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = example.Name, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = FileTypeIcons.GetLabel(kind), Foreground = Brushes.SlateGray, FontSize = 12 });
            row.Children.Add(text);
            panel.Children.Add(new Border
            {
                Width = 244, Height = 72, Margin = new Thickness(3), Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 237)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Child = row
            });
        }
        var window = new Window { Content = panel };
        RenderContent(window, path, 1024, 336);
    }

    private static async Task VerifyDeepRestoreAsync(string output)
    {
        // Keep persistence scenarios isolated from the working copy passed into this test.
        var fixture = Path.Combine(output, "session-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var repositoryPath = Path.Combine(fixture, "repository");
        var start = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("create");
        start.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(start) ?? throw new InvalidOperationException("Cannot start svnadmin"))
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var workingCopy = Path.Combine(fixture, "中文工作副本");
        var url = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await operations.CheckoutAsync(url, workingCopy, false, CancellationToken.None);
        const string target = "文档目录/子目录/目标目录";
        var deepDirectory = Path.Combine(workingCopy, target.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(deepDirectory);
        File.WriteAllText(Path.Combine(deepDirectory, "说明.txt"), "Session restore fixture\n");
        var sampleNames = new[] { "照片.jpg", "说明.txt", "发布包.zip", "需求.docx", "数据.xlsx", "演示.pptx", "手册.pdf", "MainWindow.cs", "settings.json", "音乐.mp3", "视频.mp4" };
        foreach (var name in sampleNames) File.WriteAllText(Path.Combine(workingCopy, name), "UI preview fixture\n");
        await operations.AddAsync(sampleNames.Select(name => Path.Combine(workingCopy, name)).Append(Path.Combine(workingCopy, "文档目录")), CancellationToken.None);
        await operations.CommitAsync([workingCopy], "UI persistence fixture", CancellationToken.None);

        var storage = Path.Combine(fixture, "settings");
        using (var saved = new MainViewModel(new UserSettings(storage) { CloseBehavior = WindowCloseBehavior.Exit }))
        {
            await saved.RunAsync("Open deep fixture", token => saved.OpenAsync(workingCopy, true, token));
            await saved.RunAsync("Deep navigation", token => saved.NavigateAsync(target, token));
            saved.ViewMode = MainViewModel.CardViewName;
            saved.SaveSession();
        }
        var window = new MainWindow(UserSettings.Load(storage));
        var restored = (MainViewModel)window.DataContext;
        try
        {
            await restored.RunAsync("Restore deep fixture", restored.RestoreLastSessionAsync);
            if (restored.CurrentRelative != target || restored.FindNode(target)?.IsSelected != true
                || restored.FindNode("文档目录/子目录")?.IsExpanded != true || !restored.IsCardView)
                throw new InvalidOperationException("Deep directory and ancestor expansion did not survive settings round trip");
            RenderContent(window, Path.Combine(output, "restored-directory.png"), 1360, 780);
            await restored.RunAsync("Preview card layout", token => restored.NavigateAsync("", token));
            var cards = (ListBox)window.FindName("FileCards");
            cards.SelectedItems.Add(restored.Entries.First(entry => entry.Name == "照片.jpg"));
            RenderContent(window, Path.Combine(output, "main-window-cards.png"), 1360, 780);

            await restored.RunAsync("Return to saved directory", token => restored.NavigateAsync(target, token));
            restored.FindNode(target)!.IsExpanded = false;
            restored.ViewMode = MainViewModel.DetailsViewName;
            window.Close();
            var closedSettings = UserSettings.Load(storage);
            if (closedSettings.LastRelativeDirectory != target || closedSettings.UseCardView
                || closedSettings.ExpandedDirectories.Contains(target))
                throw new InvalidOperationException("Closing the window did not capture the final directory, view, and tree expansion");
        }
        finally { restored.Dispose(); }
    }
}
