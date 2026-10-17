using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public sealed record ExportItem(BrowserEntry Entry, string Destination);

// Plan all destinations before downloading anything. Completed items are published one at a time.
public sealed class RepositoryExporter(SvnOperations operations)
{
    public static IReadOnlyList<ExportItem> CreatePlan(IReadOnlyList<BrowserEntry> selection, string destinationDirectory)
    {
        if (selection.Count == 0) throw new ArgumentException("请先选择要导出的文件或目录。");
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new ArgumentException("请选择已存在的目标文件夹。");
        var plan = new List<ExportItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selection)
        {
            if (entry.ServerRevision == null) throw new ArgumentException($"服务器上没有此项目，无法下载：{entry.Name}");
            if (entry.Name is "" or "." or ".." || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || entry.Name.EndsWith(' ') || entry.Name.EndsWith('.'))
                throw new ArgumentException($"项目名称不能保存为 Windows 文件：{entry.Name}");
            if (!names.Add(entry.Name)) throw new ArgumentException($"所选项目存在同名项：{entry.Name}");
            var destination = Path.GetFullPath(Path.Combine(directory, entry.Name));
            if (!RepositoryService.IsDescendant(destination, directory)) throw new ArgumentException("目标超出了指定文件夹。");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"目标中已存在同名项目，尚未开始下载：\n{destination}\n\n请选择其他文件夹，或先移走同名项目。");
            plan.Add(new(entry, destination));
        }
        return plan;
    }

    public async Task ExportAsync(IReadOnlyList<BrowserEntry> selection, string destinationDirectory, string revision,
        CancellationToken token, Action<int, int, ExportItem>? itemStarted = null, Action<ExportItem>? itemCompleted = null)
    {
        var plan = CreatePlan(selection, destinationDirectory);
        var directory = Path.GetFullPath(destinationDirectory);
        for (var index = 0; index < plan.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var item = plan[index];
            itemStarted?.Invoke(index + 1, plan.Count, item);
            // Use a simple SVN destination name, then rename through .NET. This preserves names
            // containing spaces before '@', which SVN otherwise normalizes incorrectly on Windows.
            var staging = Path.Combine(directory, ".svnclient-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                await operations.ExportRevisionAsync(item.Entry.Url, revision, "item", staging, token);
                token.ThrowIfCancellationRequested();
                var stagedItem = Path.Combine(staging, "item");
                if (item.Entry.IsDirectory) Directory.Move(stagedItem, item.Destination);
                else File.Move(stagedItem, item.Destination);
                itemCompleted?.Invoke(item);
            }
            finally
            {
                // Only remove the random staging directory created inside this chosen destination.
                if (RepositoryService.IsDescendant(Path.GetFullPath(staging), directory))
                {
                    try { Directory.Delete(staging, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
