namespace i3vm4windows.Config.Statements;

/// <summary>
/// workspace n and workspace back_and_forth: switches to the workspace,
/// or back to the one that was active before the current one.
/// </summary>
public sealed class WorkspaceStatement : CommandStatement
{
    public override string Name => "workspace";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("back_and_forth", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.SwitchBackAndForth);
            return;
        }

        // Workspace names may span several tokens, e.g. "1: web".
        if (parameters.Count > 0 &&
            ConfigGrammar.TryParseWorkspace(string.Join(' ', parameters), out var workspace))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.SwitchWorkspace, Parameter: workspace);
        }
    }
}
