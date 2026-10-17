using System.Globalization;
using System.Windows.Data;

namespace SVNRevDesk.Desktop.Converters;

public sealed class ToolTipTextConverter : IValueConverter
{
    // WPF still opens a tooltip for an empty string. Null prevents the popup from being created.
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("提示内容仅用于展示。");
}
