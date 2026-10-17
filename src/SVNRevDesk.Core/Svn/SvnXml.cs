using System.Globalization;
using System.Xml.Linq;
using SVNRevDesk.Core.Models;

namespace SVNRevDesk.Core.Svn;

public static class SvnXml
{
    // SVN translates svn:* text properties to native encoding/EOLs even in XML output;
    // non-XML-safe native bytes are emitted as Base64 (for example Chinese on Windows).
    public static string ReadTextProperty(XElement property) =>
        ((string?)property.Attribute("encoding") == "base64"
            ? SvnRunner.TargetFileEncoding.GetString(Convert.FromBase64String(property.Value))
            : property.Value).ReplaceLineEndings("\n");

    public static IReadOnlyList<SvnInfo> ReadInfo(string xml) => XDocument.Parse(xml)
        .Descendants("entry").Select(entry =>
        {
            var commit = entry.Element("commit");
            return new SvnInfo(
                Text(entry.Attribute("path")), Text(entry.Element("url")),
                Text(entry.Element("repository")?.Element("root")),
                Text(entry.Element("repository")?.Element("uuid")),
                Text(entry.Attribute("kind")) == "dir", Number(entry.Attribute("revision")) ?? 0,
                Number(commit?.Attribute("revision")), Text(commit?.Element("author")),
                entry.Element("wc-info")?.Element("wcroot-abspath")?.Value);
        }).ToList();

    public static IReadOnlyList<ServerEntry> ReadList(string xml) => XDocument.Parse(xml)
        .Descendants("entry").Select(entry =>
        {
            var commit = entry.Element("commit");
            return new ServerEntry(Text(entry.Element("name")), Text(entry.Attribute("kind")) == "dir",
                Number(entry.Element("size")), Number(commit?.Attribute("revision")),
                Text(commit?.Element("author")), Date(commit?.Element("date")), ReadLock(entry.Element("lock")));
        }).ToList();

    public static IReadOnlyList<WorkingStatus> ReadStatus(string xml) => XDocument.Parse(xml)
        .Descendants("entry").Select(entry =>
        {
            var local = entry.Element("wc-status");
            var remote = entry.Element("repos-status");
            return new WorkingStatus(Path.GetFullPath(Text(entry.Attribute("path"))),
                Text(local?.Attribute("item")), Text(local?.Attribute("props")),
                Text(remote?.Attribute("item")), Text(remote?.Attribute("props")),
                Number(local?.Attribute("revision")), Number(local?.Element("commit")?.Attribute("revision")),
                Text(local?.Attribute("tree-conflicted")) == "true", Text(local?.Attribute("switched")) == "true",
                ReadLock(local?.Element("lock")), ReadLock(remote?.Element("lock")))
            {
                MovedFrom = LocalPath(local?.Attribute("moved-from"), Text(entry.Parent?.Attribute("path"))),
                MovedTo = LocalPath(local?.Attribute("moved-to"), Text(entry.Parent?.Attribute("path"))),
                Copied = Text(local?.Attribute("copied")) == "true"
            };
        }).ToList();

    public static IReadOnlyList<HistoryEntry> ReadLog(string xml) => XDocument.Parse(xml)
        .Descendants("logentry").Select(entry => new HistoryEntry(Number(entry.Attribute("revision")) ?? 0,
            Text(entry.Element("author")), Date(entry.Element("date")), Text(entry.Element("msg")))).ToList();

    private static SvnLock? ReadLock(XElement? element) => element == null ? null
        : new SvnLock(Text(element.Element("owner")), Text(element.Element("comment")), Date(element.Element("created")));
    private static string? LocalPath(XAttribute? attribute, string target)
    {
        if (string.IsNullOrEmpty(attribute?.Value)) return null;
        // SVN expresses move counterparts relative to the <target>, even for a single-file target.
        return Path.GetFullPath(Path.Combine(Path.GetFullPath(target.Length > 0 ? target : "."), attribute.Value));
    }
    private static string Text(XObject? value) => value switch { XElement e => e.Value, XAttribute a => a.Value, _ => "" };
    private static long? Number(XObject? value) => long.TryParse(Text(value), out var number) && number >= 0 ? number : null;
    private static DateTimeOffset? Date(XObject? value) => DateTimeOffset.TryParse(Text(value), CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}
