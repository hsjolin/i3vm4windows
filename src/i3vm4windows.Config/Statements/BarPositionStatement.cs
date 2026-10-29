namespace i3vm4windows.Config.Statements;

/// <summary>position top|bottom: the bar's edge.</summary>
public sealed class BarPositionStatement : BarDirectiveStatement
{
    public override string Name => "position";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            context.Bar.Position = "top";
            return;
        }

        if (parameters.Count == 1 &&
            parameters[0].Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            context.Bar.Position = "bottom";
            return;
        }

        context.Warn($"Unsupported bar position: {context.Line}");
    }
}
