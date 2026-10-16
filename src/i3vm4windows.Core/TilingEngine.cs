namespace i3vm4windows.Core;

/// <summary>A window handle paired with the screen rectangle it should occupy.</summary>
public sealed record AssignedRect(long WindowHandle, Rect Rect);

/// <summary>
/// Pure, unit-testable layout engine that maintains an i3-style tree
/// (workspace -> split containers -> window leaves) and computes screen
/// rectangles for every managed window.
/// </summary>
public sealed class TilingEngine
{
    /// <summary>
    /// Adds a window to the workspace, placed right after the focused window
    /// in its parent split (i3-style "open next to the focused window").
    /// </summary>
    public void AddWindow(Workspace workspace, long windowHandle)
    {
        if (Find(workspace.Root, windowHandle) is not null)
            return;

        var node = new WindowNode(windowHandle);
        var focused = Find(workspace.Root, workspace.FocusedWindowHandle ?? -1);

        var parent = focused?.Parent ?? workspace.Root;
        var index = focused is null ? parent.Children.Count : parent.Children.IndexOf(focused) + 1;

        parent.Children.Insert(index, node);
        node.Parent = parent;
        workspace.FocusedWindowHandle = windowHandle;
    }

    /// <summary>Removes a window from the tree. Empty parents are collapsed.</summary>
    public bool RemoveWindow(Workspace workspace, long windowHandle)
    {
        if (Find(workspace.Root, windowHandle) is not { } node || node.Parent is not { } parent)
            return false;

        parent.Children.Remove(node);

        // Collapse any ancestors left empty, up to (but not including) the root.
        var current = parent;
        while (current.Children.Count == 0 && current.Parent is { } grand)
        {
            grand.Children.Remove(current);
            current = grand;
        }

        if (workspace.FocusedWindowHandle == windowHandle)
            workspace.FocusedWindowHandle = workspace.WindowHandles.Count > 0
                ? workspace.WindowHandles[0]
                : null;

        return true;
    }

    /// <summary>
    /// Computes the rectangle for every window in the workspace. Children of a
    /// split share the available area equally along the split axis.
    /// </summary>
    public IReadOnlyList<AssignedRect> ComputeLayout(
        Workspace workspace, Rect monitor, LayoutOptions? options = null)
    {
        options ??= LayoutOptions.Default;

        var usable = new Rect(
            monitor.X + options.OuterMargin,
            monitor.Y + options.OuterMargin,
            Math.Max(0, monitor.Width - 2 * options.OuterMargin),
            Math.Max(0, monitor.Height - 2 * options.OuterMargin));

        var result = new List<AssignedRect>();

        var fullscreen = workspace.Root.Windows().FirstOrDefault(w => w.IsFullscreen);
        if (fullscreen is not null)
        {
            result.Add(new AssignedRect(fullscreen.WindowHandle, usable));
            return result;
        }

        ComputeSplit(workspace.Root, usable, options, result);
        return result;
    }

    /// <summary>
    /// Moves focus to the window <paramref name="offset"/> positions away in
    /// layout order, wrapping around at the ends.
    /// </summary>
    public bool FocusRelative(Workspace workspace, int offset)
    {
        var handles = workspace.WindowHandles.ToList();
        if (handles.Count == 0)
            return false;

        var current = workspace.FocusedWindowHandle is { } focused ? handles.IndexOf(focused) : -1;
        if (current < 0)
        {
            workspace.FocusedWindowHandle = handles[offset > 0 ? 0 : handles.Count - 1];
            return true;
        }

        var next = ((current + offset) % handles.Count + handles.Count) % handles.Count;
        workspace.FocusedWindowHandle = handles[next];
        return true;
    }

    /// <summary>
    /// Moves the focused window within its parent split by <paramref name="offset"/>
    /// slots. Clamps at the split boundary; returns false if no move happened.
    /// </summary>
    public bool MoveFocused(Workspace workspace, int offset)
    {
        if (Find(workspace.Root, workspace.FocusedWindowHandle ?? -1) is not { } focused ||
            focused.Parent is not { } parent)
            return false;

        var index = parent.Children.IndexOf(focused);
        var target = index + offset;
        if (target < 0 || target >= parent.Children.Count)
            return false;

        parent.Children.RemoveAt(index);
        parent.Children.Insert(target, focused);
        return true;
    }

    /// <summary>
    /// Flips the orientation of the split containing the focused window
    /// (or the root split when nothing is focused).
    /// </summary>
    public void ToggleOrientation(Workspace workspace)
    {
        var parent = Find(workspace.Root, workspace.FocusedWindowHandle ?? -1)?.Parent ?? workspace.Root;
        parent.Orientation = parent.Orientation == SplitOrientation.Horizontal
            ? SplitOrientation.Vertical
            : SplitOrientation.Horizontal;
    }

    private static void ComputeSplit(
        SplitNode node, Rect rect, LayoutOptions options, List<AssignedRect> result)
    {
        if (node.Children.Count == 0)
            return;

        var horizontal = node.Orientation == SplitOrientation.Horizontal;
        var span = (horizontal ? rect.Width : rect.Height) - options.InnerGap * (node.Children.Count - 1);
        var baseSize = span / node.Children.Count;
        var remainder = span - baseSize * node.Children.Count;

        var position = horizontal ? rect.X : rect.Y;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            var slice = horizontal
                ? new Rect(position, rect.Y, size, rect.Height)
                : new Rect(rect.X, position, rect.Width, size);

            switch (node.Children[i])
            {
                case WindowNode window:
                    result.Add(new AssignedRect(window.WindowHandle, slice));
                    break;
                case SplitNode split:
                    ComputeSplit(split, slice, options, result);
                    break;
            }

            position += size + options.InnerGap;
        }
    }

    private static WindowNode? Find(LayoutNode node, long windowHandle) => node switch
    {
        WindowNode window => window.WindowHandle == windowHandle ? window : null,
        SplitNode split => split.Children
            .Select(child => Find(child, windowHandle))
            .FirstOrDefault(found => found is not null),
        _ => null,
    };
}
