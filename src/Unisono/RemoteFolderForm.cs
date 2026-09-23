namespace Unisono.App;

/// <summary>What the folder picker needs from a remote: child folders of a node, lazily.</summary>
interface IFolderSource
{
    string RootName { get; }
    /// <summary>My Drive itself can be synced; a NAS root (the list of shared folders) cannot.</summary>
    bool RootSelectable { get; }
    Task<List<(string Name, string Key)>> ChildrenAsync(string? key, CancellationToken ct);
    /// <summary>The value to store as RemotePath for a node.</summary>
    string PathOf(TreeNode node, string? key);
}

sealed class DriveFolderSource : IFolderSource
{
    private readonly DriveClient _drive;
    public DriveFolderSource(DriveClient drive) => _drive = drive;
    public string RootName => "My Drive";
    public bool RootSelectable => true;

    public async Task<List<(string Name, string Key)>> ChildrenAsync(string? key, CancellationToken ct) =>
        (await _drive.ListChildrenAsync(key ?? "root", ct))
            .Where(DriveClient.IsFolder)
            .Select(f => (f.Name, f.Id))
            .ToList();

    public string PathOf(TreeNode node, string? key)
    {
        var full = node.FullPath; // "My Drive/A/B"
        return full.Length <= RootName.Length ? "" : full[(RootName.Length + 1)..];
    }
}

sealed class NasFolderSource : IFolderSource
{
    private readonly SynologyClient _nas;
    public NasFolderSource(SynologyClient nas) => _nas = nas;
    public string RootName => "Shared folders";
    public bool RootSelectable => false;

    public async Task<List<(string Name, string Key)>> ChildrenAsync(string? key, CancellationToken ct) =>
        (await _nas.ListAsync(key ?? "/", ct))
            .Where(e => e.IsDir && !e.Name.StartsWith('#') && e.Name != "@eaDir")
            .Select(e => (e.Name, e.Path))
            .ToList();

    public string PathOf(TreeNode node, string? key) => key ?? "/";
}

/// <summary>Lazy tree of remote folders; returns the chosen RemotePath.</summary>
sealed class RemoteFolderForm : Form
{
    private sealed record NodeInfo(string? Key) { public bool Loaded { get; set; } }

    private readonly IFolderSource _source;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, PathSeparator = "/" };
    private readonly Button _ok = new() { Text = "Select", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
    private readonly Label _path = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CancellationTokenSource _cts = new();

    public string SelectedPath { get; private set; } = "";

    public RemoteFolderForm(IFolderSource source, string title)
    {
        _source = source;
        Text = title;
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(480, 520);
        MinimizeBox = false;
        ShowInTaskbar = false;

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_path, 0, 0);
        bottom.Controls.Add(Ui.Row(_ok, cancel), 1, 0);
        Controls.Add(_tree);
        Controls.Add(bottom);
        AcceptButton = _ok;
        CancelButton = cancel;

        var root = new TreeNode(source.RootName) { Tag = new NodeInfo(null) };
        root.Nodes.Add(new TreeNode("loading..."));
        _tree.Nodes.Add(root);
        _tree.BeforeExpand += (_, e) => { if (e.Node != null) _ = LoadAsync(e.Node); };
        _tree.AfterSelect += (_, e) => OnSelect(e.Node);
        Shown += (_, _) => root.Expand();
        FormClosed += (_, _) => _cts.Cancel();
    }

    private void OnSelect(TreeNode? node)
    {
        if (node?.Tag is not NodeInfo info) { _ok.Enabled = false; return; }
        var selectable = info.Key != null || _source.RootSelectable;
        SelectedPath = selectable ? _source.PathOf(node, info.Key) : "";
        _path.Text = selectable ? (SelectedPath.Length == 0 ? "(all of My Drive)" : SelectedPath) : "Pick a folder inside a shared folder";
        _ok.Enabled = selectable;
    }

    private async Task LoadAsync(TreeNode node)
    {
        if (node.Tag is not NodeInfo info || info.Loaded) return;
        info.Loaded = true;
        try
        {
            var children = await _source.ChildrenAsync(info.Key, _cts.Token);
            node.Nodes.Clear();
            foreach (var (name, key) in children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var child = new TreeNode(name) { Tag = new NodeInfo(key) };
                child.Nodes.Add(new TreeNode("loading..."));
                node.Nodes.Add(child);
            }
            if (children.Count == 0) node.Nodes.Add(new TreeNode("(no subfolders)") { ForeColor = SystemColors.GrayText });
            node.Expand();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            info.Loaded = false;
            node.Nodes.Clear();
            node.Nodes.Add(new TreeNode("loading..."));
            if (!IsDisposed) Ui.Error(this, Ui.Explain(ex));
        }
    }
}
