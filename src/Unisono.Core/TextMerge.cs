using System.Security.Cryptography;
using System.Text;

namespace Unisono;

/// <summary>
/// Line-based three-way merge (diff3) of small text files, for a file changed on both sides since the last sync:
/// the edits of each side against the last synced version (the base) are combined when they touch different lines.
/// Edits that touch the same or neighbouring lines overlap; then the caller keeps both files as before, or (when the
/// user asks for it in the Conflicts window) both sides' lines are written between git-style markers.
/// Line endings are compared without '\r' and written as this computer's version had them; a UTF-8 BOM is kept.
/// </summary>
public static class TextMerge
{
    /// <summary>Larger files are never merged.</summary>
    public const long MaxBytes = 1 << 20;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt" };

    /// <summary>Notes and plain text: the files a line-by-line merge suits.</summary>
    public static bool Eligible(string rel) => Extensions.Contains(Path.GetExtension(rel));

    /// <param name="Bytes">The merged file; null when edits overlap and no markers were asked for.</param>
    /// <param name="Overlaps">How many places both sides changed differently (0 = a clean merge).</param>
    public sealed record Outcome(byte[]? Bytes, int Overlaps);

    /// <summary>One line of a two-way comparison: ' ' both, '-' only left, '+' only right, '~' lines left out.</summary>
    public sealed record DiffLine(char Op, string Text);

    /// <summary>Above this many differing lines the comparison gives up (the files have little in common).</summary>
    private const int MaxEdits = 4000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Md5Hex(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Merges <paramref name="ours"/> (this computer) and <paramref name="theirs"/> (the other side) against
    /// <paramref name="ancestor"/>. Null when one of them is not UTF-8 text or they differ too much to compare.
    /// With <paramref name="markers"/>, overlapping places are written with both sides' lines between markers.
    /// </summary>
    public static Outcome? Merge(byte[] ancestor, byte[] ours, byte[] theirs, (string Ours, string Theirs)? markers = null)
    {
        var o = Decode(ancestor); var a = Decode(ours); var b = Decode(theirs);
        if (o == null || a == null || b == null) return null;
        var lo = Lines(o.Value.Text); var la = Lines(a.Value.Text); var lb = Lines(b.Value.Text);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] io = Ids(lo, ids), ia = Ids(la, ids), ib = Ids(lb, ids);
        var ha = Hunks(io, ia); var hb = Hunks(io, ib);
        if (ha == null || hb == null) return null;

        var all = ha.Select(h => (H: h, Side: 0)).Concat(hb.Select(h => (H: h, Side: 1)))
            .OrderBy(x => x.H.OStart).ThenBy(x => x.H.OEnd).ToList();
        var output = new StringBuilder();
        var overlaps = 0;
        var oPos = 0;
        for (var i = 0; i < all.Count;)
        {
            // One region: hunks that overlap or touch in the base. Touching counts: neighbouring lines changed on
            // both sides are as likely to belong together as the same line.
            var start = all[i].H.OStart;
            var end = all[i].H.OEnd;
            var group = new List<(Hunk H, int Side)> { all[i++] };
            while (i < all.Count && all[i].H.OStart <= end)
            {
                end = Math.Max(end, all[i].H.OEnd);
                group.Add(all[i++]);
            }
            for (; oPos < start; oPos++) output.Append(lo[oPos]);
            oPos = end;

            var sides = group.Select(g => g.Side).Distinct().ToList();
            var textA = Side(group, 0, lo, la, start, end);
            var textB = Side(group, 1, lo, lb, start, end);
            if (sides.Count == 1) { Append(output, sides[0] == 0 ? textA : textB); continue; }
            if (textA.SequenceEqual(textB, StringComparer.Ordinal)) { Append(output, textA); continue; } // the same edit on both sides
            overlaps++;
            if (markers is not { } m) continue;
            EndLine(output);
            output.Append("<<<<<<< ").Append(m.Ours).Append('\n');
            Append(output, textA); EndLine(output);
            output.Append("=======\n");
            Append(output, textB); EndLine(output);
            output.Append(">>>>>>> ").Append(m.Theirs).Append('\n');
        }
        for (; oPos < lo.Count; oPos++) output.Append(lo[oPos]);

        if (overlaps > 0 && markers == null) return new Outcome(null, overlaps);
        var text = output.ToString();
        if (a.Value.Crlf) text = text.Replace("\n", "\r\n");
        var body = StrictUtf8.GetBytes(text);
        return new Outcome(a.Value.Bom ? Utf8Bom.Concat(body).ToArray() : body, overlaps);
    }

    /// <summary>A line-by-line comparison of two texts with <paramref name="context"/> unchanged lines around each change; null when not text.</summary>
    public static List<DiffLine>? Diff(byte[] left, byte[] right, int context = 3)
    {
        var l = Decode(left); var r = Decode(right);
        if (l == null || r == null) return null;
        var ll = Lines(l.Value.Text); var lr = Lines(r.Value.Text);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = Matches(Ids(ll, ids), Ids(lr, ids));
        if (matches == null) return null;
        var full = new List<DiffLine>();
        int x = 0, y = 0;
        foreach (var (mx, my) in matches.Append((ll.Count, lr.Count)))
        {
            for (; x < mx; x++) full.Add(new DiffLine('-', ll[x].TrimEnd('\n')));
            for (; y < my; y++) full.Add(new DiffLine('+', lr[y].TrimEnd('\n')));
            if (mx < ll.Count) full.Add(new DiffLine(' ', ll[mx].TrimEnd('\n')));
            x = mx + 1; y = my + 1;
        }
        // Unchanged stretches longer than the context on both ends are shortened to a marker.
        var keep = new bool[full.Count];
        for (var i = 0; i < full.Count; i++)
            if (full[i].Op != ' ')
                for (var j = Math.Max(0, i - context); j <= Math.Min(full.Count - 1, i + context); j++) keep[j] = true;
        var shown = new List<DiffLine>();
        for (var i = 0; i < full.Count; i++)
        {
            if (keep[i]) { shown.Add(full[i]); continue; }
            var skipped = 0;
            while (i < full.Count && !keep[i]) { skipped++; i++; }
            i--;
            shown.Add(new DiffLine('~', $"{skipped} unchanged line(s)"));
        }
        return shown;
    }

    // ---------------------------------------------------------------- text

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static (string Text, bool Bom, bool Crlf)? Decode(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text;
        try { text = StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
        catch (DecoderFallbackException) { return null; }
        if (text.Contains('\0')) return null;
        var crlf = 0; var lf = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') crlf++; else lf++; }
        return (text.Replace("\r\n", "\n"), bom, crlf > lf);
    }

    /// <summary>Lines with their '\n' (the last one may have none).</summary>
    private static List<string> Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static int[] Ids(List<string> lines, Dictionary<string, int> ids)
    {
        var result = new int[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (!ids.TryGetValue(lines[i], out var id)) ids[lines[i]] = id = ids.Count;
            result[i] = id;
        }
        return result;
    }

    private static void Append(StringBuilder sb, List<string> lines) { foreach (var l in lines) sb.Append(l); }

    /// <summary>A marker must start on a line of its own, also after a last line without '\n'.</summary>
    private static void EndLine(StringBuilder sb) { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); }

    // ---------------------------------------------------------------- diff

    /// <summary>Base lines [OStart, OEnd) replaced by the side's lines [XStart, XEnd).</summary>
    private readonly record struct Hunk(int OStart, int OEnd, int XStart, int XEnd);

    private static List<Hunk>? Hunks(int[] o, int[] x)
    {
        var matches = Matches(o, x);
        if (matches == null) return null;
        var hunks = new List<Hunk>();
        int io = 0, ix = 0;
        foreach (var (mo, mx) in matches.Append((o.Length, x.Length)))
        {
            if (mo > io || mx > ix) hunks.Add(new Hunk(io, mo, ix, mx));
            io = mo + 1; ix = mx + 1;
        }
        return hunks;
    }

    /// <summary>What one side has in place of base lines [start, end): its own hunks there, base lines elsewhere.</summary>
    private static List<string> Side(List<(Hunk H, int Side)> group, int side, List<string> o, List<string> x, int start, int end)
    {
        var result = new List<string>();
        var pos = start;
        foreach (var (h, _) in group.Where(g => g.Side == side).OrderBy(g => g.H.OStart))
        {
            for (; pos < h.OStart; pos++) result.Add(o[pos]);
            for (var j = h.XStart; j < h.XEnd; j++) result.Add(x[j]);
            pos = h.OEnd;
        }
        for (; pos < end; pos++) result.Add(o[pos]);
        return result;
    }

    /// <summary>
    /// The matching lines of a shortest edit script (Myers' O(ND) algorithm), as ascending index pairs; null when more
    /// than <see cref="MaxEdits"/> lines differ.
    /// </summary>
    private static List<(int A, int B)>? Matches(int[] a, int[] b)
    {
        // Common start and end are matched directly; Myers runs on what is left in between.
        int pre = 0, n0 = a.Length, m0 = b.Length;
        while (pre < n0 && pre < m0 && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < n0 - pre && suf < m0 - pre && a[n0 - 1 - suf] == b[m0 - 1 - suf]) suf++;
        var n = n0 - pre - suf; var m = m0 - pre - suf;
        var result = new List<(int, int)>();
        for (var i = 0; i < pre; i++) result.Add((i, i));

        var max = n + m;
        var v = new int[2 * max + 3];
        var off = max + 1;
        var trace = new List<int[]>();
        var found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            if (d > MaxEdits) return null;
            var snap = new int[2 * d + 1];
            Array.Copy(v, off - d, snap, 0, 2 * d + 1);
            trace.Add(snap);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[pre + x] == b[pre + y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }

        var middle = new List<(int, int)>();
        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var snap = trace[d]; // the furthest points after d-1 edits, for diagonals -d..d
            int V(int k) => snap[k + d];
            var k0 = cx - cy;
            var prevK = k0 == -d || (k0 != d && V(k0 - 1) < V(k0 + 1)) ? k0 + 1 : k0 - 1;
            var prevX = V(prevK);
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { middle.Add((pre + cx - 1, pre + cy - 1)); cx--; cy--; }
            cx = prevX; cy = prevY;
        }
        while (cx > 0 && cy > 0) { middle.Add((pre + cx - 1, pre + cy - 1)); cx--; cy--; }
        middle.Reverse();
        result.AddRange(middle);
        for (var i = 0; i < suf; i++) result.Add((n0 - suf + i, m0 - suf + i));
        return result;
    }
}
