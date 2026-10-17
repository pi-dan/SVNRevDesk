using System.Collections.ObjectModel;

namespace SVNRevDesk.Desktop.ViewModels;

public sealed class DirectoryNode : ObservableObject
{
    private string summary;
    private bool expanded;
    private bool selected;
    public string Name { get; }
    public string RelativePath { get; }
    public bool IsPlaceholder { get; }
    public bool Loaded { get; set; }
    public ObservableCollection<DirectoryNode> Children { get; } = [];
    public string Summary
    {
        get => summary;
        set
        {
            if (!SetProperty(ref summary, value)) return;
            Notify(nameof(DisplaySummary));
            Notify(nameof(IsIgnored));
        }
    }
    public bool IsIgnored => Summary == "已忽略" || Summary.StartsWith("已忽略 · ", StringComparison.Ordinal);
    public string DisplaySummary
    {
        get
        {
            var parts = Summary.Split(" · ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return Summary;
            // The first state describes this directory. Matching child states are already implied.
            var ownState = parts[0];
            return string.Join(" · ", parts.Where((part, index) => index == 0
                || part != ownState && !part.StartsWith(ownState + " ", StringComparison.Ordinal)));
        }
    }
    public bool IsExpanded { get => expanded; set => SetProperty(ref expanded, value); }
    public bool IsSelected { get => selected; set => SetProperty(ref selected, value); }

    public DirectoryNode(string name, string relativePath, string summary = "未检查", bool placeholder = false)
    {
        Name = name;
        RelativePath = relativePath;
        this.summary = summary;
        IsPlaceholder = placeholder;
        if (!placeholder) Children.Add(new DirectoryNode("加载中…", "", "", true));
    }
}
