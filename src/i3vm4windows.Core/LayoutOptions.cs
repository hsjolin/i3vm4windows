namespace i3vm4windows.Core;

/// <summary>Tuning parameters applied when a layout is computed.</summary>
public sealed record LayoutOptions
{
    /// <summary>Margin between the monitor edge and the outermost windows.</summary>
    public int OuterMargin { get; init; } = 10;

    /// <summary>Gap between adjacent windows inside a split.</summary>
    public int InnerGap { get; init; } = 10;

    /// <summary>
    /// The smallest width a horizontal split gives its children. When the
    /// split can no longer fit this for every child, the children overlap
    /// like a spread deck of cards instead of shrinking below it.
    /// </summary>
    public int MinWindowWidth { get; init; } = 400;

    /// <summary>
    /// The layout a new workspace starts in: its root split presents the
    /// windows as tabs (i3's workspace_layout tabbed) or tiles them.
    /// </summary>
    public LayoutMode DefaultLayoutMode { get; init; } = LayoutMode.Split;

    public static LayoutOptions Default { get; } = new();
}
