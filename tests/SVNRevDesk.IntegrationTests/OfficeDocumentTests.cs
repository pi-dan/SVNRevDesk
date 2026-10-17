using System.IO.Compression;
using System.Xml.Linq;
using SVNRevDesk.Core.Services;

internal static class OfficeDocumentTests
{
    public static async Task RunAsync(string fixture, Action<bool, string> assert)
    {
        Directory.CreateDirectory(fixture);
        var formats = new[]
        {
            (NewItemKind.WordDocument, "空白.docx", "word/document.xml"),
            (NewItemKind.Spreadsheet, "空白.xlsx", "xl/workbook.xml"),
            (NewItemKind.Presentation, "空白.pptx", "ppt/presentation.xml")
        };
        foreach (var (kind, name, mainPart) in formats)
        {
            var path = await LocalItemCreator.CreateAsync(fixture, fixture, new(name, kind), CancellationToken.None);
            using var package = ZipFile.OpenRead(path);
            assert(package.GetEntry(mainPart) != null && package.GetEntry("[Content_Types].xml") != null
                && package.GetEntry("_rels/.rels") != null, $"Code generates a complete {Path.GetExtension(name)} package without templates");
            var readable = true;
            foreach (var entry in package.Entries)
            {
                using var content = entry.Open();
                var xml = XDocument.Load(content);
                if (!entry.FullName.EndsWith(".rels", StringComparison.Ordinal)) continue;
                var sourceDirectory = entry.FullName == "_rels/.rels" ? ""
                    : entry.FullName[..entry.FullName.LastIndexOf("/_rels/", StringComparison.Ordinal)] + "/";
                foreach (var relationship in xml.Root!.Elements())
                {
                    var target = (string)relationship.Attribute("Target")!;
                    var resolved = new Uri(new Uri("https://package.invalid/" + sourceDirectory), target).AbsolutePath.TrimStart('/');
                    readable &= package.GetEntry(resolved) != null;
                }
            }
            assert(readable, $"All generated {Path.GetExtension(name)} XML is readable and every package relationship resolves");
            var inferred = await LocalItemCreator.CreateAsync(fixture, fixture, new("后缀识别" + Path.GetExtension(name).ToUpperInvariant(), NewItemKind.EmptyFile), CancellationToken.None);
            using var inferredPackage = ZipFile.OpenRead(inferred);
            assert(inferredPackage.GetEntry(mainPart) != null, $"An arbitrary filename with {Path.GetExtension(name)} automatically creates that Office format");
        }
        using (var word = ZipFile.OpenRead(Path.Combine(fixture, "空白.docx")))
        {
            using var content = word.GetEntry("word/document.xml")!.Open();
            var document = XDocument.Load(content);
            XNamespace ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            assert(document.Descendants(ns + "p").Count() == 1 && !document.Descendants(ns + "t").Any(),
                "Generated Word starts with one empty paragraph and page settings");
        }
        using (var workbook = ZipFile.OpenRead(Path.Combine(fixture, "空白.xlsx")))
        {
            using var content = workbook.GetEntry("xl/workbook.xml")!.Open();
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            assert(XDocument.Load(content).Descendants(ns + "sheet").Single().Attribute("name")?.Value == "Sheet1",
                "Generated Excel starts with one sheet and default cell styles");
        }
        using (var presentation = ZipFile.OpenRead(Path.Combine(fixture, "空白.pptx")))
        {
            using var content = presentation.GetEntry("ppt/presentation.xml")!.Open();
            XNamespace ns = "http://schemas.openxmlformats.org/presentationml/2006/main";
            assert(XDocument.Load(content).Descendants(ns + "sldId").Count() == 1
                && presentation.GetEntry("ppt/theme/theme1.xml") != null,
                "Generated PowerPoint starts with a blank slide, layout, master and theme");
        }
        using var cancellation = new CancellationTokenSource();
        var mismatched = false;
        try { await LocalItemCreator.CreateAsync(fixture, fixture, new("错误后缀.txt", NewItemKind.WordDocument), CancellationToken.None); }
        catch (ArgumentException) { mismatched = true; }
        assert(mismatched && !File.Exists(Path.Combine(fixture, "错误后缀.txt")), "Selected Office types reject mismatched filename extensions before creating files");
        cancellation.Cancel();
        var cancelled = false;
        try { await LocalItemCreator.CreateAsync(fixture, fixture, new("取消.docx", NewItemKind.WordDocument), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        assert(cancelled && !File.Exists(Path.Combine(fixture, "取消.docx"))
            && !Directory.EnumerateFileSystemEntries(fixture).Any(path => Path.GetFileName(path).StartsWith(".svnclient-new-", StringComparison.Ordinal)),
            "Cancelling Office creation leaves no destination or staging file");
    }
}
