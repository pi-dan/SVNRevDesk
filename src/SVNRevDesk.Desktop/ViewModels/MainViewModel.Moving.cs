using System.IO;
using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public Task MoveLocalFilesAsync(FileMovePlan plan, bool skipExisting, CancellationToken token) =>
        MoveLocalFilesAsync(plan, new FileMoveOptions(skipExisting ? FileMoveConflictBehavior.Skip : FileMoveConflictBehavior.Stop), token);

    public async Task MoveLocalFilesAsync(FileMovePlan plan, FileMoveOptions options, CancellationToken token,
        bool stayInCurrentDirectory = false)
    {
        var session = Session ?? throw new InvalidOperationException("请先打开本地工作副本。");
        if (session.LocalRoot == null || !string.Equals(session.LocalRoot, plan.LocalRoot, StringComparison.OrdinalIgnoreCase)
            || !IsCreationDirectoryMapped(plan.DestinationDirectory)) throw new InvalidOperationException("移动位置已变化，请重新选择。");
        var relative = Path.GetRelativePath(session.LocalRoot, plan.DestinationDirectory).Replace('\\', '/');
        if (relative == ".") relative = "";
        var sources = plan.Items.Select(item => item.Source).ToArray();
        var targets = plan.Items.Where(item => !item.IsSameLocation && (!item.DestinationExists
            || options.Conflicts == FileMoveConflictBehavior.OverwriteFiles && item.CanOverwrite)).Select(item => item.Destination).ToArray();
        foreach (var path in sources.Concat(targets)) LocalItemCreationStarted?.Invoke(path);
        try
        {
            var progress = new Progress<FileMoveProgress>(value =>
            {
                if (disposed || !IsBusy) return;
                SetProgressTarget(value.Path);
                ProgressMessage = $"已移动 {value.CompletedItems}/{value.ItemCount} 项；版本控制变更尚未提交";
            });
            var count = await Task.Run(() => Operations.MoveFilesAsync(plan, options, token, progress,
                path => AddLog($"移动覆盖前备份：{path}")), token);
            var skipped = plan.Items.Count(item => !item.IsSameLocation) - count;
            AddLog($"移动完成：{count} 项，跳过 {skipped} 项。受版本控制的源项目保留 SVN 历史；变更尚未提交。");
        }
        catch
        {
            AddLog("移动未全部完成：已移动的内容保留在目标位置，剩余内容保留在源位置，请检查两处。");
            throw;
        }
        finally
        {
            try
            {
                var directories = await Task.Run(() => targets.Where(Directory.Exists).SelectMany(EnumerateMovedDirectories).ToArray());
                await RefreshLocalTransfersAsync(session, stayInCurrentDirectory ? CurrentRelative : relative,
                    plan.DestinationDirectory, targets, directories, sources, preserveFilters: stayInCurrentDirectory);
            }
            catch (Exception ex) { AddLog($"移动后本地状态读取失败，请刷新：{ex.Message}"); }
            foreach (var path in sources.Concat(targets)) LocalItemCreationFinished?.Invoke(path);
        }
    }

    private static IEnumerable<string> EnumerateMovedDirectories(string directory)
    {
        LocalFileTransfer.ValidatePath(directory);
        yield return directory;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child).Equals(".svn", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var descendant in EnumerateMovedDirectories(child)) yield return descendant;
        }
    }
}
