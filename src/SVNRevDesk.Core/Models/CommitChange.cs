namespace SVNRevDesk.Core.Models;

// A fresh local status query supplies the commit list; browsing/server caches are not used here.
public sealed record CommitChange(string Path, string RelativePath, bool IsDirectory, WorkingStatus Status,
    IReadOnlyList<string> RequiredParents, string UnavailableReason = "")
{
    public IReadOnlyList<string> RequiredMoves { get; init; } = [];
    public IEnumerable<string> RequiredChanges => RequiredParents.Concat(RequiredMoves);
    public bool NeedsAddition => Status.Item == "unversioned";
    public bool CanCommit => (Status.HasLocalChanges || NeedsAddition) && !Status.TreeConflict && UnavailableReason.Length == 0
        && Status.Item is not ("conflicted" or "obstructed" or "missing" or "incomplete" or "external")
        && Status.Properties != "conflicted";
    public string Extension => IsDirectory ? "—" : System.IO.Path.GetExtension(Path);
    public string PropertyText => Status.Properties switch { "modified" => "修改", "conflicted" => "冲突", _ => "—" };
    public string LockOwner => Status.LocalLock?.Owner ?? "—";
    public bool RequiresChildren => IsDirectory && Status.Item == "deleted";
    public string StateText => Status.TreeConflict || Status.Item is "conflicted" or "obstructed" || Status.Properties == "conflicted"
        ? "冲突" : Status.Item switch
        {
            "added" => "新增（已添加）", "deleted" => "删除", "replaced" => "替换", "missing" => "本地缺失",
            "unversioned" => "未版本控制", "incomplete" => "不完整", _ => Status.Item == "modified" ? "内容修改" : "属性修改"
        };
    public string Detail => UnavailableReason.Length > 0 ? UnavailableReason : !CanCommit ? Status.Item switch
    {
        "missing" => "需先恢复文件，或通过 SVN 安排删除。",
        _ => "需先处理冲突或修复工作副本状态。"
    } : RequiredMoves.Count > 0 ? "移动需要同时提交原位置的删除和新位置的新增；勾选会自动关联另一端。"
        : NeedsAddition ? "勾选后将在提交前自动添加到 SVN；目录不自动包含未勾选的文件。"
        : RequiresChildren ? "删除目录会同时删除服务器上的全部子项。"
        : IsDirectory && Status.Item is "added" or "replaced" ? "提交子项时，此父目录也必须勾选。"
        : Status.Item == "modified" && Status.Properties == "modified" ? "内容和属性均有修改。" : "";
}
