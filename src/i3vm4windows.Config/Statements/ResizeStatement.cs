namespace i3vm4windows.Config.Statements;

/// <summary>
/// resize grow|shrink width|height n: resizes the focused window along
/// the split axis. The pixel amount drives the resize; ppt (percentage
/// points) has no meaning here.
/// </summary>
public sealed class ResizeStatement : CommandStatement
{
    public override string Name => "resize";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count < 3 || !int.TryParse(parameters[2], out var amount))
            return;

        var grow = parameters[0].Equals("grow", StringComparison.OrdinalIgnoreCase);
        var width = parameters[1].Equals("width", StringComparison.OrdinalIgnoreCase);
        context.Intent = new CommandIntent(
            Action: grow
                ? width ? ConfigAction.ResizeGrowWidth : ConfigAction.ResizeGrowHeight
                : width ? ConfigAction.ResizeShrinkWidth : ConfigAction.ResizeShrinkHeight,
            Parameter: amount);
    }
}
