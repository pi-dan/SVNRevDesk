using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace SVNRevDesk.Core.Services;

// Office Open XML files are ZIP packages. These minimal parts create editable empty documents
// without Office automation, external libraries, or a template file on disk.
public static class OfficeDocumentCreator
{
    private sealed record DocumentPart(string Path, string ContentType, string Xml);
    private const string OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PresentationNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    public static NewItemKind? GetKindFromName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".docx" => NewItemKind.WordDocument,
        ".xlsx" => NewItemKind.Spreadsheet,
        ".pptx" => NewItemKind.Presentation,
        _ => null
    };

    public static string? GetExtension(NewItemKind kind) => kind switch
    {
        NewItemKind.WordDocument => ".docx",
        NewItemKind.Spreadsheet => ".xlsx",
        NewItemKind.Presentation => ".pptx",
        _ => null
    };

    public static async Task WriteAsync(Stream output, NewItemKind kind, CancellationToken token)
    {
        var parts = kind switch
        {
            NewItemKind.WordDocument => WordParts(),
            NewItemKind.Spreadsheet => SpreadsheetParts(),
            NewItemKind.Presentation => PresentationParts(),
            _ => throw new ArgumentException("请选择 Word、Excel 或 PowerPoint 文档类型。", nameof(kind))
        };
        token.ThrowIfCancellationRequested();
        using var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WritePartAsync(package, "[Content_Types].xml", ContentTypes(parts), token);
        await WritePartAsync(package, "_rels/.rels", Relationships(("rId1", "officeDocument", parts[0].Path)), token);
        foreach (var part in parts) await WritePartAsync(package, part.Path, part.Xml, token);
    }

    private static async Task WritePartAsync(ZipArchive package, string path, string xml, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = package.CreateEntry(path, CompressionLevel.Optimal);
        await using var content = entry.Open();
        await content.WriteAsync(Encoding.UTF8.GetBytes(xml), token);
    }

    private static string ContentTypes(IReadOnlyList<DocumentPart> parts)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        return new XElement(ns + "Types",
            new XElement(ns + "Default", new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            parts.Where(part => !part.Path.EndsWith(".rels", StringComparison.Ordinal)).Select(part =>
                new XElement(ns + "Override", new XAttribute("PartName", "/" + part.Path), new XAttribute("ContentType", part.ContentType))))
            .ToString();
    }

    private static string Relationships(params (string Id, string Type, string Target)[] relationships)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        return new XElement(ns + "Relationships", relationships.Select(relationship =>
            new XElement(ns + "Relationship", new XAttribute("Id", relationship.Id),
                new XAttribute("Type", OfficeRelationships + relationship.Type), new XAttribute("Target", relationship.Target)))).ToString();
    }

    private static IReadOnlyList<DocumentPart> WordParts() =>
    [
        new("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", $$"""
            <w:document xmlns:w="{{WordNamespace}}">
              <w:body>
                <w:p />
                <w:sectPr>
                  <w:pgSz w:w="11906" w:h="16838" />
                  <w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0" />
                </w:sectPr>
              </w:body>
            </w:document>
            """)
    ];

    private static IReadOnlyList<DocumentPart> SpreadsheetParts() =>
    [
        new("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", $$"""
            <workbook xmlns="{{SpreadsheetNamespace}}" xmlns:r="{{OfficeRelationships.TrimEnd('/')}}">
              <bookViews><workbookView /></bookViews>
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1" /></sheets>
            </workbook>
            """),
        new("xl/_rels/workbook.xml.rels", "", Relationships(
            ("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml"))),
        new("xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", $$"""
            <worksheet xmlns="{{SpreadsheetNamespace}}">
              <sheetViews><sheetView workbookViewId="0" /></sheetViews>
              <sheetFormatPr defaultRowHeight="15" />
              <sheetData />
            </worksheet>
            """),
        new("xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml", $$"""
            <styleSheet xmlns="{{SpreadsheetNamespace}}">
              <fonts count="1"><font><sz val="11" /><name val="Calibri" /><family val="2" /></font></fonts>
              <fills count="2"><fill><patternFill patternType="none" /></fill><fill><patternFill patternType="gray125" /></fill></fills>
              <borders count="1"><border><left /><right /><top /><bottom /><diagonal /></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" /></cellStyleXfs>
              <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" /></cellXfs>
              <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0" /></cellStyles>
            </styleSheet>
            """)
    ];

    private static IReadOnlyList<DocumentPart> PresentationParts() =>
    [
        new("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml", $$"""
            <p:presentation xmlns:p="{{PresentationNamespace}}" xmlns:r="{{OfficeRelationships.TrimEnd('/')}}">
              <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1" /></p:sldMasterIdLst>
              <p:sldIdLst><p:sldId id="256" r:id="rId2" /></p:sldIdLst>
              <p:sldSz cx="12192000" cy="6858000" />
              <p:notesSz cx="6858000" cy="9144000" />
            </p:presentation>
            """),
        new("ppt/_rels/presentation.xml.rels", "", Relationships(
            ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "slide", "slides/slide1.xml"))),
        new("ppt/slides/slide1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml", $$"""
            <p:sld xmlns:p="{{PresentationNamespace}}" xmlns:a="{{DrawingNamespace}}">
              <p:cSld>{{EmptyShapeTree}}</p:cSld>
              <p:clrMapOvr><a:masterClrMapping /></p:clrMapOvr>
            </p:sld>
            """),
        new("ppt/slides/_rels/slide1.xml.rels", "", Relationships(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"))),
        new("ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml", $$"""
            <p:sldLayout xmlns:p="{{PresentationNamespace}}" xmlns:a="{{DrawingNamespace}}" type="blank" preserve="1">
              <p:cSld name="空白">{{EmptyShapeTree}}</p:cSld>
              <p:clrMapOvr><a:masterClrMapping /></p:clrMapOvr>
            </p:sldLayout>
            """),
        new("ppt/slideLayouts/_rels/slideLayout1.xml.rels", "", Relationships(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"))),
        new("ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml", $$"""
            <p:sldMaster xmlns:p="{{PresentationNamespace}}" xmlns:a="{{DrawingNamespace}}" xmlns:r="{{OfficeRelationships.TrimEnd('/')}}">
              <p:cSld>{{EmptyShapeTree}}</p:cSld>
              <p:clrMap accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6"
                        bg1="lt1" bg2="lt2" folHlink="folHlink" hlink="hlink" tx1="dk1" tx2="dk2" />
              <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1" /></p:sldLayoutIdLst>
              <p:txStyles><p:titleStyle /><p:bodyStyle /><p:otherStyle /></p:txStyles>
            </p:sldMaster>
            """),
        new("ppt/slideMasters/_rels/slideMaster1.xml.rels", "", Relationships(
            ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "theme", "../theme/theme1.xml"))),
        new("ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml", PresentationTheme)
    ];

    // Slides, layouts and masters all start with the same empty group shape.
    private const string EmptyShapeTree = """
        <p:spTree>
          <p:nvGrpSpPr><p:cNvPr id="1" name="" /><p:cNvGrpSpPr /><p:nvPr /></p:nvGrpSpPr>
          <p:grpSpPr><a:xfrm><a:off x="0" y="0" /><a:ext cx="0" cy="0" /><a:chOff x="0" y="0" /><a:chExt cx="0" cy="0" /></a:xfrm></p:grpSpPr>
        </p:spTree>
        """;

    private static string PresentationTheme => $$"""
        <a:theme xmlns:a="{{DrawingNamespace}}" name="Office">
          <a:themeElements>
            <a:clrScheme name="Office">
              <a:dk1><a:sysClr val="windowText" lastClr="000000" /></a:dk1>
              <a:lt1><a:sysClr val="window" lastClr="FFFFFF" /></a:lt1>
              <a:dk2><a:srgbClr val="44546A" /></a:dk2><a:lt2><a:srgbClr val="E7E6E6" /></a:lt2>
              <a:accent1><a:srgbClr val="4472C4" /></a:accent1><a:accent2><a:srgbClr val="ED7D31" /></a:accent2>
              <a:accent3><a:srgbClr val="A5A5A5" /></a:accent3><a:accent4><a:srgbClr val="FFC000" /></a:accent4>
              <a:accent5><a:srgbClr val="5B9BD5" /></a:accent5><a:accent6><a:srgbClr val="70AD47" /></a:accent6>
              <a:hlink><a:srgbClr val="0563C1" /></a:hlink><a:folHlink><a:srgbClr val="954F72" /></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Office">
              <a:majorFont><a:latin typeface="Calibri" /><a:ea typeface="微软雅黑" /><a:cs typeface="Arial" /></a:majorFont>
              <a:minorFont><a:latin typeface="Calibri" /><a:ea typeface="微软雅黑" /><a:cs typeface="Arial" /></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Office">
              <a:fillStyleLst>
                {{SolidFill}}{{SolidFill}}{{SolidFill}}
              </a:fillStyleLst>
              <a:lnStyleLst>
                <a:ln w="9525">{{SolidFill}}<a:prstDash val="solid" /></a:ln>
                <a:ln w="25400">{{SolidFill}}<a:prstDash val="solid" /></a:ln>
                <a:ln w="38100">{{SolidFill}}<a:prstDash val="solid" /></a:ln>
              </a:lnStyleLst>
              <a:effectStyleLst>
                <a:effectStyle><a:effectLst /></a:effectStyle><a:effectStyle><a:effectLst /></a:effectStyle><a:effectStyle><a:effectLst /></a:effectStyle>
              </a:effectStyleLst>
              <a:bgFillStyleLst>
                {{SolidFill}}{{SolidFill}}{{SolidFill}}
              </a:bgFillStyleLst>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;

    private const string SolidFill = "<a:solidFill><a:schemeClr val=\"phClr\" /></a:solidFill>";
}
