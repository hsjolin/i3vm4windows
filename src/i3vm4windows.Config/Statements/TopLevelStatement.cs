namespace i3vm4windows.Config.Statements;

/// <summary>
/// A statement that belongs at the top level of the configuration: it
/// warns when the bar block carries it instead.
/// </summary>
public abstract class TopLevelStatement : IConfigStatement
{
    public abstract string Name { get; }

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (context.InBar)
        {
            context.Warn($"{Name} does not belong in the bar block: {context.Line}");
            return;
        }

        Apply(context, parameters);
    }

    protected abstract void Apply(ConfigContext context, IReadOnlyList<string> parameters);
}
