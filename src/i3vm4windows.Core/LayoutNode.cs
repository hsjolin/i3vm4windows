namespace i3vm4windows.Core;

public enum SplitOrientation
{
    /// <summary>Children are laid out side by side along the X axis.</summary>
    Horizontal,

    /// <summary>Children are laid out stacked along the Y axis.</summary>
    Vertical,
}

/// <summary>How a split container presents its children.</summary>
public enum LayoutMode
{
    /// <summary>Children share the area along the split axis.</summary>
    Split,

    /// <summary>One child shows at a time, covering the whole area; the
    /// focused window picks which, like i3's tabbed layout.</summary>
    Tabbed,
}

/// <summary>A node in the layout tree, mirroring i3's container model.</summary>
public abstract class LayoutNode
{
    public SplitNode? Parent { get; internal set; }

    /// <summary>
    /// Relative share of the parent split's span along its axis. Siblings are
    /// sized proportionally; 1 for every node by default.
    /// </summary>
    public double Weight { get; set; } = 1;
}

/// <summary>A leaf node representing a managed window.</summary>
public sealed class WindowNode : LayoutNode
{
    public long WindowHandle { get; }

    /// <summary>When set, the window covers the whole workspace area.</summary>
    public bool IsFullscreen { get; set; }

    public WindowNode(long windowHandle)
    {
        WindowHandle = windowHandle;
    }
}

/// <summary>An interior node that divides its area among its children.</summary>
public sealed class SplitNode : LayoutNode
{
    public SplitOrientation Orientation { get; set; }

    public LayoutMode Mode { get; set; }

    public List<LayoutNode> Children { get; } = new();

    /// <summary>
    /// The span along the split axis (in pixels, gaps excluded) this split
    /// distributed at the last layout computation; used to translate manual
    /// resize requests into sibling weights.
    /// </summary>
    public double LastUsableSpan { get; internal set; }

    public SplitNode(SplitOrientation orientation, LayoutMode defaultLayoutMode)
    {
        Orientation = orientation;
        Mode = defaultLayoutMode;
    }

    /// <summary>Returns all window leaves in the subtree, in layout order.</summary>
    public IEnumerable<WindowNode> Windows()
    {
        foreach (var child in Children)
        {
            switch (child)
            {
                case WindowNode window:
                    yield return window;
                    break;
                case SplitNode split:
                    foreach (var inner in split.Windows())
                        yield return inner;
                    break;
            }
        }
    }
}
