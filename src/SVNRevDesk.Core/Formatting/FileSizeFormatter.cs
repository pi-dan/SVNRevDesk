namespace SVNRevDesk.Core.Formatting;

public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    // Match the familiar Windows file-size convention: each unit is 1024 of the previous one.
    public static string Format(long? bytes)
    {
        if (bytes is null or < 0) return "—";
        var size = (double)bytes.Value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {Units[unit]}";
    }
}
