using System.Xml.Linq;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    private static void CheckMoveDestination(FileMoveItem expected, FileMoveItem current)
    {
        if (!expected.CanOverwrite || !current.CanOverwrite || current.IsReverseMove != expected.IsReverseMove
            || current.DestinationSize != expected.DestinationSize
            || current.DestinationModifiedAt != expected.DestinationModifiedAt
            || current.DestinationStatus != expected.DestinationStatus)
            throw new IOException("同名目标在确认期间已变化，已保留源文件和目标文件，请重新操作：\n" + current.Destination);
    }

    private async Task OverwriteMoveFileAsync(FileMoveItem item, string localRoot, bool backupBeforeOverwrite,
        CancellationToken token, Action<string>? backupCreated)
    {
        LocalFileTransfer.ValidatePath(item.Source);
        LocalFileTransfer.ValidatePath(item.Destination);
        CheckMoveFile(item);
        if (item.DestinationSize == null)
        {
            await MoveToAbsentFileAsync(item, localRoot, token);
            return;
        }
        var targetIsVersioned = item.DestinationStatus?.Item is "normal" or "modified" or "added";
        // Property values are needed only to restore the original working state if SVN rejects the move.
        var properties = item.IsVersioned && targetIsVersioned ? await ReadMovePropertiesAsync(item.Destination, token) : null;
        if (backupBeforeOverwrite)
        {
            var root = Path.GetFullPath(backupDirectory ?? Path.Combine(Path.GetTempPath(), "SVNRevDesk", "Backups"));
            if (Inside(root, item.Source) || Inside(root, item.Destination)) throw new IOException("备份目录不能位于移动项目内。");
            var backup = Path.Combine(root, "moved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"),
                Path.GetRelativePath(localRoot, item.Destination));
            LocalFileTransfer.ValidatePath(backup);
            await BackupNewItemAsync(item.Destination, backup, localRoot, token);
            backupCreated?.Invoke(backup);
        }
        token.ThrowIfCancellationRequested();
        var current = (await PrepareFileRenameAsync(item.Source, item.Destination, localRoot, token)).Items.Single();
        CheckMoveDestination(item, current);
        if (current.IsVersioned != item.IsVersioned) throw new IOException("源文件版本控制状态已变化，请重新操作。\n" + item.Source);
        CheckMoveFile(item);
        if (!item.IsVersioned)
        {
            // Retain the existing destination's SVN history/properties when an ordinary file replaces its contents.
            File.Move(item.Source, item.Destination, overwrite: true);
            return;
        }

        // Temporarily retain the old content for failure recovery, even when permanent backup is disabled.
        // Finish this individual SVN replacement before accepting cancellation between batch items.
        var saved = Path.Combine(Path.GetDirectoryName(item.Destination)!, ".svnclient-new-" + Guid.NewGuid().ToString("N"));
        File.Move(item.Destination, saved);
        try
        {
            if (targetIsVersioned)
                await runner.RunAsync(["delete", "--force", SvnRunner.Target(item.Destination)], CancellationToken.None);
            await RunLocalMoveAsync(item, CancellationToken.None);
        }
        catch (Exception moveError)
        {
            try
            {
                var destination = (await ReadMoveStatusesAsync(item.Destination, "empty", CancellationToken.None))
                    .FirstOrDefault(status => SameMovePath(status.Path, item.Destination));
                if (!File.Exists(item.Source) || destination?.MovedFrom != null)
                    throw new IOException("SVN 已执行了部分移动，请检查源、目标的状态。");
                if (File.Exists(item.Destination) || Directory.Exists(item.Destination))
                    throw new IOException("目标位置已有其他内容，已保留原内容供人工恢复。");
                if (targetIsVersioned)
                    await runner.RunAsync(["revert", "--depth", "empty", SvnRunner.Target(item.Destination)], CancellationToken.None);
                if (File.Exists(item.Destination)) MakeMoveFileWritable(item.Destination);
                File.Move(saved, item.Destination, overwrite: true);
                if (targetIsVersioned)
                {
                    if (item.DestinationStatus?.Item == "added")
                        await runner.RunAsync(["add", "--depth", "empty", SvnRunner.Target(item.Destination)], CancellationToken.None);
                    await RestoreMovePropertiesAsync(item.Destination, properties!);
                }
            }
            catch (Exception restoreError)
            {
                var recoveryPath = File.Exists(saved) ? saved : item.Destination;
                throw new IOException($"移动未完成。目标原内容保留在：{recoveryPath}\n{moveError.Message}\n恢复状态失败：{restoreError.Message}", moveError);
            }
            throw new IOException("移动未完成，目标原内容已恢复：\n" + item.Destination + "\n" + moveError.Message, moveError);
        }
        // Failure to remove a temporary original must be visible so it can be cleaned up manually.
        MakeMoveFileWritable(saved);
        File.Delete(saved);
    }

    private static void MakeMoveFileWritable(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void CheckMoveFile(FileMoveItem item)
    {
        LocalFileTransfer.ValidatePath(item.Destination);
        var file = new FileInfo(item.Destination);
        if (!item.CanOverwrite || Directory.Exists(item.Destination) || file.Exists != item.DestinationSize.HasValue
            || file.Exists && (file.Length != item.DestinationSize || file.LastWriteTimeUtc != item.DestinationModifiedAt))
            throw new IOException("覆盖期间目标已变化，已保留该项目，请重试：\n" + item.Destination);
    }

    private async Task<Dictionary<string, byte[]>> ReadMovePropertiesAsync(string path, CancellationToken token, bool useBaseRevision = false)
    {
        var arguments = new List<string> { "proplist", "--xml", "--verbose" };
        if (useBaseRevision) arguments.AddRange(["-r", "BASE"]);
        arguments.Add(SvnRunner.Target(path));
        var xml = XDocument.Parse(await runner.RunAsync(arguments, token));
        return xml.Descendants("property").ToDictionary(property => (string)property.Attribute("name")!,
            property => (string?)property.Attribute("encoding") == "base64" ? Convert.FromBase64String(property.Value)
                : System.Text.Encoding.UTF8.GetBytes(property.Value), StringComparer.Ordinal);
    }

    private async Task RestoreMovePropertiesAsync(string path, IReadOnlyDictionary<string, byte[]> properties)
    {
        var current = await ReadMovePropertiesAsync(path, CancellationToken.None);
        foreach (var name in current.Keys.Except(properties.Keys))
            await runner.RunAsync(["propdel", name, SvnRunner.Target(path)], CancellationToken.None);
        var directory = temporaryDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "svnclient-property-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (name, value) in properties)
            {
                if (current.TryGetValue(name, out var existing) && value.SequenceEqual(existing)) continue;
                await File.WriteAllBytesAsync(temporary, value);
                await runner.RunAsync(["propset", name, "--file", temporary, SvnRunner.Target(path)], CancellationToken.None);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
