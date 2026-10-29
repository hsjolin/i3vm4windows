namespace i3vm4windows.Config.Statements;

/// <summary>kill: closes the focused window.</summary>
public sealed class KillStatement : CommandStatement
{
    public override string Name => "kill";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0)
            context.Intent = new CommandIntent(Action: ConfigAction.CloseFocused);
    }
}
