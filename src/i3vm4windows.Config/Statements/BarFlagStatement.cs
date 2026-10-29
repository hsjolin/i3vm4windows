namespace i3vm4windows.Config.Statements;

/// <summary>
/// A yes/no flag of the bar block: clock, date, cpu or memory.
/// </summary>
public sealed class BarFlagStatement : BarDirectiveStatement
{
    private readonly Action<BarSettings, bool> _assign;

    public BarFlagStatement(string name, Action<BarSettings, bool> assign)
    {
        Name = name;
        _assign = assign;
    }

    public override string Name { get; }

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var enabled = parameters.Count == 1 &&
            parameters[0].Equals("yes", StringComparison.OrdinalIgnoreCase);
        var disabled = parameters.Count == 0 ||
            (parameters.Count == 1 && parameters[0].Equals("no", StringComparison.OrdinalIgnoreCase));
        if (!enabled && !disabled)
        {
            context.Warn($"Unsupported bar value: {context.Line}");
            return;
        }

        _assign(context.Bar, enabled);
    }
}
