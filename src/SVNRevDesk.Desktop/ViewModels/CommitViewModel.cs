using System.ComponentModel;
using System.Windows.Data;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed class CommitItem(CommitChange change) : ObservableObject
{
    private bool selected = change.CanCommit && !change.NeedsAddition;
    public CommitChange Change { get; } = change;
    public bool IsChecked { get => selected; set => SetProperty(ref selected, value && Change.CanCommit); }
}

public sealed class CommitViewModel : ObservableObject
{
    private string message = "";
    private bool updatingSelection;
    private bool showUnversioned = true;
    public string LocalRoot { get; }
    public string Destination { get; }
    public IReadOnlyList<CommitItem> Items { get; }
    public ICollectionView ItemsView { get; }
    public string Message { get => message; set => SetProperty(ref message, value); }
    public IReadOnlyList<CommitChange> SelectedChanges => Items.Where(item => item.IsChecked).Select(item => item.Change).ToList();
    public int SelectedCount => Items.Count(item => item.IsChecked);
    public string SelectionText => $"已勾选 {SelectedCount} 项 · 自动添加 {Items.Count(item => item.IsChecked && item.Change.NeedsAddition)} 项 · 共 {Items.Count} 项变更"
        + (HasMissingDependencies ? " · 需同时勾选关联的移动项目或新增父目录" : "");
    public bool CanSubmit => SelectedCount > 0 && !HasMissingDependencies;
    private bool HasMissingDependencies
    {
        get
        {
            var paths = SelectedChanges.Select(change => change.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return SelectedChanges.Any(change => change.RequiredChanges.Any(path => !paths.Contains(path)));
        }
    }
    public bool ShowUnversioned
    {
        get => showUnversioned;
        set
        {
            if (!SetProperty(ref showUnversioned, value)) return;
            // Hidden new files must not be added behind the user's back.
            if (!value)
                foreach (var item in Items.Where(item => item.Change.NeedsAddition)) item.IsChecked = false;
            ItemsView.Refresh();
            NotifySelection();
        }
    }

    public CommitViewModel(string localRoot, IReadOnlyList<CommitChange> changes, string? repositoryUrl = null)
    {
        LocalRoot = localRoot;
        Destination = repositoryUrl ?? localRoot;
        Items = changes.Select(change => new CommitItem(change)).ToList();
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = value => value is CommitItem item && (ShowUnversioned || !item.Change.NeedsAddition);
        foreach (var item in Items) item.PropertyChanged += Item_PropertyChanged;
    }

    public void SelectAll(bool selected) => SelectItems(item => selected);
    public void SelectVersioned() => SelectItems(item => !item.Change.NeedsAddition);
    public void SelectUnversioned()
    {
        ShowUnversioned = true;
        SelectItems(item => item.Change.NeedsAddition);
    }

    private void SelectItems(Func<CommitItem, bool> matches)
    {
        updatingSelection = true;
        try
        {
            foreach (var item in Items)
                item.IsChecked = item.Change.CanCommit && (ShowUnversioned || !item.Change.NeedsAddition) && matches(item);
            SelectRequiredChanges();
        }
        finally { updatingSelection = false; }
        NotifySelection();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (updatingSelection || e.PropertyName != nameof(CommitItem.IsChecked) || sender is not CommitItem changed) return;
        updatingSelection = true;
        try
        {
            if (changed.IsChecked)
            {
                SelectRequiredChanges();
            }
            else
            {
                bool removed;
                do
                {
                    removed = false;
                    var uncheckedPaths = Items.Where(item => !item.IsChecked).Select(item => item.Change.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var dependent in Items.Where(item => item.IsChecked && item.Change.RequiredChanges.Any(uncheckedPaths.Contains)))
                    { dependent.IsChecked = false; removed = true; }
                } while (removed);
            }
        }
        finally { updatingSelection = false; }
        NotifySelection();
    }

    private void SelectRequiredChanges()
    {
        bool added;
        do
        {
            added = false;
            var required = SelectedChanges.SelectMany(change => change.RequiredChanges).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items.Where(item => !item.IsChecked && item.Change.CanCommit && required.Contains(item.Change.Path)))
            { item.IsChecked = true; added = true; }
        } while (added);
    }

    private void NotifySelection()
    {
        Notify(nameof(SelectedCount)); Notify(nameof(SelectionText)); Notify(nameof(CanSubmit));
    }
}
