using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Unisono;

/// <summary>
/// Writes a Word edit of an exported Google document back tab by tab, through the Docs API, instead of replacing the
/// whole document (Drive's docx import cannot hold tabs and loses comments). The edited docx is split at its tab-name
/// paragraphs (see <see cref="DocxFix"/>), each tab's paragraphs are compared with the document's, and only the
/// differences are sent: text inserted or deleted inside a paragraph, paragraphs added or removed, paragraph styles
/// (headings, title, normal text) changed. Everything else in the document, formatting and comments included, stays.
///
/// What cannot be carried over safely refuses the whole write-back (the caller then keeps the edit as its own file):
/// renamed or removed tab names, edited tables, removed images or other objects. Character formatting made in Word
/// (bold, colours) is not carried over. The update is conditional on the document's revision, so an edit made in
/// Google in the meantime is never overwritten.
/// </summary>
public static class DocsPatch
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const char Obj = '￼';        // an image, chip, footnote reference... anything that is not text
    private const string Opaque = "\u0001";   // prefix of a table or table of contents, compared as a whole

    public sealed record Para(string Text, string Style, int Start, int End)
    {
        public bool IsOpaque => Text.StartsWith(Opaque, StringComparison.Ordinal);
    }

    public sealed record Tab(string Id, string Title, int Depth, List<Para> Paras);

    // ------------------------------------------------------------ Google side

    /// <summary>The document's revision and tabs (document order, with nesting) from documents.get?includeTabsContent=true.</summary>
    public static (string Revision, List<Tab> Tabs) ReadGoogle(JsonElement root)
    {
        var tabs = new List<Tab>();
        void Walk(JsonElement list, int depth)
        {
            foreach (var t in list.EnumerateArray())
            {
                var props = t.GetProperty("tabProperties");
                var paras = new List<Para>();
                if (t.TryGetProperty("documentTab", out var dt) && dt.TryGetProperty("body", out var body) && body.TryGetProperty("content", out var content))
                    paras = ReadContent(content);
                tabs.Add(new Tab(props.GetProperty("tabId").GetString()!, props.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "", depth, paras));
                if (t.TryGetProperty("childTabs", out var c)) Walk(c, depth + 1);
            }
        }
        if (root.TryGetProperty("tabs", out var top)) Walk(top, 0);
        return (root.GetProperty("revisionId").GetString()!, tabs);
    }

    private static List<Para> ReadContent(JsonElement content)
    {
        var list = new List<Para>();
        foreach (var e in content.EnumerateArray())
        {
            var start = e.TryGetProperty("startIndex", out var s) ? s.GetInt32() : 0;
            var end = e.GetProperty("endIndex").GetInt32();
            if (e.TryGetProperty("paragraph", out var p))
            {
                var style = p.TryGetProperty("paragraphStyle", out var ps) && ps.TryGetProperty("namedStyleType", out var n) ? n.GetString()! : "NORMAL_TEXT";
                list.Add(new Para(ParagraphText(p), style, start, end));
            }
            else if (e.TryGetProperty("table", out var table))
                list.Add(new Para(Opaque + "table:" + Squash(string.Join("\n", TableTexts(table))), "", start, end));
            else if (e.TryGetProperty("tableOfContents", out _))
                list.Add(new Para(Opaque + "toc", "", start, end));
            // sectionBreak: nothing to compare
        }
        return list;
    }

    private static string ParagraphText(JsonElement p)
    {
        var sb = new StringBuilder();
        foreach (var el in p.GetProperty("elements").EnumerateArray())
        {
            if (el.TryGetProperty("textRun", out var tr)) sb.Append(tr.GetProperty("content").GetString());
            else if (el.TryGetProperty("pageBreak", out _)) sb.Append('\f');
            else sb.Append(Obj); // inline object, footnote reference, chip, date, horizontal rule, equation...
        }
        var text = sb.ToString();
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static IEnumerable<string> TableTexts(JsonElement table)
    {
        foreach (var row in table.GetProperty("tableRows").EnumerateArray())
            foreach (var cell in row.GetProperty("tableCells").EnumerateArray())
                foreach (var e in cell.GetProperty("content").EnumerateArray())
                    if (e.TryGetProperty("paragraph", out var p)) yield return ParagraphText(p);
    }

    // -------------------------------------------------------------- Word side

    /// <summary>The edited docx split into tabs, in the order of <paramref name="tabs"/>; null with a reason when it cannot be.</summary>
    public static List<List<Para>>? ReadDocx(string path, IReadOnlyList<Tab> tabs, out string? why)
    {
        why = null;
        XDocument doc;
        using (var zip = ZipFile.OpenRead(path))
        {
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) { why = "the Word file has no document part"; return null; }
            using var s = entry.Open();
            doc = XDocument.Load(s);
        }
        var body = doc.Root?.Element(W + "body");
        if (body == null) { why = "the Word file has no body"; return null; }

        var result = new List<List<Para>>();
        List<Para>? current = null;
        var next = 0;
        var depth = 0;
        // Exports made since tab names got outline levels have the headings in a tab moved down by depth + 1.
        var shifted = body.Elements(W + "p").Any(p => StyleOf(p) == "Title" && p.Element(W + "pPr")?.Element(W + "outlineLvl") != null);
        if (tabs.Count == 1)
        {
            // One tab: the export has no tab name, the whole body is that tab.
            current = new List<Para>();
            result.Add(current);
            next = 1;
            shifted = false;
        }
        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p")
            {
                var text = DocxText(el);
                var style = StyleOf(el);
                if (style == "Title" && next < tabs.Count && Same(text, tabs[next].Title))
                {
                    current = new List<Para>();
                    result.Add(current);
                    depth = tabs[next].Depth;
                    next++;
                    continue;
                }
                if (current == null)
                {
                    if (text.Trim().Length > 0) { why = "there is text before the first tab name"; return null; }
                    continue;
                }
                current.Add(new Para(text, GoogleStyle(style, shifted ? depth + 1 : 0), 0, 0));
            }
            else if (el.Name == W + "tbl")
            {
                if (current == null) { why = "there is a table before the first tab name"; return null; }
                current.Add(new Para(Opaque + "table:" + Squash(string.Join("\n", el.Descendants(W + "tc").SelectMany(tc => tc.Elements(W + "p")).Select(DocxText))), "", 0, 0));
            }
            else if (el.Name == W + "sdt")
            {
                current?.Add(new Para(Opaque + "toc", "", 0, 0));
            }
        }
        if (next != tabs.Count) { why = "tab names were renamed or removed in the Word file"; return null; }
        return result;
    }

    private static string? StyleOf(XElement p) => p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;

    private static string GoogleStyle(string? style, int shift)
    {
        if (style == "Title") return "TITLE";
        if (style == "Subtitle") return "SUBTITLE";
        if (style != null && Regex.Match(style, @"^Heading([1-9])$") is { Success: true } m)
            return $"HEADING_{Math.Clamp(int.Parse(m.Groups[1].Value) - shift, 1, 6)}";
        return "NORMAL_TEXT";
    }

    private static string DocxText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var r in p.Descendants(W + "r"))
        {
            // Deleted text of tracked changes is not part of the document.
            if (r.Ancestors().TakeWhile(a => a != p).Any(a => a.Name == W + "del" || a.Name == W + "moveFrom")) continue;
            foreach (var c in r.Elements())
            {
                var n = c.Name.LocalName;
                if (c.Name == W + "t") sb.Append(c.Value);
                else if (n == "tab") sb.Append('\t');
                else if (n == "br") sb.Append((string?)c.Attribute(W + "type") == "page" ? '\f' : '\v');
                else if (n == "cr") sb.Append('\v');
                else if (n is "drawing" or "pict" or "object" or "footnoteReference" or "endnoteReference" or "sym") sb.Append(Obj);
                else if (n == "noBreakHyphen") sb.Append('‑');
            }
        }
        return sb.ToString();
    }

    private static bool Same(string a, string b) => Squash(a) == Squash(b);
    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // ---------------------------------------------------------------- planning

    /// <summary>The Docs API requests that turn <paramref name="google"/> into <paramref name="edited"/>; none when nothing changed.</summary>
    public static List<object>? Plan(IReadOnlyList<Tab> google, IReadOnlyList<List<Para>> edited, out string? why)
    {
        why = null;
        var requests = new List<object>();
        for (var t = 0; t < google.Count; t++)
        {
            var tab = google[t];
            var full = tab.Paras;
            var g = TrimEnd(full);
            var e = TrimEnd(edited[t]);
            foreach (var (i1, i2, j1, j2) in Enumerable.Reverse(Hunks(g, e)))
            {
                if (!PlanHunk(tab, full, g, e, i1, i2, j1, j2, requests, out why)) { why = $"in the tab \"{tab.Title}\": {why}"; return null; }
            }
        }
        return requests;
    }

    private static List<Para> TrimEnd(List<Para> paras)
    {
        var n = paras.Count;
        while (n > 0 && paras[n - 1].Text.Length == 0 && paras[n - 1].Style == "NORMAL_TEXT") n--;
        return paras.Take(n).ToList();
    }

    private static bool Equal(Para a, Para b) => a.Text == b.Text && (a.IsOpaque || a.Style == b.Style);

    /// <summary>Ranges where the two paragraph lists differ, from a longest common subsequence.</summary>
    private static List<(int I1, int I2, int J1, int J2)> Hunks(List<Para> a, List<Para> b)
    {
        var n = a.Count; var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = Equal(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var hunks = new List<(int, int, int, int)>();
        int x = 0, y = 0, hi = -1, hj = -1;
        void Close() { if (hi >= 0) { hunks.Add((hi, x, hj, y)); hi = -1; } }
        while (x < n || y < m)
        {
            if (x < n && y < m && Equal(a[x], b[y])) { Close(); x++; y++; }
            else
            {
                if (hi < 0) { hi = x; hj = y; }
                if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) y++;
                else x++;
            }
        }
        Close();
        return hunks;
    }

    private static bool PlanHunk(Tab tab, List<Para> full, List<Para> g, List<Para> e, int i1, int i2, int j1, int j2, List<object> requests, out string? why)
    {
        why = null;
        var gh = g.Skip(i1).Take(i2 - i1).ToList();
        var eh = e.Skip(j1).Take(j2 - j1).ToList();
        if (gh.Any(p => p.IsOpaque) || eh.Any(p => p.IsOpaque)) { why = "a table or table of contents was edited, added or removed"; return false; }

        if (gh.Count == eh.Count)
        {
            // Same number of paragraphs: change each in place (text first, then its style), last one first.
            for (var k = gh.Count - 1; k >= 0; k--)
            {
                var (gp, ep) = (gh[k], eh[k]);
                if (gp.Text != ep.Text)
                {
                    var pre = 0;
                    while (pre < gp.Text.Length && pre < ep.Text.Length && gp.Text[pre] == ep.Text[pre]) pre++;
                    var suf = 0;
                    while (suf < gp.Text.Length - pre && suf < ep.Text.Length - pre && gp.Text[^(suf + 1)] == ep.Text[^(suf + 1)]) suf++;
                    var removed = gp.Text[pre..(gp.Text.Length - suf)];
                    var added = ep.Text[pre..(ep.Text.Length - suf)];
                    if (removed.Contains(Obj) || added.Contains(Obj)) { why = "an image or other object was changed"; return false; }
                    if (removed.Length > 0) requests.Add(Delete(tab.Id, gp.Start + pre, gp.Start + pre + removed.Length));
                    if (added.Length > 0) requests.Add(Insert(tab.Id, gp.Start + pre, added));
                }
                if (gp.Style != ep.Style) requests.Add(Style(tab.Id, gp.Start, gp.Start + ep.Text.Length + 1, ep.Style));
            }
            return true;
        }

        if (gh.Any(p => p.Text.Contains(Obj))) { why = "a paragraph with an image or other object was removed or split"; return false; }
        if (eh.Any(p => p.Text.Contains(Obj))) { why = "an image or other object was added"; return false; }

        int start, end;
        string text;
        var isDocEnd = gh.Count > 0 && ReferenceEquals(gh[^1], full[^1]);
        if (gh.Count == 0)
        {
            // Pure insertion: before the next compared paragraph, or after the last one.
            if (i1 < g.Count) { start = end = g[i1].Start; text = string.Join("\n", eh.Select(p => p.Text)) + "\n"; }
            else if (g.Count > 0)
            {
                start = end = g[^1].End - 1;                      // before the last paragraph's newline
                text = "\n" + string.Join("\n", eh.Select(p => p.Text));
                requests.Add(Insert(tab.Id, start, text));
                AddStyles(tab.Id, start + 1, eh, requests);
                return true;
            }
            else { start = end = full.Count > 0 ? full[0].Start : 1; text = string.Join("\n", eh.Select(p => p.Text)) + "\n"; }
        }
        else if (isDocEnd)
        {
            // The last paragraph's newline can never be deleted: replace up to it.
            start = gh[0].Start;
            end = gh[^1].End - 1;
            text = string.Join("\n", eh.Select(p => p.Text));
            if (eh.Count == 0 && i1 > 0) start = g[i1 - 1].End - 1; // drop the newline before instead
        }
        else
        {
            start = gh[0].Start;
            end = gh[^1].End;
            text = eh.Count > 0 ? string.Join("\n", eh.Select(p => p.Text)) + "\n" : "";
        }
        if (end > start) requests.Add(Delete(tab.Id, start, end));
        if (text.Length > 0) requests.Add(Insert(tab.Id, start, text));
        AddStyles(tab.Id, start, eh, requests);
        // The paragraph before took over the removed last paragraph's newline, and with it its style: set it back.
        if (isDocEnd && eh.Count == 0 && i1 > 0) requests.Add(Style(tab.Id, g[i1 - 1].Start, g[i1 - 1].End, g[i1 - 1].Style));
        return true;
    }

    private static void AddStyles(string tabId, int start, List<Para> paras, List<object> requests)
    {
        var cursor = start;
        foreach (var p in paras)
        {
            requests.Add(Style(tabId, cursor, cursor + p.Text.Length + 1, p.Style));
            cursor += p.Text.Length + 1;
        }
    }

    private static object Insert(string tabId, int index, string text) =>
        new { insertText = new { location = new { index, tabId }, text } };

    private static object Delete(string tabId, int start, int end) =>
        new { deleteContentRange = new { range = new { startIndex = start, endIndex = end, tabId } } };

    private static object Style(string tabId, int start, int end, string style) =>
        new { updateParagraphStyle = new { range = new { startIndex = start, endIndex = end, tabId }, paragraphStyle = new { namedStyleType = style }, fields = "namedStyleType" } };
}
