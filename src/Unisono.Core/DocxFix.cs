using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Unisono;

/// <summary>
/// Repairs what Google's docx export gets wrong for Word. Its styles carry sizes like w:sz="34.079999923706055", but
/// Word only accepts whole numbers there and drops such a style, so a document's tab names (exported with the Title
/// style) and anything else in it look like plain text. Every numeric WordprocessingML attribute is rounded.
/// </summary>
public static class DocxFix
{
    /// <summary>Bumped when the repair changes, so earlier exports are made again once.</summary>
    public const int Version = 2;

    private static readonly Regex Decimal = new(@"(w:[A-Za-z]+)=""(-?\d+)\.(\d+)""", RegexOptions.Compiled);

    public static void Apply(string docxPath)
    {
        using var zip = ZipFile.Open(docxPath, ZipArchiveMode.Update);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("word/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList())
        {
            string xml;
            using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();
            var fixedXml = Decimal.Replace(xml, m =>
                $"{m.Groups[1].Value}=\"{Math.Round(double.Parse($"{m.Groups[2].Value}.{m.Groups[3].Value}", System.Globalization.CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero)}\"");
            if (fixedXml == xml) continue;
            var name = entry.FullName;
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new System.Text.UTF8Encoding(false));
            w.Write(fixedXml);
        }
    }
}
