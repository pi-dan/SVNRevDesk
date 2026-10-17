using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace SVNRevDesk.Desktop.Services;

// CF_HDROP and Preferred DropEffect are the same formats used by Windows Explorer.
public static class FileClipboard
{
    private const string OriginalPathsFormat = "SVNRevDesk.FileOrigins";
    private const string CutPathsFormat = "SVNRevDesk.CutPaths";
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    public static uint SequenceNumber => GetClipboardSequenceNumber();
    public static bool ContainsFiles()
    {
        try { return ReadFiles().Count > 0; }
        catch (ExternalException) { return false; }
    }

    public static IReadOnlyList<string> ReadFiles() => ReadFiles(Clipboard.GetDataObject());

    public static IReadOnlyList<string> ReadFiles(IDataObject? data)
    {
        if (data?.GetData(CutPathsFormat) is string json)
        {
            try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
            catch (JsonException) { return []; }
        }
        return data?.GetDataPresent(DataFormats.FileDrop) == true
            && data.GetData(DataFormats.FileDrop) is System.Collections.IEnumerable paths ? paths.Cast<string>().ToArray() : [];
    }

    public static bool IsMove(IDataObject? data)
    {
        if (data?.GetDataPresent(CutPathsFormat) == true) return true;
        var value = data?.GetData("Preferred DropEffect");
        var bytes = value switch { MemoryStream stream => stream.ToArray(), byte[] array => array, _ => [] };
        return bytes.Length >= 4 && (BitConverter.ToUInt32(bytes, 0) & 3) == 2;
    }

    public static DataObject CreateCutData(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        // Keep SVN cuts inside this client: Explorer cannot schedule an SVN move or preserve its history.
        data.SetData(CutPathsFormat, JsonSerializer.Serialize(paths));
        data.SetData("Preferred DropEffect", new MemoryStream([2, 0, 0, 0]));
        return data;
    }

    public static IReadOnlyDictionary<string, string> ReadOriginalPaths(IDataObject? data)
    {
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data?.GetData(OriginalPathsFormat) is not string json) return origins;
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json);
            var paths = ReadFiles(data);
            if (values == null || values.Length != paths.Count) return origins;
            for (var index = 0; index < paths.Count; index++)
                if (!string.IsNullOrEmpty(values[index]) && Path.IsPathFullyQualified(values[index])) origins[paths[index]] = values[index];
        }
        catch (JsonException) { /* Clipboard contents from other applications need no origins. */ }
        return origins;
    }

    public static DataObject CreateData(IReadOnlyList<string> paths, IReadOnlyList<string>? originalPaths = null, bool move = false)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        data.SetData("Preferred DropEffect", new MemoryStream([move ? (byte)2 : (byte)1, 0, 0, 0]));
        if (originalPaths != null) data.SetData(OriginalPathsFormat, JsonSerializer.Serialize(originalPaths));
        return data;
    }

    public static async Task WriteFilesAsync(IReadOnlyList<string> paths, IReadOnlyList<string>? originalPaths = null)
        => await WriteDataAsync(CreateData(paths, originalPaths));

    public static Task WriteCutFilesAsync(IReadOnlyList<string> paths) => WriteDataAsync(CreateCutData(paths));

    private static async Task WriteDataAsync(DataObject data)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(100); }
        }
    }

    public static async Task CompleteMoveAsync(IDataObject original, uint sequence, IReadOnlyList<string> remaining)
    {
        for (var attempt = 0; ; attempt++)
        {
            // The user may have copied something else during a large move. Never replace that clipboard.
            if (SequenceNumber != sequence) return;
            try
            {
                if (remaining.Count == 0) Clipboard.Clear();
                else Clipboard.SetDataObject(original.GetDataPresent(CutPathsFormat)
                    ? CreateCutData(remaining) : CreateData(remaining, move: true), copy: true);
                return;
            }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(100); }
        }
    }
}
