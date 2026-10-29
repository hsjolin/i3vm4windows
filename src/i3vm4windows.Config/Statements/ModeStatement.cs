namespace i3vm4windows.Config.Statements;

/// <summary>
/// mode "name": enters the named binding mode — the command of a
/// binding — and mode "name" { opens the mode's block.
/// </summary>
public sealed class ModeStatement : CommandStatement
{
    public override string Name => "mode";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        // mode "name" { opens a binding mode block; } closes it in the
        // parser.
        if (parameters.Count == 2 && parameters[1] == "{")
        {
            if (context.Mode is not null || context.InBar)
            {
                context.Warn($"Nested mode block: {context.Line}");
                return;
            }

            context.Mode = parameters[0].Trim('"');
            return;
        }

        if (parameters.Count == 1)
        {
            var name = parameters[0].Trim('"');
            context.Intent = new CommandIntent(
                Action: ConfigAction.Mode,
                Command: name,
                Run: (runtime, _) => runtime.SetMode(name));
            return;
        }

        context.Warn($"Unsupported mode directive: {context.Line}");
    }
}
