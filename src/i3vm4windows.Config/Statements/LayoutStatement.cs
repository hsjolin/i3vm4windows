namespace i3vm4windows.Config.Statements;

/// <summary>
/// layout tabbed|default and layout toggle split: presents the split as
/// tabs or returns it to normal tiling.
/// </summary>
public sealed class LayoutStatement : CommandStatement
{
    public override string Name => "layout";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1)
        {
            if (parameters[0].Equals("tabbed", StringComparison.OrdinalIgnoreCase))
                context.Intent = new CommandIntent(Action: ConfigAction.SetTabbed);
            else if (parameters[0].Equals("default", StringComparison.OrdinalIgnoreCase))
                context.Intent = new CommandIntent(Action: ConfigAction.SetSplit);
            return;
        }

        if (parameters.Count == 2 &&
            parameters[0].Equals("toggle", StringComparison.OrdinalIgnoreCase) &&
            parameters[1].Equals("split", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.ToggleOrientation);
        }
    }
}
