namespace i3vm4windows.Core;

public enum SplitOrientation
{
    /// <summary>Children are laid out side by side along the X axis.</summary>
    Horizontal,

    /// <summary>Children are laid out stacked along the Y axis.</summary>
    Vertical,
}

/// <summary>A node in the layout tree, mirroring i3's container model.</summary>
public abstract class LayoutNode
{
    public SplitNode? Parent { get; internal set; }
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

    public List<LayoutNode> Children { get; } = new();

    public SplitNode(SplitOrientation orientation)
    {
        Orientation = orientation;
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
