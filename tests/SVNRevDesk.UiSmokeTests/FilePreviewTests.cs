using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Core.Svn;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class FilePreviewTests
{
    public static async Task RunAsync(MainWindow window, string output, Action<Window, string, int, int> render)
    {
        var root = Path.Combine(output, "preview-fixture");
        var repositoryPath = Path.GetFullPath(Path.Combine(root, "repository"));
        Directory.CreateDirectory(root);
        var adminStart = new ProcessStartInfo("svnadmin.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        adminStart.ArgumentList.Add("create"); adminStart.ArgumentList.Add(repositoryPath);
        using (var admin = Process.Start(adminStart)!)
        {
            var error = admin.StandardError.ReadToEndAsync();
            await admin.WaitForExitAsync();
            if (admin.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var settings = new UserSettings(Path.Combine(root, "application"));
        using var runner = new SvnRunner();
        var operations = new SvnOperations(runner);
        var local = Path.GetFullPath(Path.Combine(root, "working-copy"));
        await operations.CheckoutAsync(new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/'), local, false, default);
        File.WriteAllText(Path.Combine(local, "small.txt"), "服务器第一版\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(local, "utf16.txt"), "UTF16 中文文本", Encoding.Unicode);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllText(Path.Combine(local, "gb.txt"), "GB 中文文本", Encoding.GetEncoding("GB18030"));
        File.WriteAllText(Path.Combine(local, "large.txt"), new string('x', FilePreviewService.MaximumTextBytes * 2));
        File.WriteAllBytes(Path.Combine(local, "binary.dat"), [0, 1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(local, "many-lines.txt"), string.Concat(Enumerable.Repeat("line\n", 7000)));
        var imagePath = Path.Combine(local, "图片 @ 预览.png");
        var bitmap = BitmapSource.Create(100, 60, 96, 96, PixelFormats.Bgr32, null,
            Enumerable.Repeat((byte)180, 100 * 60 * 4).ToArray(), 100 * 4);
        using (var imageStream = File.Create(imagePath))
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(imageStream);
        }
        await operations.AddAsync(Directory.EnumerateFiles(local), default);
        await operations.CommitAsync([local], "Preview fixture", default);
        var repository = new RepositoryService(runner);
        var session = await repository.OpenWorkingCopyAsync(local, default);
        var snapshot = await repository.RefreshAsync(session, default);
        var entries = await repository.ReadDirectoryAsync(session, snapshot, "", default);
        var text = entries.Single(item => item.Name == "small.txt");
        var image = entries.Single(item => item.Name == "图片 @ 预览.png");
        async Task<FilePreviewContent> Read(BrowserEntry entry) =>
            await FilePreviewService.LoadAsync(entry, snapshot.Revision.ToString(), runner, settings, default);

        File.WriteAllText(text.LocalPath!, "本地未提交的内容\n", new UTF8Encoding(false));
        var content = await Read(text);
        if (content.Text != "本地未提交的内容\n" || !content.Source.StartsWith("本地文件")) throw new InvalidOperationException("Preview ignored local changes.");
        if ((await Read(entries.Single(item => item.Name == "utf16.txt"))).Text != "UTF16 中文文本"
            || (await Read(entries.Single(item => item.Name == "gb.txt"))).Text != "GB 中文文本") throw new InvalidOperationException("Preview encoding failed.");
        var imageContent = await Read(image);
        if (imageContent.Image is not { IsFrozen: true, PixelWidth: 100, PixelHeight: 60 }) throw new InvalidOperationException("Image preview did not decode/freeze correctly.");
        using (var exclusive = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        if ((await Read(entries.Single(item => item.Name == "large.txt"))).Text != null
            || (await Read(entries.Single(item => item.Name == "binary.dat"))).Text != null) throw new InvalidOperationException("Large/binary data was treated as text.");
        var truncated = await Read(entries.Single(item => item.Name == "many-lines.txt"));
        if (truncated.Text!.Count(character => character == '\n') >= 5000 || !truncated.Message.Contains("仅显示")) throw new InvalidOperationException("Long text was not bounded for display.");

        var remote = await Read(text with { LocalPath = null });
        if (remote.Text != "服务器第一版\n" || !remote.Source.StartsWith("服务器 r")) throw new InvalidOperationException("Remote preview read or changed local content.");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var rejected = false;
            try { await FilePreviewService.LoadAsync(entries.Single(item => item.Name == "large.txt") with { LocalPath = null, Size = null },
                snapshot.Revision.ToString(), runner, settings, timeout.Token); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected || Directory.EnumerateFiles(Path.Combine(settings.TemporaryDirectory, "Preview")).Any())
                throw new InvalidOperationException("Unknown remote size was not bounded or its temporary output survived.");
        }
        if (!(await runner.RunAsync(["--version", "--quiet"])).StartsWith("1.14.5")) throw new InvalidOperationException("Preview stalled the main SVN runner.");
        await VerifyCancellationAsync(text, image);

        var model = (MainViewModel)window.DataContext;
        var grid = (DataGrid)window.FindName("FileGrid");
        var toggle = (CheckBox)window.FindName("PreviewToggle");
        var oldView = model.ViewMode;
        if (model.Preview.IsEnabled || toggle.IsChecked == true) throw new InvalidOperationException("New preview settings did not default to disabled.");
        model.ViewMode = MainViewModel.DetailsViewName;
        model.Entries.Add(image);
        try
        {
            grid.SelectedItems.Clear(); grid.SelectedItem = image;
            toggle.IsChecked = true;
            await model.Preview.LoadingTask;
            if (!model.Preview.HasImage || model.IsBusy || !grid.IsEnabled) throw new InvalidOperationException("Selection/toggle did not show an independent preview.");
            render(window, Path.Combine(output, "image-preview.png"), 1380, 780);
            render(window, Path.Combine(output, "image-preview-narrow.png"), 1000, 620);
            await model.RunAsync("Preview preference during an operation", _ =>
            {
                model.SaveSession();
                if (!model.Preview.IsPaused || !UserSettings.Load(model.Settings.StorageDirectory).PreviewEnabled)
                    throw new InvalidOperationException("Temporarily pausing preview changed the saved preference.");
                return Task.CompletedTask;
            });
            await model.Preview.LoadingTask;
            VerifyRestoredPreview(model.Settings, true, output, render);
            model.SetSelection([text]);
            await model.Preview.LoadingTask;
            render(window, Path.Combine(output, "text-preview.png"), 1380, 780);
            var pane = (FilePreviewPane)window.FindName("PreviewPane");
            ((Button)pane.FindName("ClosePreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (model.Preview.IsEnabled || toggle.IsChecked == true) throw new InvalidOperationException("Panel close did not synchronize the quick toggle.");
            model.SaveSession();
            VerifyRestoredPreview(model.Settings, false, output, render);
            toggle.IsChecked = true;
            model.SetSelection([text, image]);
            if (model.Preview.HasContent || model.Preview.IsLoading) throw new InvalidOperationException("Multiple selection kept an unrelated preview.");
            toggle.IsChecked = false;
            if (model.Preview.IsEnabled || ((FrameworkElement)window.FindName("PreviewPane")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Quick preview close did not hide the panel.");
            model.SaveSession();
            if (UserSettings.Load(model.Settings.StorageDirectory).PreviewEnabled)
                throw new InvalidOperationException("Disabling the toolbar toggle was not saved.");
        }
        finally { model.Preview.IsEnabled = false; model.Entries.Remove(image); model.ViewMode = oldView; }
        Console.WriteLine("PASS: preview initially defaults off and restores the saved on/off state/layout, temporary pauses retain preferences, quick toggle and list selection work, local changes/images/encodings render, files stay unlocked, large/remote transfers are bounded and cleaned, late/cancelled reads never replace current content.");
    }

    private static void VerifyRestoredPreview(UserSettings previousSettings, bool expected, string output,
        Action<Window, string, int, int> render)
    {
        var settings = UserSettings.Load(previousSettings.StorageDirectory);
        var restored = new MainWindow(settings);
        using var model = (MainViewModel)restored.DataContext;
        var toggle = (CheckBox)restored.FindName("PreviewToggle");
        var pane = (FilePreviewPane)restored.FindName("PreviewPane");
        var column = (ColumnDefinition)restored.FindName("PreviewColumn");
        var splitter = (ColumnDefinition)restored.FindName("PreviewSplitterColumn");
        render(restored, Path.Combine(output, expected ? "preview-restored-enabled.png" : "preview-restored-disabled.png"), 1000, 620);
        if (settings.PreviewEnabled != expected || model.Preview.IsEnabled != expected || toggle.IsChecked != expected
            || pane.Visibility != (expected ? Visibility.Visible : Visibility.Collapsed)
            || (column.Width.Value > 0) != expected || (splitter.Width.Value > 0) != expected
            || (column.MinWidth > 0) != expected || model.Preview.IsLoading)
            throw new InvalidOperationException("Restored preview preference did not synchronize its toggle, panel, and layout without reading a file.");
    }

    private static async Task VerifyCancellationAsync(BrowserEntry first, BrowserEntry second)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<FilePreviewContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var preview = new FilePreviewViewModel((entry, revision, token) =>
        {
            calls++;
            if (entry == first) { started.TrySetResult(); return delayed.Task; }
            return Task.FromResult(new FilePreviewContent("local", "ready", Text: "new selection"));
        });
        preview.Update([first], "1");
        if (calls != 0) throw new InvalidOperationException("Disabled preview performed a read.");
        preview.IsEnabled = true;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldTask = preview.LoadingTask;
        preview.Update([second], "1");
        await preview.LoadingTask;
        delayed.SetResult(new("local", "old", Text: "obsolete selection"));
        await oldTask;
        if (preview.Text != "new selection" || preview.IsLoading) throw new InvalidOperationException("A late read replaced the current preview.");
        preview.IsEnabled = false;
        preview.IsEnabled = true;
        preview.IsEnabled = false;
        await preview.LoadingTask;
        if (preview.HasContent || preview.IsLoading) throw new InvalidOperationException("Closing preview kept a pending result.");
    }
}
