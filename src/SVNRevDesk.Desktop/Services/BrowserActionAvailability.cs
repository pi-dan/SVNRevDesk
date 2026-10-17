using System.IO;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Desktop.Services;

// Shared by toolbar buttons and menus so both expose the same operation requirements.
public static class BrowserActionAvailability
{
    public static bool CanExecute(string operation, RepositorySession? session, IReadOnlyList<BrowserEntry> selection)
    {
        if (operation == "checkout") return true;
        if (session == null) return false;
        var one = selection.Count == 1 ? selection[0] : null;
        var local = session.LocalRoot != null;
        var selectedLocal = local && selection.Count > 0 && selection.All(entry => entry.LocalPath != null);
        var containsRoot = local && selection.Any(entry => string.Equals(entry.LocalPath, session.LocalRoot, StringComparison.OrdinalIgnoreCase));
        var ignoreTarget = local && one?.LocalPath != null && one.ExistsLocally && !containsRoot && one.ServerRevision == null
            && one.BaseRevision == null && Directory.Exists(Path.GetDirectoryName(one.LocalPath))
            && !Directory.Exists(Path.Combine(one.LocalPath, ".svn"));

        return operation switch
        {
            "associate" => !local,
            "branch" => true,
            "log" => selection.Count <= 1,
            "update-directory" or "commit-directory" or "cleanup" or "directory-diff"
                or "switch" or "merge" or "merge-preview" or "update-directory-revision" or "reverse-merge-directory" => local,
            "update-revision" => selectedLocal && selection.All(entry => entry.BaseRevision != null || entry.ServerRevision != null),
            "reverse-merge" => one?.LocalPath != null && one.ExistsLocally && one.BaseRevision >= 0 && one.State != FileState.Conflict,
            "update" or "commit" or "revert" => selectedLocal,
            "delete" => selectedLocal && !containsRoot && selection.All(entry => entry.State != FileState.LocalDeleted),
            "add" => selectedLocal && selection.All(entry => entry.State == FileState.LocalAdded && entry.ExistsLocally),
            "ignore" => ignoreTarget && one!.IsUnversioned && one.State == FileState.LocalAdded,
            "unignore" => ignoreTarget && one!.State == FileState.Ignored
                && string.Equals(one.LocalIgnoreRulePath, one.LocalPath, StringComparison.OrdinalIgnoreCase),
            "open" or "properties" => one != null,
            "export" => selection.Count > 0 && selection.All(entry => entry.ServerRevision != null),
            "show-in-explorer" => one?.LocalPath != null,
            "compare-server" => one?.LocalPath != null && one.ExistsLocally && one.ServerRevision != null,
            "compare-base" => one?.LocalPath != null && one.ExistsLocally && one.BaseRevision >= 0,
            "compare-history" or "blame" => one is { IsDirectory: false, ServerRevision: not null },
            "move" => one?.LocalPath != null && one.ExistsLocally && !containsRoot && one.State is not (FileState.LocalDeleted or FileState.Conflict),
            "move-to" => selectedLocal && !containsRoot && selection.All(entry => entry.ExistsLocally
                && entry.State is not (FileState.LocalDeleted or FileState.Conflict)),
            "set-property" => one?.LocalPath != null && one.ExistsLocally && one.State != FileState.LocalDeleted,
            "lock" => one is { IsDirectory: false, ServerRevision: not null, Lock: null }
                && one.LocalPath != null && one.ExistsLocally,
            "unlock" => one is { IsDirectory: false, Lock: not null } && one.LocalPath != null && one.ExistsLocally,
            "resolve" => one is { State: FileState.Conflict } && one.LocalPath != null,
            _ => false
        };
    }
}
