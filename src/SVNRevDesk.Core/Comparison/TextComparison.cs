using System.Security.Cryptography;
using System.Text;

namespace SVNRevDesk.Core.Comparison;

public sealed record DiffLine(int? LeftNumber, string LeftText, int? RightNumber, string RightText, bool Changed);
public sealed record ComparisonResult(bool IsBinary, bool Identical, string Summary, IReadOnlyList<DiffLine> Lines);

public static class TextComparison
{
    private const int MaximumTextBytes = 5 * 1024 * 1024;
    private const long MaximumMatrixCells = 4_000_000;

    public static ComparisonResult CompareFiles(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        var same = leftInfo.Length == rightInfo.Length && Hash(leftPath).SequenceEqual(Hash(rightPath));
        if (leftInfo.Length > MaximumTextBytes || rightInfo.Length > MaximumTextBytes)
            return new ComparisonResult(true, same, "文件较大，请使用外部比较工具。" + (same ? " 内容完全一致。" : " 内容不同。"), []);

        var left = ReadText(leftPath);
        var right = ReadText(rightPath);
        if (left == null || right == null)
            return new ComparisonResult(true, same, same ? "文件字节完全一致。" : "二进制文件或不支持的文本编码，内容不同，请使用外部比较工具。", []);
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);
        if ((long)(leftLines.Length + 1) * (rightLines.Length + 1) > MaximumMatrixCells)
            return new ComparisonResult(false, same, "行数较多，逐行对齐展示；精确差异请使用外部工具。", AlignByPosition(leftLines, rightLines));
        return new ComparisonResult(false, same, same ? "文件内容完全一致。" :
            left.Replace("\r\n", "\n") == right.Replace("\r\n", "\n") ? "仅换行符不同。" : "高亮行表示新增、删除或修改。",
            CompareLines(leftLines, rightLines));
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static string? ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Contains((byte)0)) return null;
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static IReadOnlyList<DiffLine> CompareLines(string[] left, string[] right)
    {
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
            for (var j = right.Length - 1; j >= 0; j--)
                lengths[i, j] = left[i] == right[j] ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var rows = new List<DiffLine>();
        var l = 0;
        var r = 0;
        while (l < left.Length || r < right.Length)
        {
            if (l < left.Length && r < right.Length && left[l] == right[r])
            {
                rows.Add(new DiffLine(l + 1, left[l], r + 1, right[r], false));
                l++; r++;
                continue;
            }
            var removed = new List<(int Number, string Text)>();
            var added = new List<(int Number, string Text)>();
            while ((l < left.Length || r < right.Length) && !(l < left.Length && r < right.Length && left[l] == right[r]))
            {
                if (l < left.Length && (r == right.Length || lengths[l + 1, r] >= lengths[l, r + 1]))
                    removed.Add((++l, left[l - 1]));
                else added.Add((++r, right[r - 1]));
            }
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
                rows.Add(new DiffLine(i < removed.Count ? removed[i].Number : null, i < removed.Count ? removed[i].Text : "",
                    i < added.Count ? added[i].Number : null, i < added.Count ? added[i].Text : "", true));
        }
        return rows;
    }

    private static IReadOnlyList<DiffLine> AlignByPosition(string[] left, string[] right) =>
        Enumerable.Range(0, Math.Max(left.Length, right.Length)).Select(i =>
            new DiffLine(i < left.Length ? i + 1 : null, i < left.Length ? left[i] : "",
                i < right.Length ? i + 1 : null, i < right.Length ? right[i] : "",
                i >= left.Length || i >= right.Length || left[i] != right[i])).ToList();
}
