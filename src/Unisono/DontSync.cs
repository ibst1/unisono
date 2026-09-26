namespace Unisono.App;

/// <summary>
/// Explorer's "Don't sync this (Unisono)": adds the item to the .unisonoignore of the (innermost) pair it lies in, after
/// asking. The running tray app notices the file change and leaves the item alone from its next cycle on.
/// </summary>
static class DontSync
{
    public static int Run(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim().TrimEnd('\\'));
            var pair = Config.Load().Pairs
                .Where(p => p.LocalPath.Length > 0 && path.StartsWith(p.LocalFullPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LocalFullPath.Length) // pairs can nest: the innermost wins
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"{path} is not inside a folder Unisono syncs.");
            var root = pair.LocalFullPath.TrimEnd('\\');
            var rel = path[(root.Length + 1)..].Replace('\\', '/');
            var isDir = Directory.Exists(path);
            var answer = MessageBox.Show(
                $"Stop syncing {(isDir ? "the folder " : "")}'{Path.GetFileName(path)}'?\n\n" +
                "It stays as it is, both here and on the other side; Unisono just leaves it alone from now on, " +
                "on every computer that syncs this folder.\n\n" +
                $"To undo, remove the line from Ignore in the pair's settings (the file {IgnoreRules.FileName} in {root}).",
                "Unisono", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return 1;
            IgnoreFile.AddItem(root, rel, isDir);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unisono", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }
}
