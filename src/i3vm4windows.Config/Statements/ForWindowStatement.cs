namespace i3vm4windows.Config.Statements;

/// <summary>
/// for_window [criteria] command: applies the command — a statement of
/// its own — to the windows matching the criteria: floating enable
/// leaves them alone, move container to workspace makes them join that
/// workspace when they appear, and move scratchpad hides them into the
/// scratchpad as they appear.
/// </summary>
public sealed class ForWindowStatement : TopLevelStatement
{
    public override string Name => "for_window";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var line = context.Line;
        var index = 0;
        if (parameters.Count == 0 || !parameters[0].StartsWith('['))
        {
            context.Warn($"for_window without criteria: {line}");
            return;
        }

        if (!ConfigGrammar.TryParseCriteria(parameters, ref index, out var rule, context))
            return;

        if (index + 1 >= parameters.Count)
        {
            context.Warn($"for_window without a command: {line}");
            return;
        }

        var command = ConfigGrammar.Rest(parameters, index + 1);
        var statement = context.Resolve(command[0]);
        if (statement is null)
        {
            context.Warn($"Unsupported for_window command: {line}");
            return;
        }

        context.CollectingIntent = true;
        context.Intent = null;
        try
        {
            statement.Execute(context, ConfigGrammar.Rest(command, 1));
        }
        finally
        {
            context.CollectingIntent = false;
        }

        var intent = context.Intent;
        context.Intent = null;
        if (intent?.Rule is { } ruleAction)
        {
            context.Rules.Add(ruleAction == WindowRuleAction.Floating
                ? rule!
                : rule! with { Action = ruleAction });
            return;
        }

        if (intent?.Action == ConfigAction.MoveToWorkspace)
        {
            context.Rules.Add(rule! with
            {
                Action = WindowRuleAction.MoveToWorkspace,
                Parameter = intent.Parameter,
            });
            return;
        }

        context.Warn($"Unsupported for_window command: {line}");
    }
}
