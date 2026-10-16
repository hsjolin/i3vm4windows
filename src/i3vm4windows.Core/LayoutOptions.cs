namespace i3vm4windows.Core;

/// <summary>Tuning parameters applied when a layout is computed.</summary>
public sealed record LayoutOptions
{
    /// <summary>Margin between the monitor edge and the outermost windows.</summary>
    public int OuterMargin { get; init; } = 10;

    /// <summary>Gap between adjacent windows inside a split.</summary>
    public int InnerGap { get; init; } = 10;

    public static LayoutOptions Default { get; } = new();
}
