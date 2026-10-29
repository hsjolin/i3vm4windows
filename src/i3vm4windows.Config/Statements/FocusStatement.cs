namespace i3vm4windows.Config.Statements;

/// <summary>focus left|right: focuses the previous or next window.</summary>
public sealed class FocusStatement : CommandStatement
{
    public override string Name => "focus";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count != 1)
            return;

        context.Intent = parameters[0].ToLowerInvariant() switch
        {
            "left" => new CommandIntent(Action: ConfigAction.FocusPrevious),
            "right" => new CommandIntent(Action: ConfigAction.FocusNext),
            _ => null,
        };
    }
}
