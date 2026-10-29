namespace i3vm4windows.Config.Statements;

/// <summary>
/// bindsym keys command: a key combination bound to a command that is
/// itself a statement resolved from the container (exec "program",
/// workspace 3, layout tabbed, ...). A criteria prefix targets windows,
/// e.g. [class="CASCADIA_HOSTING_WINDOW_CLASS"] scratchpad show.
/// </summary>
public sealed class BindsymStatement : TopLevelStatement
{
    public override string Name => "bindsym";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var line = context.Line;
        if (parameters.Count > 0 &&
            parameters[0].Equals("--release", StringComparison.OrdinalIgnoreCase))
        {
            context.Warn($"Release-time bindings are not supported: {line}");
            return;
        }

        if (parameters.Count < 2)
        {
            context.Warn($"Incomplete bindsym: {line}");
            return;
        }

        var keys = parameters[0];
        var original = ConfigGrammar.Rest(parameters, 1);

        var index = 0;
        WindowRule? rule = null;
        if (original.Length > 0 && original[0].StartsWith('['))
        {
            if (!ConfigGrammar.TryParseCriteria(original, ref index, out rule, context))
                return;

            // The criteria ended on the token at index; the command follows.
            index++;
        }

        var command = ConfigGrammar.Rest(original, index);
        if (command.Length == 0)
        {
            context.Warn($"bindsym without a command: {line}");
            return;
        }

        if (!ConfigGrammar.TryParseKeys(keys, out var modifiers, out var key))
        {
            context.Warn($"Unsupported key combination: {line}");
            return;
        }

        // The hook dispatches combinations through its ownership of the Win
        // key, so a default-mode binding without Win has no reliable meaning
        // here; mode bindings dispatch through the hook's mode matching
        // instead.
        if (context.Mode is null && !modifiers.HasFlag(Modifiers.Win))
        {
            context.Warn($"Binding must include the Win key (Mod4): {line}");
            return;
        }

        // The command is a statement of its own, resolved like the top
        // level resolves its own statements; its intent becomes the
        // binding's action.
        var statement = context.Resolve(command[0]);
        if (statement is null)
        {
            context.Warn($"Unsupported command: {line}");
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
        if (intent?.Action is not { } action)
        {
            context.Warn($"Unsupported command: {line}");
            return;
        }

        // The stateless scratchpad toggle needs to know which window to act
        // on; criteria are how the configuration expresses that.
        if (action == ConfigAction.ScratchpadShow && rule is null)
        {
            context.Warn($"scratchpad show needs window criteria: {line}");
            return;
        }

        context.Bindings[(context.Mode ?? "default", modifiers, key)] =
            new Binding(
                modifiers, key, action, intent.Parameter, intent.Command, rule,
                context.Mode ?? "default");
    }
}
