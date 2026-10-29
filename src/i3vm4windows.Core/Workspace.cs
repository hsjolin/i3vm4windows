namespace i3vm4windows.Core;

/// <summary>
/// A workspace holds a layout tree, remembers which window has focus and
/// which output (display) it is currently bound to.
/// </summary>
public sealed class Workspace
{
    public int Index { get; }

    public string Name { get; }

    /// <summary>The output this workspace is currently shown on.</summary>
    public string OutputName { get; set; }

    /// <summary>Root container for all windows in this workspace.</summary>
    public SplitNode Root { get; }

    public long? FocusedWindowHandle { get; set; }

    public Workspace(int index, string outputName, LayoutMode defaultLayoutMode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        Index = index;
        Name = index.ToString();
        OutputName = outputName;
        Root = new (SplitOrientation.Horizontal, defaultLayoutMode);
    }

    /// <summary>All window handles in the tree, in layout order.</summary>
    public IReadOnlyList<long> WindowHandles => Root.Windows()
        .Select(w => w.WindowHandle)
        .ToList();
}
