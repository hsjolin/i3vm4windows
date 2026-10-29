namespace i3vm4windows.Config.Statements;

/// <summary>
/// move left|right, move container to workspace, move workspace to
/// output next — and, as a for_window rule, move scratchpad sends
/// matching windows into the scratchpad when they appear.
/// </summary>
public sealed class MoveStatement : CommandStatement
{
    public override string Name => "move";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("scratchpad", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Rule: WindowRuleAction.Scratchpad);
            return;
        }

        if (parameters.Count == 1 && parameters[0].Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.MovePrevious,
                Run: (runtime, _) => runtime.MoveRelative(-1));
            return;
        }

        if (parameters.Count == 1 && parameters[0].Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.MoveNext,
                Run: (runtime, _) => runtime.MoveRelative(1));
            return;
        }

        // Workspace names may span several tokens, e.g. "3: web".
        if (parameters.Count >= 4 &&
            parameters[0].Equals("container", StringComparison.OrdinalIgnoreCase) &&
            parameters[1].Equals("to", StringComparison.OrdinalIgnoreCase) &&
            parameters[2].Equals("workspace", StringComparison.OrdinalIgnoreCase) &&
            ConfigGrammar.TryParseWorkspace(string.Join(' ', ConfigGrammar.Rest(parameters, 3)), out var workspace))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.MoveToWorkspace,
                Parameter: workspace,
                Run: (runtime, _) => runtime.MoveFocusedToWorkspace(workspace));
            return;
        }

        if (parameters.Count >= 3 &&
            parameters[0].Equals("to", StringComparison.OrdinalIgnoreCase) &&
            parameters[1].Equals("workspace", StringComparison.OrdinalIgnoreCase) &&
            ConfigGrammar.TryParseWorkspace(string.Join(' ', ConfigGrammar.Rest(parameters, 2)), out var shortWorkspace))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.MoveToWorkspace,
                Parameter: shortWorkspace,
                Run: (runtime, _) => runtime.MoveFocusedToWorkspace(shortWorkspace));
            return;
        }

        if (parameters.Count == 4 &&
            parameters[0].Equals("workspace", StringComparison.OrdinalIgnoreCase) &&
            parameters[1].Equals("to", StringComparison.OrdinalIgnoreCase) &&
            parameters[2].Equals("output", StringComparison.OrdinalIgnoreCase) &&
            parameters[3].Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.MoveWorkspaceToNextMonitor,
                Run: (runtime, _) => runtime.MoveActiveWorkspaceToNextMonitor());
        }
    }
}
