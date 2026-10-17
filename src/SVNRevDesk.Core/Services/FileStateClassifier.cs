using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Services;

public static class FileStateClassifier
{
    public static FileState Classify(bool onServer, bool onDisk, WorkingStatus? status, bool hasWorkingCopy)
    {
        if (!hasWorkingCopy) return FileState.Unchecked;
        if (status?.Item == "ignored" || status?.IsLocallyIgnored == true) return onServer ? FileState.BothChanged : FileState.Ignored;
        if (status?.Item == "external") return FileState.Unchecked;
        if (status is { TreeConflict: true } || status?.Item is "conflicted" or "obstructed"
            || status?.Properties == "conflicted") return FileState.Conflict;
        if (status?.Item == "deleted")
            return onServer && status.HasServerChanges ? FileState.BothChanged : FileState.LocalDeleted;

        if (!onServer)
        {
            // Unversioned and scheduled additions are different from tracked files deleted remotely.
            if (status == null || status.Item is "unversioned" or "added") return FileState.LocalAdded;
            return FileState.ServerDeleted;
        }
        if (status?.Item is "unversioned" or "added") return FileState.BothChanged;
        if (status?.HasLocalChanges == true)
            return status.HasServerChanges ? FileState.BothChanged : FileState.LocalModified;
        if (!onDisk) return status?.Item == "missing" ? FileState.Missing : FileState.NotDownloaded;
        if (status?.HasServerChanges == true) return FileState.ServerChanged;
        return status == null ? FileState.Unchecked : FileState.Equal;
    }
}
