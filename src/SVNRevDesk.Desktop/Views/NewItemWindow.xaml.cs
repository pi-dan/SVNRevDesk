using System.IO;
using System.Windows;
using System.Windows.Controls;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.Views;

public partial class NewItemWindow : Window
{
    public NewItemOption Option { get; }
    public string ItemName => NameBox.Text;
    public bool AddToSvn => AddToSvnBox.IsChecked == true;

    public NewItemWindow(Window? owner, string directory, NewItemOption option)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        Option = option;
        Title = "新建" + (option.Kind == NewItemKind.Folder ? "文件夹" : "文件");
        TargetPath.Text = directory;
        TargetPath.ToolTip = directory;
        TypeDescription.Text = option.Kind == NewItemKind.EmptyFile ? OtherFileDescription : option.Label;
        NameBox.Text = option.DefaultName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.Select(0, option.Kind == NewItemKind.Folder ? NameBox.Text.Length : Path.GetFileNameWithoutExtension(NameBox.Text).Length);
        };
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ValidationText == null || CreateButton == null) return;
        try
        {
            if (Option == null) LocalItemCreator.ValidateName(NameBox.Text);
            else LocalItemCreator.ValidateRequest(Option.Request(NameBox.Text));
            ValidationText.Text = "";
            CreateButton.IsEnabled = true;
        }
        catch (ArgumentException ex) { ValidationText.Text = ex.Message; CreateButton.IsEnabled = false; }
        if (Option?.Kind == NewItemKind.EmptyFile)
            TypeDescription.Text = OfficeDocumentCreator.GetKindFromName(NameBox.Text) is { } kind
                ? "将生成有效的空白 Office 文档：" + OfficeDocumentCreator.GetExtension(kind) : OtherFileDescription;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (CreateButton.IsEnabled) DialogResult = true;
    }

    private const string OtherFileDescription = "请输入完整名称和后缀；docx / xlsx / pptx 自动生成空白文档，其他后缀创建空文件。";
}
