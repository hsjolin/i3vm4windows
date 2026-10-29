namespace i3vm4windows.Config.Statements;

/// <summary>
/// floating toggle (a binding's action) and floating enable (a
/// for_window rule that leaves matching windows alone).
/// </summary>
public sealed class FloatingStatement : CommandStatement
{
    public override string Name => "floating";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count != 1)
            return;

        if (parameters[0].Equals("toggle", StringComparison.OrdinalIgnoreCase))
            context.Intent = new CommandIntent(
                Action: ConfigAction.FloatingToggle,
                Run: (runtime, _) => runtime.ToggleFloating());
        else if (parameters[0].Equals("enable", StringComparison.OrdinalIgnoreCase))
            context.Intent = new CommandIntent(Rule: WindowRuleAction.Floating);
    }
}
