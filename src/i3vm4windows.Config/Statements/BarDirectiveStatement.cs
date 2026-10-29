namespace i3vm4windows.Config.Statements;

/// <summary>
/// A directive of the bar block; it warns when it appears anywhere
/// else.
/// </summary>
public abstract class BarDirectiveStatement : IConfigStatement
{
    public abstract string Name { get; }

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (!context.InBar)
        {
            context.Warn($"{Name} does not belong outside the bar block: {context.Line}");
            return;
        }

        Apply(context, parameters);
    }

    protected abstract void Apply(ConfigContext context, IReadOnlyList<string> parameters);
}
