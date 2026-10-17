using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Desktop.Services;

namespace SVNRevDesk.Desktop.Converters;

public sealed class FileIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value switch
        {
            BrowserEntry entry => FileTypeIcons.GetKind(entry.Name, entry.IsDirectory),
            CommitChange change => FileTypeIcons.GetKind(System.IO.Path.GetFileName(change.Path), change.IsDirectory),
            _ => (FileTypeKind?)null
        };
        if (kind == null) return DependencyProperty.UnsetValue;
        if (parameter is "label") return FileTypeIcons.GetLabel(kind.Value);

        return Application.Current.FindResource("FileIcon" + kind.Value);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("文件图标仅用于展示。");
}
