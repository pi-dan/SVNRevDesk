using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    private async Task MoveToAbsentFileAsync(FileMoveItem item, string localRoot, CancellationToken token)
    {
        // There is no local target content to back up. Read its properties only for restoration.
        var properties = !item.IsVersioned || item.DestinationStatus?.Item == "missing"
            ? await ReadMovePropertiesAsync(item.Destination, token, useBaseRevision: item.DestinationStatus?.Item == "deleted") : null;
        var current = (await PrepareFileRenameAsync(item.Source, item.Destination, localRoot, token)).Items.Single();
        CheckMoveDestination(item, current);
        if (current.IsVersioned != item.IsVersioned) throw new IOException("源文件版本控制状态已变化，请重新操作。\n" + item.Source);
        CheckMoveFile(current);
        token.ThrowIfCancellationRequested();

        // Complete one local SVN change before accepting cancellation between batch items.
        var targetChanged = false;
        try
        {
            if (item.IsVersioned)
            {
                if (item.DestinationStatus?.Item == "missing")
                {
                    targetChanged = true;
                    await runner.RunAsync(["delete", "--force", SvnRunner.Target(item.Destination)], CancellationToken.None);
                }
                // SVN also understands the exact reverse of a pending move. Reverting either end first
                // would discard the relationship, so let SVN move it back together with its local edits.
                await RunLocalMoveAsync(item, CancellationToken.None);
            }
            else
            {
                // An ordinary source should edit the original target, retaining its SVN history/properties.
                targetChanged = true;
                await runner.RunAsync(["revert", "--depth", "empty", SvnRunner.Target(item.Destination)], CancellationToken.None);
                await RestoreMovePropertiesAsync(item.Destination, properties!);
                MakeMoveFileWritable(item.Destination);
                File.Move(item.Source, item.Destination, overwrite: true);
            }
        }
        catch (Exception moveError)
        {
            if (!targetChanged) throw;
            try
            {
                var destination = (await ReadMoveStatusesAsync(item.Destination, "empty", CancellationToken.None))
                    .FirstOrDefault(status => SameMovePath(status.Path, item.Destination));
                if (!File.Exists(item.Source) || destination?.MovedFrom != null)
                    throw new IOException("SVN 已执行了部分移动，请检查源、目标的状态。");
                if (item.IsVersioned && (File.Exists(item.Destination) || Directory.Exists(item.Destination)))
                    throw new IOException("目标位置出现了其他内容，已保留供人工检查。");
                await runner.RunAsync(["revert", "--depth", "empty", SvnRunner.Target(item.Destination)], CancellationToken.None);
                await RestoreMovePropertiesAsync(item.Destination, properties!);
                MakeMoveFileWritable(item.Destination);
                if (item.DestinationStatus?.Item == "deleted")
                    await runner.RunAsync(["delete", "--force", SvnRunner.Target(item.Destination)], CancellationToken.None);
                else File.Delete(item.Destination);
            }
            catch (Exception restoreError)
            {
                throw new IOException($"移动未完成，请检查源文件与目标位置：{item.Destination}\n{moveError.Message}\n恢复原状态失败：{restoreError.Message}", moveError);
            }
            throw new IOException("移动未完成，源文件已保留，目标已恢复原删除或缺失状态：\n" + item.Destination + "\n" + moveError.Message, moveError);
        }
    }
}
