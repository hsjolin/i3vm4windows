namespace i3vm4windows.Config.Statements;

/// <summary>
/// min_window_width n: the smallest width a window keeps before the
/// layout overlaps windows instead of shrinking them further.
/// </summary>
public sealed class MinWindowWidthStatement : TopLevelStatement
{
    public override string Name => "min_window_width";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            int.TryParse(parameters[0], out var width) && width > 0)
        {
            context.MinWindowWidth = width;
            return;
        }

        context.Warn($"Unsupported min_window_width: {context.Line}");
    }
}
