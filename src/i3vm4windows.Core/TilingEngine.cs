namespace i3vm4windows.Core;

/// <summary>A window handle paired with the screen rectangle it should occupy.</summary>
public sealed record AssignedRect(long WindowHandle, Rect Rect);

/// <summary>The layout axis a manual resize applies to.</summary>
public enum ResizeAxis
{
    Width,
    Height,
}

/// <summary>
/// Pure, unit-testable layout engine that maintains an i3-style tree
/// (workspace -> split containers -> window leaves) and computes screen
/// rectangles for every managed window.
/// </summary>
public sealed class TilingEngine
{
    private const double MinWeight = 0.01;

    /// <summary>The smallest extent a manually resized window may keep.</summary>
    public const int MinWindowExtent = 32;
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

        // Weights turn into pixel units as soon as a resize sets them, so a
        // new window must not start at 1: the siblings' average gives it
        // exactly an (n+1)-th of the split while they keep their ratios.
        node.Weight = parent.Children.Count == 0
            ? 1
            : parent.Children.Sum(child => Math.Max(child.Weight, MinWeight)) / parent.Children.Count;

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

        ComputeSplit(workspace.Root, usable, options, result, workspace.FocusedWindowHandle);
        return result;
    }

    /// <summary>
    /// Switches the split containing the focused window (the root when
    /// nothing is focused) to the given mode, like i3's layout command.
    /// </summary>
    public void SetLayoutMode(Workspace workspace, LayoutMode mode)
    {
        var parent = Find(workspace.Root, workspace.FocusedWindowHandle ?? -1)?.Parent ?? workspace.Root;
        parent.Mode = mode;
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
        if (parent.Mode == LayoutMode.Tabbed)
        {
            // i3's "layout toggle split" returns a tabbed container to splits.
            parent.Mode = LayoutMode.Split;
            return;
        }

        parent.Orientation = parent.Orientation == SplitOrientation.Horizontal
            ? SplitOrientation.Vertical
            : SplitOrientation.Horizontal;
    }

    /// <summary>
    /// Applies a manual resize of a window: the window keeps the requested
    /// size along its parent split's axis and its siblings share the rest,
    /// keeping their relative proportions. Returns false when the resize is
    /// not allowed, e.g. across the split axis, below the minimum size, or
    /// in a split with a single child.
    /// </summary>
    public bool ApplyUserResize(Workspace workspace, long windowHandle, ResizeAxis axis, int requestedSize)
    {
        if (Find(workspace.Root, windowHandle) is not { } node || node.Parent is not { } parent)
            return false;

        var horizontal = parent.Orientation == SplitOrientation.Horizontal;
        if ((axis == ResizeAxis.Width) != horizontal || parent.Children.Count < 2)
            return false;

        var span = parent.LastUsableSpan;
        var max = span - MinWindowExtent * (parent.Children.Count - 1);
        if (span <= 0 || requestedSize < MinWindowExtent || requestedSize > max)
            return false;

        // Weights are in pixel units: the resized window takes the requested
        // extent, the siblings absorb the rest proportionally.
        node.Weight = requestedSize;

        var siblingSpan = span - requestedSize;
        var siblings = parent.Children.Where(child => !ReferenceEquals(child, node)).ToList();
        var oldTotal = siblings.Sum(child => Math.Max(child.Weight, MinWeight));
        foreach (var sibling in siblings)
            sibling.Weight = Math.Max(sibling.Weight, MinWeight) * siblingSpan / oldTotal;

        return true;
    }

    private static void ComputeSplit(
        SplitNode node, Rect rect, LayoutOptions options, List<AssignedRect> result, long? focused)
    {
        if (node.Children.Count == 0)
            return;

        if (node.Mode == LayoutMode.Tabbed)
        {
            // Only the child containing the focused window shows, covering
            // the whole area; the window manager hides the other members.
            switch (ActiveChild(node, focused))
            {
                case WindowNode window:
                    result.Add(new AssignedRect(window.WindowHandle, rect));
                    break;
                case SplitNode split:
                    ComputeSplit(split, rect, options, result, focused);
                    break;
            }

            return;
        }

        var horizontal = node.Orientation == SplitOrientation.Horizontal;
        var span = (horizontal ? rect.Width : rect.Height) - options.InnerGap * (node.Children.Count - 1);
        node.LastUsableSpan = span;

        // Children share the span proportionally to their weights; leftover
        // pixels go to the children with the largest fractional parts.
        var totalWeight = node.Children.Sum(child => Math.Max(child.Weight, MinWeight));
        var sizes = new int[node.Children.Count];
        var remainders = new double[node.Children.Count];
        for (var i = 0; i < node.Children.Count; i++)
        {
            var exact = span * Math.Max(node.Children[i].Weight, MinWeight) / totalWeight;
            sizes[i] = (int)exact;
            remainders[i] = exact - sizes[i];
        }

        var leftover = span - sizes.Sum();
        foreach (var i in Enumerable.Range(0, node.Children.Count)
                     .OrderByDescending(i => remainders[i])
                     .Take(leftover))
        {
            sizes[i]++;
        }

        var position = horizontal ? rect.X : rect.Y;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var size = sizes[i];
            var slice = horizontal
                ? new Rect(position, rect.Y, size, rect.Height)
                : new Rect(rect.X, position, rect.Width, size);

            switch (node.Children[i])
            {
                case WindowNode window:
                    result.Add(new AssignedRect(window.WindowHandle, slice));
                    break;
                case SplitNode split:
                    ComputeSplit(split, slice, options, result, focused);
                    break;
            }

            position += size + options.InnerGap;
        }
    }

    /// <summary>
    /// The child that shows in a tabbed container: the one holding the
    /// focused window, else the first child.
    /// </summary>
    private static LayoutNode ActiveChild(SplitNode node, long? focused) =>
        node.Children.FirstOrDefault(child => ContainsWindow(child, focused)) ?? node.Children[0];

    private static bool ContainsWindow(LayoutNode node, long? focused) =>
        focused is { } handle && node switch
        {
            WindowNode window => window.WindowHandle == handle,
            SplitNode split => split.Windows().Any(w => w.WindowHandle == handle),
            _ => false,
        };

    private static WindowNode? Find(LayoutNode node, long windowHandle) => node switch
    {
        WindowNode window => window.WindowHandle == windowHandle ? window : null,
        SplitNode split => split.Children
            .Select(child => Find(child, windowHandle))
            .FirstOrDefault(found => found is not null),
        _ => null,
    };
}
