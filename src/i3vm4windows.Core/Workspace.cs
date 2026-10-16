namespace i3vm4windows.Core;

/// <summary>A workspace holds a layout tree and remembers which window has focus.</summary>
public sealed class Workspace
{
    public string Name { get; }

    /// <summary>Root container for all windows in this workspace.</summary>
    public SplitNode Root { get; } = new(SplitOrientation.Horizontal);

    public long? FocusedWindowHandle { get; set; }

    public Workspace(string name)
    {
        Name = name;
    }

    /// <summary>All window handles in the tree, in layout order.</summary>
    public IReadOnlyList<long> WindowHandles => Root.Windows()
        .Select(w => w.WindowHandle)
        .ToList();
}
