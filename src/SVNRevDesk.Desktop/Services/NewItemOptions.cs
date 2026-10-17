using SVNRevDesk.Core.Services;

namespace SVNRevDesk.Desktop.Services;

public sealed record NewItemOption(string Label, string DefaultName, NewItemKind Kind, string InitialText = "")
{
    public NewItemRequest Request(string name) => new(name, Kind, InitialText);
    public override string ToString() => Label;
}

public static class NewItemOptions
{
    public static IReadOnlyList<NewItemOption> Standard { get; } =
    [
        new("文件夹", "新建文件夹", NewItemKind.Folder),
        new("文本文档 (.txt)", "新建文本文档.txt", NewItemKind.Text),
        new("Markdown (.md)", "新建文档.md", NewItemKind.Text),
        new("JSON 文件 (.json)", "新建配置.json", NewItemKind.Text, "{}\n"),
        new("XML 文件 (.xml)", "新建配置.xml", NewItemKind.Text, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root />\n"),
        new("Word 文档 (.docx)", "新建Word文档.docx", NewItemKind.WordDocument),
        new("Excel 工作簿 (.xlsx)", "新建Excel工作簿.xlsx", NewItemKind.Spreadsheet),
        new("PowerPoint 演示文稿 (.pptx)", "新建PowerPoint演示文稿.pptx", NewItemKind.Presentation),
        new("其他文件…", "新建文件", NewItemKind.EmptyFile)
    ];
}
