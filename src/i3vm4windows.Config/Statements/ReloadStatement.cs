namespace i3vm4windows.Config.Statements;

/// <summary>reload: rescans the windows and re-applies the layout.</summary>
public sealed class ReloadStatement : CommandStatement
{
    public override string Name => "reload";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0)
            context.Intent = new CommandIntent(Action: ConfigAction.Refresh);
    }
}
