using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.Desktop.Services;

// User workflows live here; SVN command construction and state classification live in Core.
public sealed class ClientActions(Window owner, MainViewModel model)
{
    private readonly OperationDialogs dialogs = new(owner, model);

    public async Task ExecuteAsync(string operation, IReadOnlyList<BrowserEntry> selection, CancellationToken token,
        string? exportRevision = null)
    {
        switch (operation)
        {
            case "checkout": await CheckoutAsync(token); return;
            case "associate": await AssociateAsync(token); return;
        }
        if (model.Session == null) throw new InvalidOperationException("请先打开工作副本或仓库地址。");
        switch (operation)
        {
            case "open": await OpenAsync(One(selection), token); break;
            case "show-in-explorer":
                var explorerEntry = One(selection);
                var shownPath = FileExplorer.Show(explorerEntry.LocalPath);
                model.AddLog(shownPath.Equals(explorerEntry.LocalPath, StringComparison.OrdinalIgnoreCase)
                    ? $"已在资源管理器中选中：{shownPath}"
                    : $"项目尚未下载或本地缺失，已打开最近的本地目录：{shownPath}");
                break;
            case "update":
                var updates = Selected(selection);
                for (var i = 0; i < updates.Count; i++)
                {
                    var entry = updates[i];
                    model.SetProgressTarget($"第 {i + 1}/{updates.Count} 项：{Local(entry)}");
                    model.AddLog(await model.Operations.UpdateAsync(Local(entry), entry.IsDirectory, token));
                }
                await model.RefreshPathsAsync(updates.Select(Local), token);
                break;
            case "update-directory":
                model.SetProgressTarget(CurrentLocal());
                model.AddLog(await model.Operations.UpdateAsync(CurrentLocal(), true, token));
                await model.RefreshPathsAsync([CurrentLocal()], token); break;
            case "update-revision": await UpdateRevisionAsync(Selected(selection), token); break;
            case "update-directory-revision": await UpdateRevisionAsync([], token); break;
            case "reverse-merge": await ReverseMergeAsync(One(selection), token); break;
            case "reverse-merge-directory": await ReverseMergeAsync(null, token); break;
            case "commit": await CommitAsync(Selected(selection).Select(Local).ToList(), token); break;
            case "commit-directory": await CommitAsync([CurrentLocal()], token); break;
            case "add":
                model.AddLog(await model.Operations.AddAsync(Selected(selection).Select(Local), token));
                await model.RefreshPathsAsync(Selected(selection).Select(Local), token); break;
            case "delete": await DeleteAsync(selection, token); break;
            case "revert": await RevertAsync(selection, token); break;
            case "move": await MoveAsync(One(selection), token); break;
            case "move-to": await MoveToFolderAsync(Selected(selection), token); break;
            case "compare-server": await CompareAsync(One(selection), false, token); break;
            case "compare-base": await CompareAsync(One(selection), true, token); break;
            case "compare-history": await CompareHistoryAsync(One(selection), token); break;
            case "directory-diff":
                ShowOutput("目录差异（服务器 → 本地）", await model.Operations.DirectoryDiffAsync(CurrentLocal(), token)); break;
            case "log": ShowHistory(selection.Count == 0 ? [CurrentUrl()] : selection.Select(RevisionHistoryUrl).ToArray(), token); break;
            case "batch-rename": await BatchRenameAsync(selection, token); break;
            case "blame":
                ShowOutput("逐行追溯", await model.Operations.BlameAsync(FileEntry(One(selection)).Url, token)); break;
            case "lock": await LockAsync(One(selection), token); break;
            case "unlock":
                model.AddLog(await model.Operations.UnlockAsync(Local(FileEntry(One(selection))), token));
                await model.RefreshPathsAsync([Local(One(selection))], token); break;
            case "cleanup":
                model.AddLog(await model.Operations.CleanupAsync(model.Session.LocalRoot ?? throw new InvalidOperationException("请打开本地工作副本。"), token));
                await model.RefreshCurrentDirectoryAsync(token); break;
            case "resolve": await ResolveAsync(One(selection), token); break;
            case "properties":
                var propertyEntry = One(selection);
                ShowOutput("SVN 属性", await model.Operations.PropertiesAsync(propertyEntry.ExistsLocally ? Local(propertyEntry) : propertyEntry.Url, token)); break;
            case "set-property": await SetPropertyAsync(One(selection), token); break;
            case "ignore": case "unignore": await model.SetIgnoredAsync(One(selection), operation == "ignore", token); break;
            case "export": await ExportAsync(Selected(selection), exportRevision ?? SnapshotRevision(), token); break;
            case "branch": await BranchAsync(token); break;
            case "switch": await SwitchAsync(token); break;
            case "merge-preview": await MergeAsync(true, token); break;
            case "merge": await MergeAsync(false, token); break;
            default: throw new ArgumentException($"未知操作：{operation}");
        }
    }

    public bool TryOpenLocalFile(BrowserEntry entry)
    {
        // Launching an existing file does not touch SVN, so it need not stop background server checks.
        if (!CanOpenLocalFileDirectly(entry)) return false;
        OpenFile(Local(entry));
        return true;
    }

    public static bool CanOpenLocalFileDirectly(BrowserEntry entry) => !entry.IsDirectory && entry.ExistsLocally
        && entry.State is not (FileState.ServerChanged or FileState.BothChanged or FileState.Conflict);

    private async Task OpenAsync(BrowserEntry entry, CancellationToken token)
    {
        if (entry.IsDirectory) { await model.NavigateAsync(entry.RelativePath, token); return; }
        model.SetProgressTarget(entry.LocalPath ?? entry.Url);
        // Use the displayed state; periodic and manual refresh keep server information current.
        if (entry.State == FileState.Conflict)
        {
            var choice = dialogs.Choose("文件存在冲突", "请先检查冲突。查看服务器版本会下载到临时目录，不覆盖工作副本。",
                "打开当前本地文件", "查看服务器版本（临时文件）", "处理冲突…");
            if (choice == 0 && entry.ExistsLocally) OpenFile(Local(entry));
            else if (choice == 1) await OpenTemporaryAsync(entry, token);
            else if (choice == 2) await ResolveAsync(entry, token);
            return;
        }
        if (CanOpenLocalFileDirectly(entry))
        {
            OpenFile(Local(entry)); return;
        }
        if (entry.ServerRevision == null && entry.State is FileState.ServerDeleted or FileState.LocalAdded)
            throw new InvalidOperationException("服务器上没有对应文件，本地文件也不存在。");
        if (entry.LocalPath == null)
        {
            await OpenTemporaryAsync(entry, token); return;
        }

        var choices = entry.ExistsLocally
            ? new[] { "打开本地版本", "更新并打开服务器版本", "只查看服务器版本（临时文件）" }
            : new[] { "下载到工作副本并打开", "只查看服务器版本（临时文件）" };
        var answer = dialogs.Choose("选择打开版本", $"{entry.Name}\n状态：{entry.StateText}\n本地最后修改：{entry.LocalRevisionText}，服务器最后修改：{entry.ServerRevisionText}", choices);
        if (answer < 0) return;
        if (entry.ExistsLocally && answer == 0) { OpenFile(Local(entry)); return; }
        if (answer == choices.Length - 1) { await OpenTemporaryAsync(entry, token); return; }

        if (entry.ExistsLocally || entry.State is FileState.LocalModified or FileState.BothChanged)
        {
            if (!Confirm("覆盖本地文件", $"将用服务器最新版本替换：\n{Local(entry)}\n\n当前本地文件会先备份。未提交的内容和属性修改会被撤销。是否继续？")) return;
            var backup = await model.Operations.ReplaceWithServerAsync(Local(entry), token);
            if (backup != null) model.AddLog($"覆盖前备份：{backup}");
        }
        else model.AddLog(await model.Operations.UpdateAsync(Local(entry), false, token));
        await model.RefreshPathsAsync([Local(entry)], token);
        var refreshed = model.Entries.FirstOrDefault(item => item.RelativePath == entry.RelativePath);
        if (refreshed?.State == FileState.Conflict) throw new InvalidOperationException("更新产生冲突，请解决冲突后打开。");
        if (!File.Exists(Local(entry))) throw new InvalidOperationException("文件未下载成功，或刚刚被服务器删除，请刷新检查。");
        OpenFile(Local(entry));
    }

    private async Task OpenTemporaryAsync(BrowserEntry entry, CancellationToken token)
    {
        model.SetProgressTarget(entry.Url);
        var path = await model.Operations.DownloadForViewingAsync(entry.Url, SnapshotRevision(), model.Settings.CacheDirectory, token);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        model.AddLog($"服务器版本已下载到只读临时文件：{path}");
        OpenFile(path);
    }

    private async Task CompareAsync(BrowserEntry entry, bool compareBase, CancellationToken token)
    {
        model.SetProgressTarget(entry.LocalPath ?? entry.Url);
        if (entry.IsDirectory)
        {
            var difference = compareBase
                ? await model.Operations.DirectoryBaseDiffAsync(Local(entry), token)
                : await model.Operations.DirectoryDiffAsync(Local(entry), token);
            ShowOutput(compareBase ? "目录差异（基础版本 → 本地）" : "目录差异（服务器 → 本地）", difference);
            return;
        }
        if (!entry.ExistsLocally) throw new InvalidOperationException("本地文件不存在，请先下载，或选择比较历史版本。");
        string server;
        if (compareBase) server = await model.Operations.DownloadBaseAsync(Local(entry), model.Settings.CacheDirectory, token);
        else
        {
            await model.RefreshPathsAsync([Local(entry)], token);
            server = await model.Operations.DownloadForViewingAsync(entry.Url, SnapshotRevision(), model.Settings.CacheDirectory, token);
        }
        await ShowComparisonAsync(server, Local(entry), compareBase ? "基础版本" : $"服务器 r{SnapshotRevision()}", "本地工作文件", token);
    }

    private async Task CompareHistoryAsync(BrowserEntry entry, CancellationToken token)
    {
        FileEntry(entry);
        model.SetProgressTarget(entry.Url);
        var values = dialogs.Ask("比较历史版本", "输入两个仓库修订号。历史文件会下载到缓存，不改变工作副本。",
            new InputField("左侧修订号", entry.LocalRevision?.ToString() ?? "1"),
            new InputField("右侧修订号", SnapshotRevision()));
        if (values == null) return;
        if (!long.TryParse(values[0], out var leftRevision) || !long.TryParse(values[1], out var rightRevision)
            || leftRevision < 1 || rightRevision < 1) throw new ArgumentException("修订号必须是正整数。");
        var left = await model.Operations.DownloadForViewingAsync(entry.Url, leftRevision.ToString(), model.Settings.CacheDirectory, token, SnapshotRevision());
        var right = await model.Operations.DownloadForViewingAsync(entry.Url, rightRevision.ToString(), model.Settings.CacheDirectory, token, SnapshotRevision());
        await ShowComparisonAsync(left, right, $"服务器 r{leftRevision}", $"服务器 r{rightRevision}", token);
    }

    private async Task ShowComparisonAsync(string left, string right, string leftTitle, string rightTitle, CancellationToken token)
    {
        model.BeginProgressPhase("启动比较工具", "准备文件比较工具");
        var start = await ComparisonTool.PrepareAsync(owner, model.Settings.ExternalDiffExecutable,
            left, right, leftTitle, rightTitle, model.Settings.StorageDirectory, token);
        token.ThrowIfCancellationRequested();
        // The operation ends after launch; reading the comparison must not keep progress running.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动文件比较工具。");
        model.AddLog("已打开文件比较工具。");
    }

    private async Task CommitAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        var root = model.Session!.LocalRoot!;
        model.BeginProgressPhase("读取提交列表", "读取本地提交变更");
        var changes = await Task.Run(() => model.Operations.ReadCommitChangesAsync(paths, root, token), token);
        token.ThrowIfCancellationRequested();
        if (changes.Count == 0)
        {
            dialogs.Inform("提交", "所选范围内没有本地变更。");
            return;
        }
        var dialog = new CommitWindow(owner, root, changes, model.Session.Url);
        if (!dialogs.ShowCommit(dialog)) return;
        token.ThrowIfCancellationRequested();
        var selected = dialog.Model.SelectedChanges;
        model.BeginProgressPhase("提交修改", $"提交已勾选的 {selected.Count} 项");
        model.AddLog(await model.Operations.CommitSelectedAsync(selected, dialog.Model.Message, root, token));
        await model.RefreshPathsAsync(selected.Select(change => change.Path), token);
    }

    public bool ConfirmDelete(IReadOnlyList<BrowserEntry> selection)
    {
        var paths = Selected(selection).Select(Local).ToList();
        return Confirm("确认删除", "将删除以下本地文件 / 文件夹，本地文件不会进入回收站。\n\n"
            + "本地新增项目会先备份到程序的 Backups 目录；未加入版本控制的直接删除，已添加但未提交的会同时取消添加，不影响服务器。\n\n"
            + "已有版本控制的项目会安排 SVN 删除，服务器内容将在提交后删除。为保护未提交内容，SVN 仍会拒绝删除有本地修改或含未版本控制内容的这类项目。\n\n"
            + string.Join("\n", paths));
    }

    private async Task DeleteAsync(IReadOnlyList<BrowserEntry> selection, CancellationToken token)
    {
        var paths = Selected(selection).Select(Local).ToList();
        model.SetProgressTarget(string.Join("、", paths.Select(Path.GetFileName)));
        model.AddLog(await Task.Run(() => model.Operations.DeleteSelectedAsync(paths, model.Session!.LocalRoot!, token), token));
        await model.RefreshPathsAsync(paths, token);
    }

    private async Task RevertAsync(IReadOnlyList<BrowserEntry> selection, CancellationToken token)
    {
        var paths = Selected(selection).Select(Local).ToList();
        if (!Confirm("撤销本地修改", "将撤销以下项目的未提交修改（目录包含子项）。文件内容会先备份，但 SVN 属性及调度信息需通过版本历史恢复。\n\n" + string.Join("\n", paths))) return;
        var backup = Path.Combine(model.Settings.BackupDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        for (var i = 0; i < paths.Count; i++) BackupPath(paths[i], Path.Combine(backup, i.ToString()), token);
        model.AddLog($"撤销前文件备份：{backup}");
        model.AddLog(await model.Operations.RevertAsync(paths, token));
        await model.RefreshPathsAsync(paths, token);
    }

    private static void BackupPath(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(source) && !Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"备份遇到链接，请先人工处理：{source}");
        if (File.Exists(source))
        {
            Directory.CreateDirectory(destination);
            File.Copy(source, Path.Combine(destination, Path.GetFileName(source))); return;
        }
        Directory.CreateDirectory(destination);
        foreach (var child in Directory.EnumerateFileSystemEntries(source))
            if (!Path.GetFileName(child).Equals(".svn", StringComparison.OrdinalIgnoreCase))
                BackupPath(child, Path.Combine(destination, Path.GetFileName(child)), token);
    }

    private async Task MoveAsync(BrowserEntry entry, CancellationToken token)
    {
        var source = Local(entry);
        var values = dialogs.Ask("重命名 / 移动", "可以填写新文件名，或工作副本内部的完整目标路径。更改将在下次提交时生效。",
            new InputField("目标名称或路径", entry.Name));
        if (values == null) return;
        var destination = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, values[0].Trim()));
        if (!RepositoryService.IsDescendant(destination, model.Session!.LocalRoot!))
            throw new ArgumentException("目标必须位于当前打开的工作副本内部。");
        var plan = await Task.Run(() => model.Operations.PrepareFileRenameAsync(source, destination, model.Session.LocalRoot!, token), token);
        var options = dialogs.ResolveMoveConflicts(plan);
        if (options != null) await model.MoveLocalFilesAsync(plan, options, token);
    }

    private async Task MoveToFolderAsync(IReadOnlyList<BrowserEntry> selection, CancellationToken token)
    {
        var root = model.Session!.LocalRoot!;
        var dialog = new OpenFolderDialog { Title = "选择工作副本内的目标目录", InitialDirectory = CurrentLocal(), Multiselect = false };
        if (!dialogs.ChooseFolder(dialog)) return;
        var plan = await Task.Run(() => model.Operations.PrepareFileMoveAsync(selection.Select(Local), dialog.FolderName, root, token), token);
        var options = dialogs.ResolveMoveConflicts(plan);
        if (options != null) await model.MoveLocalFilesAsync(plan, options, token);
    }

    private async Task LockAsync(BrowserEntry entry, CancellationToken token)
    {
        FileEntry(entry);
        var values = dialogs.Ask("获取锁", "获取所选文件的锁。已被他人锁定时会显示 SVN 返回的原因。",
            new InputField("锁定说明", Multiline: true));
        if (values == null) return;
        model.AddLog(await model.Operations.LockAsync(Local(entry), values[0], token));
        await model.RefreshPathsAsync([Local(entry)], token);
    }

    private async Task ResolveAsync(BrowserEntry entry, CancellationToken token)
    {
        var choice = dialogs.Choose("解决冲突", "“标记已解决”应在手工编辑完成后使用。选择本地或服务器内容会改变冲突文件；目录树冲突需要先人工处理文件布局。",
            "标记已解决（保留当前工作文件）", "使用本地内容（mine-full）", "使用服务器内容（theirs-full）");
        if (choice < 0) return;
        if (choice != 0 && !Confirm("确认解决冲突", "将使用选定版本解决冲突，当前文件内容会先备份。是否继续？")) return;
        var path = Local(entry);
        if (File.Exists(path))
        {
            var directory = Path.Combine(model.Settings.BackupDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backup = Path.Combine(directory, Path.GetFileName(path));
            File.Copy(path, backup); model.AddLog($"冲突文件备份：{backup}");
        }
        model.AddLog(await model.Operations.ResolveAsync(path, new[] { "working", "mine-full", "theirs-full" }[choice], token));
        await model.RefreshPathsAsync([path], token);
    }

    private async Task SetPropertyAsync(BrowserEntry entry, CancellationToken token)
    {
        var values = dialogs.Ask("设置 SVN 属性", "修改保存在工作副本中，下次提交时上传。目录的忽略规则使用 svn:ignore，值为每行一个匹配模式。",
            new InputField("属性名", entry.IsDirectory ? "svn:ignore" : "svn:needs-lock"), new InputField("属性值", Multiline: true));
        if (values == null) return;
        if (string.IsNullOrWhiteSpace(values[0])) throw new ArgumentException("属性名不能为空。");
        model.AddLog(await model.Operations.SetPropertyAsync(Local(entry), values[0].Trim(), values[1], token));
        await model.RefreshPathsAsync([Local(entry)], token);
    }

    private async Task ExportAsync(IReadOnlyList<BrowserEntry> selection, string revision, CancellationToken token)
    {
        var dialog = new OpenFolderDialog { Title = $"导出所选 {selection.Count} 项：选择保存文件夹（下载服务器版本，保留原名称）" };
        if (Directory.Exists(model.Settings.LastExportDirectory)) dialog.InitialDirectory = model.Settings.LastExportDirectory;
        if (!dialogs.ChooseFolder(dialog)) return;
        var exporter = new RepositoryExporter(model.Operations);
        await exporter.ExportAsync(selection, dialog.FolderName, revision, token,
            (index, count, item) => model.SetProgressTarget($"第 {index}/{count} 项：{item.Destination}"),
            item => model.AddLog($"已导出：{item.Destination}"));
        model.Settings.LastExportDirectory = dialog.FolderName;
        model.SaveSession();
        model.AddLog($"导出完成，共 {selection.Count} 项。保存位置：{dialog.FolderName}");
    }

    private async Task CheckoutAsync(CancellationToken token)
    {
        var values = dialogs.Ask("检出工作副本", "目标目录必须不存在或为空。完整检出会下载全部文件；稀疏检出可以之后按文件下载。",
            new InputField("仓库 URL", model.Session?.Url ?? model.Address),
            new InputField("本地目标目录", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SvnWorkingCopy")));
        if (values == null) return;
        var destination = Path.GetFullPath(values[1].Trim());
        if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new ArgumentException("检出目标必须不存在或为空。");
        var choice = dialogs.Choose("检出范围", "请选择首次下载范围。", "完整检出", "稀疏检出（只创建根目录）");
        if (choice < 0) return;
        model.SetProgressTarget($"{values[0].Trim()} → {destination}");
        model.AddLog(await model.Operations.CheckoutAsync(values[0].Trim(), destination, choice == 1, token));
        await model.OpenAsync(destination, true, token);
    }

    private async Task AssociateAsync(CancellationToken token)
    {
        if (model.Session == null) throw new InvalidOperationException("请先打开仓库地址。");
        var dialog = new OpenFolderDialog { Title = "选择与浏览根地址对应的工作副本（关联后保留当前目录）" };
        if (!dialogs.ChooseFolder(dialog)) return;
        await model.AssociateWorkingCopyAsync(dialog.FolderName, token);
    }

    private async Task BranchAsync(CancellationToken token)
    {
        var values = dialogs.Ask("创建分支 / 标签", "将服务器上的当前目录复制到目标 URL，此操作直接创建服务器提交。",
            new InputField("源 URL", CurrentUrl()), new InputField("目标 URL"), new InputField("提交说明（可选）", Multiline: true));
        if (values == null) return;
        if (values.Take(2).Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("源和目标 URL 不能为空。");
        if (!Confirm("创建服务器分支 / 标签", $"源：{values[0]}\n目标：{values[1]}\n\n确认在服务器创建？")) return;
        model.AddLog(await model.Operations.CopyBranchAsync(values[0], values[1], values[2], token));
        await model.RefreshCurrentDirectoryAsync(token);
    }

    private async Task SwitchAsync(CancellationToken token)
    {
        var local = CurrentLocal();
        var values = dialogs.Ask("切换分支", "切换当前目录的仓库地址。SVN 会尝试保留本地修改，产生冲突时需人工处理。",
            new InputField("目标分支 URL", CurrentUrl()));
        if (values == null) return;
        if (!Confirm("切换分支", $"将 {local} 切换到：\n{values[0]}\n\n是否继续？")) return;
        model.AddLog(await model.Operations.SwitchAsync(values[0].Trim(), local, token));
        await model.RefreshSwitchedDirectoryAsync(token);
    }

    private async Task MergeAsync(bool preview, CancellationToken token)
    {
        var local = CurrentLocal();
        var values = dialogs.Ask(preview ? "预览合并" : "合并", "将源分支尚未合并的变化应用到当前目录。建议先更新，并确保没有未提交修改；合并完成后仍需手动提交。",
            new InputField("源分支 URL"));
        if (values == null) return;
        if (!preview && !Confirm("确认合并", $"源：{values[0]}\n目标：{local}\n\n是否执行合并？")) return;
        var output = await model.Operations.MergeAsync(values[0].Trim(), local, preview, token);
        ShowOutput(preview ? "合并预览（未修改工作副本）" : "合并结果", output);
        if (!preview) await model.RefreshPathsAsync([local], token);
    }

    private void ShowHistory(IReadOnlyList<string> urls, CancellationToken token)
    {
        model.WaitForUserInput(() => new HistoryWindow(owner, string.Join("；", urls.Distinct(StringComparer.Ordinal)),
            (start, peg, readToken) => model.Operations.LogPageAsync(urls, start, peg, token: readToken), token).ShowDialog());
    }

    private async Task BatchRenameAsync(IReadOnlyList<BrowserEntry> selection, CancellationToken token)
    {
        var sources = selection.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new BatchRenameSource(Local(entry), entry.IsDirectory)).ToArray();
        var plan = model.WaitForUserInput(() =>
        {
            var window = new BatchRenameWindow(owner, sources,
                (rows, readToken) => Task.Run(() => model.Operations.PrepareBatchRenameAsync(rows, model.Session!.LocalRoot!, readToken), readToken), token);
            return window.ShowDialog() == true ? window.Plan : null;
        }, result => result == null);
        if (plan != null) await model.MoveLocalFilesAsync(plan, new FileMoveOptions(), token);
    }

    private async Task UpdateRevisionAsync(IReadOnlyList<BrowserEntry> entries, CancellationToken token)
    {
        var selectedRevision = PickRevision("更新到指定修订号", "选择或输入目标修订号。目录包含子目录。SVN 会尝试保留本地修改，冲突留待手动处理。更新不会创建服务器提交。",
            entries.Count == 0 ? [CurrentUrl()] : entries.Select(RevisionHistoryUrl).ToArray(), SnapshotRevision(), true, token);
        if (selectedRevision == null) return;
        var revision = selectedRevision.Value;
        var targets = entries.Count == 0 ? new[] { (Path: CurrentLocal(), Directory: true) }
            : entries.Select(entry => (Path: Local(entry), Directory: entry.IsDirectory)).ToArray();
        if (!Confirm("确认更新版本", $"将以下项目更新到 r{revision}：\n\n{string.Join("\n", targets.Select(target => target.Path))}\n\n此操作会改变本地工作文件。是否继续？")) return;
        await RunRevisionChangeAsync(targets.Select(target => target.Path), async () =>
        {
            foreach (var target in targets)
            {
                model.SetProgressTarget(target.Path);
                model.AddLog(await model.Operations.UpdateToRevisionAsync(target.Path, target.Directory, revision, token));
            }
        }, token);
    }

    private async Task ReverseMergeAsync(BrowserEntry? entry, CancellationToken token)
    {
        var path = entry == null ? CurrentLocal() : Local(entry);
        var selectedRevision = PickRevision("回滚指定提交", "选择或输入要撤销的单次提交修订号。反向合并生成本地修改，不会删除历史或自动提交。请先将目标更新到最新，并处理未提交修改。下一步会显示预览。",
            [entry == null ? CurrentUrl() : RevisionHistoryUrl(entry)], "", false, token);
        if (selectedRevision == null) return;
        var revision = selectedRevision.Value;
        model.SetProgressTarget(path);
        var preview = await model.Operations.ReverseMergeAsync(path, revision, true, token);
        if (!model.WaitForUserInput(() => new ReverseMergePreviewWindow(owner, path, revision, preview).ShowDialog() == true, result => !result)) return;
        token.ThrowIfCancellationRequested();
        await RunRevisionChangeAsync([path], async () => model.AddLog(await model.Operations.ReverseMergeAsync(path, revision, false, token)), token);
        dialogs.Inform("反向合并完成", "回滚结果已保存在本地工作副本。请检查差异及冲突，再使用“提交”上传回滚结果。");
    }

    private string RevisionHistoryUrl(BrowserEntry entry) => entry.LocalPath != null && model.Snapshot != null
        && model.Snapshot.LocalInfo.TryGetValue(entry.LocalPath, out var info) ? info.Url : entry.Url;

    private long? PickRevision(string title, string explanation, IReadOnlyList<string> historyUrls, string initialRevision, bool allowZero, CancellationToken token) =>
        model.WaitForUserInput<long?>(() =>
        {
            var scope = string.Join("；", historyUrls.Distinct(StringComparer.Ordinal));
            var picker = new RevisionPickerWindow(owner, title, explanation, scope, initialRevision, allowZero,
                (start, peg, readToken) => model.Operations.LogPageAsync(historyUrls, start, peg, token: readToken), token);
            return picker.ShowDialog() == true ? picker.SelectedRevision : null;
        }, result => result == null);

    private async Task RunRevisionChangeAsync(IEnumerable<string> paths, Func<Task> change, CancellationToken token)
    {
        try { await change(); }
        catch
        {
            // A failed merge/update can have applied part of its changes. Refresh if possible,
            // retaining the original error; cancellation must not start an uncancellable read.
            if (!token.IsCancellationRequested)
            {
                try { await model.RefreshPathsAsync(paths, token); }
                catch (Exception ex) { model.AddLog($"操作后的状态刷新失败，请手动刷新：{ex.Message}"); }
            }
            throw;
        }
        await model.RefreshPathsAsync(paths, token);
    }

    private string CurrentLocal() => RepositoryService.GetLocalPath(model.Session!, model.CurrentRelative)
        ?? throw new InvalidOperationException("此操作需要本地工作副本，请先关联或检出。");
    private string CurrentUrl()
    {
        var path = RepositoryService.GetLocalPath(model.Session!, model.CurrentRelative);
        return path != null && model.Snapshot!.LocalInfo.TryGetValue(path, out var info) ? info.Url : model.CurrentPath;
    }
    private string SnapshotRevision() => model.CurrentDirectorySnapshot?.Revision.ToString() ?? "HEAD";
    private static string Local(BrowserEntry entry) => entry.LocalPath ?? throw new InvalidOperationException("此操作需要本地工作副本，请先关联或检出。");
    private static BrowserEntry One(IReadOnlyList<BrowserEntry> entries) => entries.Count == 1 ? entries[0] : throw new InvalidOperationException("请选择一个项目。");
    private static IReadOnlyList<BrowserEntry> Selected(IReadOnlyList<BrowserEntry> entries) => entries.Count > 0 ? entries : throw new InvalidOperationException("请先选择文件或目录。");
    private static BrowserEntry FileEntry(BrowserEntry entry) => !entry.IsDirectory ? entry : throw new InvalidOperationException("请选择文件。");
    private bool Confirm(string title, string message) => dialogs.Confirm(title, message);
    private void ShowOutput(string title, string text) => new OutputWindow(owner, title, string.IsNullOrWhiteSpace(text) ? "没有变化。" : text).Show();
    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
}
