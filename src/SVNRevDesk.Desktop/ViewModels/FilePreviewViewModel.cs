using System.Windows.Media.Imaging;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed class FilePreviewViewModel(Func<BrowserEntry, string, CancellationToken, Task<FilePreviewContent>> load) : ObservableObject, IDisposable
{
    private bool enabled, paused, loading, disposed;
    private BrowserEntry? entry;
    private string revision = "";
    private CancellationTokenSource? cancellation;
    private long generation;
    private FilePreviewContent content = new("", "选中一个文件以预览。");
    public Task LoadingTask { get; private set; } = Task.CompletedTask;
    public bool IsEnabled
    {
        get => enabled;
        set { if (SetProperty(ref enabled, value)) Restart(); }
    }
    public bool IsPaused
    {
        get => paused;
        set { if (SetProperty(ref paused, value)) Restart(); }
    }
    public bool IsLoading { get => loading; private set => SetProperty(ref loading, value); }
    public string Name => entry?.Name ?? "文件预览";
    public string Source => content.Source;
    public string Message => content.Message;
    public string Text => content.Text ?? "";
    public BitmapSource? Image => content.Image;
    public bool HasText => content.Text != null;
    public bool HasImage => content.Image != null;
    public bool HasContent => HasText || HasImage;

    public void Update(IReadOnlyList<BrowserEntry> selection, string serverRevision)
    {
        entry = selection.Count == 1 ? selection[0] : null;
        revision = serverRevision;
        Notify(nameof(Name));
        Restart();
    }

    private void Restart()
    {
        if (disposed) return;
        Cancel();
        SetContent(new("", !IsEnabled ? "预览已关闭。" : IsPaused ? "操作完成后继续预览。"
            : entry == null ? "选中一个文件以预览。" : entry.IsDirectory ? "文件夹不提供内容预览。" : "正在读取预览…"));
        if (!IsEnabled || IsPaused || entry == null || entry.IsDirectory) return;
        var request = entry;
        var requestRevision = revision;
        var version = generation;
        var source = cancellation = new CancellationTokenSource();
        IsLoading = true;
        LoadingTask = ReadAsync(request, requestRevision, version, source);
    }

    private async Task ReadAsync(BrowserEntry request, string serverRevision, long version, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(180, source.Token);
            var result = await load(request, serverRevision, source.Token);
            if (version != generation || source.IsCancellationRequested || disposed) return;
            SetContent(result);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == generation && !disposed)
                SetContent(new("", "无法预览：" + ex.Message.Split('\n')[0]));
        }
        finally
        {
            if (version == generation) { cancellation = null; IsLoading = false; }
            source.Dispose();
        }
    }

    private void SetContent(FilePreviewContent value)
    {
        content = value;
        Notify(nameof(Source)); Notify(nameof(Message)); Notify(nameof(Text)); Notify(nameof(Image));
        Notify(nameof(HasText)); Notify(nameof(HasImage)); Notify(nameof(HasContent));
    }

    public void Cancel()
    {
        generation++;
        cancellation?.Cancel();
        cancellation = null;
        IsLoading = false;
        SetContent(new("", "预览已取消。"));
    }

    public void Dispose() { if (disposed) return; disposed = true; Cancel(); }
}
