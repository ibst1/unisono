using System.Text.RegularExpressions;

namespace Unisono;

/// <summary>
/// The names of the copies kept when a file changed on both sides, "name (conflict COMPUTER yyyy-MM-dd HHmm).ext", or
/// when a Word edit of a Google document could not go back into it, "name (edited COMPUTER ...).docx".
/// </summary>
public static class ConflictCopy
{
    public static readonly Regex Name = new(
        @"^(?<stem>.+) \((?<kind>conflict|edited) (?<machine>[^()]+?) (?<when>\d{4}-\d{2}-\d{2} \d{4})\)(?<ext>\.[^.]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsCopy(string rel) => Name.IsMatch(PathUtil.NameOf(rel));
}
