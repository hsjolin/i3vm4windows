namespace i3vm4windows.Config.Statements;

/// <summary>scratchpad show: toggles the windows matching the binding's
/// criteria.</summary>
public sealed class ScratchpadStatement : CommandStatement
{
    public override string Name => "scratchpad";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.ScratchpadShow);
        }
    }
}
