namespace SVNRevDesk.Core.Models;

public enum FileState
{
    Equal,
    ServerChanged,
    LocalModified,
    BothChanged,
    NotDownloaded,
    Missing,
    LocalAdded,
    ServerDeleted,
    Conflict,
    Unchecked,
    // Append new values so persisted numeric states remain compatible.
    LocalDeleted,
    Ignored
}

public static class FileStateNames
{
    public static string DisplayName(this FileState state) => state switch
    {
        FileState.Equal => "一致",
        FileState.ServerChanged => "服务器有更新",
        FileState.LocalModified => "本地已修改",
        FileState.BothChanged => "双方均有修改",
        FileState.NotDownloaded => "本地未下载",
        FileState.Missing => "本地缺失",
        FileState.LocalAdded => "本地新增",
        FileState.LocalDeleted => "本地已删除",
        FileState.Ignored => "已忽略",
        FileState.ServerDeleted => "服务器已删除",
        FileState.Conflict => "冲突",
        _ => "未检查"
    };
}
