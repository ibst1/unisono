using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Unisono;

/// <summary>
/// Repairs and completes Google's docx export for Word:
///
///   sizes   Google writes styles with sizes like w:sz="34.079999923706055"; Word only accepts whole numbers there and
///           drops such a style. Every fractional numeric attribute in word/*.xml is rounded.
///   tabs    A document with tabs is exported with each tab name as a Title paragraph, which Word's navigation pane does
///           not show. With the tab list from the Docs API, tab names get the outline level of their nesting (1, or 2
///           for a tab inside a tab), so the pane lists them. Headings keep their numbers (Heading 1 in Google is
///           Heading 1 in Word, which is what one reaches for when writing in Word), so they sit beside the tab names
///           rather than beneath them: Word fixes the level of its heading styles. The document variable
///           "UnisonoHeadings" = "same" marks this; exports from version 4 (headings moved one level down) lack it.
/// </summary>
public static class DocxFix
{
    /// <summary>Bumped when the repair changes, so earlier exports are made again once.</summary>
    public const int Version = 5;

    /// <summary>Document variable written into exports whose headings keep Google's numbers (see the summary).</summary>
    public const string HeadingsVariable = "UnisonoHeadings";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly Regex Decimal = new(@"(w:[A-Za-z]+)=""(-?\d+)\.(\d+)""", RegexOptions.Compiled);
    private static readonly Regex HeadingStyle = new(@"^Heading([1-9])$", RegexOptions.Compiled);

    /// <summary>Elements that come after w:outlineLvl in w:pPr (Word rejects the file when the order is wrong).</summary>
    private static readonly HashSet<string> AfterOutline = new() { "divId", "cnfStyle", "rPr", "sectPr", "pPrChange" };

    /// <param name="tabs">The document's tabs in document order with their nesting depth (0 = top level); null or a single
    /// tab: nothing to do for tabs.</param>
    public static void Apply(string docxPath, IReadOnlyList<(string Title, int Depth)>? tabs = null)
    {
        using var zip = ZipFile.Open(docxPath, ZipArchiveMode.Update);
        var parts = zip.Entries.Where(e => e.FullName.StartsWith("word/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
        var result = parts.ToDictionary(p => p.Key, p => RoundSizes(p.Value));

        if (tabs is { Count: > 1 } && result.TryGetValue("word/document.xml", out var document) && OutlineTabsCore(document, tabs) is { } outlined)
        {
            result["word/document.xml"] = outlined;
            if (result.TryGetValue("word/settings.xml", out var settings)) result["word/settings.xml"] = WithHeadingsVariable(settings);
        }


        foreach (var (name, xml) in result)
        {
            if (xml == parts[name]) continue;
            zip.GetEntry(name)!.Delete();
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new System.Text.UTF8Encoding(false));
            w.Write(xml);
        }
    }

    private static string RoundSizes(string xml) => Decimal.Replace(xml, m =>
        $"{m.Groups[1].Value}=\"{Math.Round(double.Parse($"{m.Groups[2].Value}.{m.Groups[3].Value}", System.Globalization.CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero)}\"");

    /// <summary>Null when the tab names are not where they were expected: then the document stays as Google made it.</summary>
    private static string? OutlineTabsCore(string xml, IReadOnlyList<(string Title, int Depth)> tabs)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var body = doc.Root?.Element(W + "body");
        if (body == null) return null;
        var next = 0;          // the tab whose name is expected next
        var depth = -1;        // nesting depth of the tab being read; -1 = before the first tab name
        foreach (var p in body.Descendants(W + "p"))
        {
            var pStyle = p.Element(W + "pPr")?.Element(W + "pStyle");
            var style = pStyle?.Attribute(W + "val")?.Value;
            if (style == "Title" && next < tabs.Count && Same(Text(p), tabs[next].Title))
            {
                depth = tabs[next].Depth;
                SetOutline(p.Element(W + "pPr")!, Math.Min(depth, 8));
                next++;
            }
        }
        return next == 0 ? null : doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Elements that follow w:docVars in w:settings (the schema fixes the order).</summary>
    private static readonly HashSet<string> AfterDocVars = new()
    {
        "rsids", "mathPr", "attachedSchema", "themeFontLang", "clrSchemeMapping", "doNotIncludeSubdocsInStats", "doNotAutoCompressPictures",
        "forceUpgrade", "captions", "readModeInkLockDown", "smartTagType", "schemaLibrary", "shapeDefaults", "doNotEmbedSmartTags", "decimalSymbol", "listSeparator",
    };

    private static string WithHeadingsVariable(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = doc.Root!;
        var vars = root.Element(W + "docVars");
        if (vars == null)
        {
            vars = new XElement(W + "docVars");
            var before = root.Elements().FirstOrDefault(e => AfterDocVars.Contains(e.Name.LocalName));
            if (before != null) before.AddBeforeSelf(vars); else root.Add(vars);
        }
        vars.Elements(W + "docVar").Where(v => (string?)v.Attribute(W + "name") == HeadingsVariable).Remove();
        vars.Add(new XElement(W + "docVar", new XAttribute(W + "name", HeadingsVariable), new XAttribute(W + "val", "same")));
        return doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string Text(XElement p) => string.Concat(p.Descendants(W + "t").Select(t => t.Value));

    private static bool Same(string a, string b) =>
        string.Equals(Regex.Replace(a, @"\s+", " ").Trim(), Regex.Replace(b, @"\s+", " ").Trim(), StringComparison.Ordinal);

    private static void SetOutline(XElement pPr, int level)
    {
        var outline = pPr.Element(W + "outlineLvl");
        if (outline != null) { outline.SetAttributeValue(W + "val", level); return; }
        outline = new XElement(W + "outlineLvl", new XAttribute(W + "val", level));
        var before = pPr.Elements().FirstOrDefault(e => AfterOutline.Contains(e.Name.LocalName));
        if (before != null) before.AddBeforeSelf(outline);
        else pPr.Add(outline);
    }
}
