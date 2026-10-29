namespace i3vm4windows.Config.Statements;

/// <summary>split toggle: flips the split orientation.</summary>
public sealed class SplitStatement : CommandStatement
{
    public override string Name => "split";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.ToggleOrientation);
        }
    }
}
