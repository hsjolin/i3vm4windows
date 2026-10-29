namespace i3vm4windows.Config.Statements;

/// <summary>
/// The commands a bindsym nests and a for_window applies to the windows
/// it matches: each one resolves from the IoC container by its name and
/// writes its intent into the context for the statement that collected
/// it.
/// </summary>
public abstract class CommandStatement : IConfigStatement
{
    public abstract string Name { get; }

    public abstract void Execute(ConfigContext context, IReadOnlyList<string> parameters);
}

/// <summary>exec "program": starts the program from a binding, runs it
/// from the bar block as a status command, and at the top level is i3's
/// autostart.</summary>
public sealed class ExecStatement : CommandStatement
{
    public override string Name => "exec";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var rest = StripMarker(parameters);
        if (rest.Length == 0)
        {
            // A binding's bare exec warns as the unsupported command it
            // is; the top level and the bar keep their own wording.
            if (!context.CollectingIntent)
                context.Warn($"exec without a program: {context.Line}");
            return;
        }

        if (context.InBar)
        {
            context.Bar.Commands.Add(string.Join(' ', rest));
            return;
        }

        if (context.CollectingIntent)
        {
            // The program name and arguments keep their original case;
            // only the --no-startup-id marker of i3 is dropped.
            context.Intent = new CommandIntent(
                Action: ConfigAction.Launch,
                Command: string.Join(' ', rest));
            return;
        }

        context.Autostarts.Add(string.Join(' ', rest));
    }

    /// <summary>Drops the --no-startup-id marker of i3.</summary>
    private static string[] StripMarker(IReadOnlyList<string> parameters)
    {
        var rest = ConfigGrammar.Rest(parameters, 0);
        if (rest.Length > 0 &&
            rest[0].Equals("--no-startup-id", StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[1..];
        }

        return rest;
    }
}

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

/// <summary>
/// move left|right, move container to workspace, move workspace to
/// output next: moves the focused window or the whole workspace.
/// </summary>
public sealed class MoveStatement : CommandStatement
{
    public override string Name => "move";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 && parameters[0].Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.MovePrevious);
            return;
        }

        if (parameters.Count == 1 && parameters[0].Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.MoveNext);
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
                Action: ConfigAction.MoveToWorkspace, Parameter: workspace);
            return;
        }

        if (parameters.Count >= 3 &&
            parameters[0].Equals("to", StringComparison.OrdinalIgnoreCase) &&
            parameters[1].Equals("workspace", StringComparison.OrdinalIgnoreCase) &&
            ConfigGrammar.TryParseWorkspace(string.Join(' ', ConfigGrammar.Rest(parameters, 2)), out var shortWorkspace))
        {
            context.Intent = new CommandIntent(
                Action: ConfigAction.MoveToWorkspace, Parameter: shortWorkspace);
            return;
        }

        if (parameters.Count == 4 &&
            parameters[0].Equals("workspace", StringComparison.OrdinalIgnoreCase) &&
            parameters[1].Equals("to", StringComparison.OrdinalIgnoreCase) &&
            parameters[2].Equals("output", StringComparison.OrdinalIgnoreCase) &&
            parameters[3].Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.MoveWorkspaceToNextMonitor);
        }
    }
}

/// <summary>split toggle: flips the split orientation.</summary>
public sealed class SplitStatement : CommandStatement
{
    public override string Name => "split";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.ToggleOrientation);
        }
    }
}

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
            context.Intent = new CommandIntent(Action: ConfigAction.FloatingToggle);
        else if (parameters[0].Equals("enable", StringComparison.OrdinalIgnoreCase))
            context.Intent = new CommandIntent(Rule: WindowRuleAction.Floating);
    }
}

/// <summary>scratchpad show: toggles the windows matching the binding's
/// criteria.</summary>
public sealed class ScratchpadStatement : CommandStatement
{
    public override string Name => "scratchpad";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            context.Intent = new CommandIntent(Action: ConfigAction.ScratchpadShow);
        }
    }
}

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
            context.Intent = new CommandIntent(
                Action: ConfigAction.Mode, Command: parameters[0].Trim('"'));
            return;
        }

        context.Warn($"Unsupported mode directive: {context.Line}");
    }
}

/// <summary>
/// resize grow|shrink width|height n: resizes the focused window along
/// the split axis. The pixel amount drives the resize; ppt (percentage
/// points) has no meaning here.
/// </summary>
public sealed class ResizeStatement : CommandStatement
{
    public override string Name => "resize";

    public override void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count < 3 || !int.TryParse(parameters[2], out var amount))
            return;

        var grow = parameters[0].Equals("grow", StringComparison.OrdinalIgnoreCase);
        var width = parameters[1].Equals("width", StringComparison.OrdinalIgnoreCase);
        context.Intent = new CommandIntent(
            Action: grow
                ? width ? ConfigAction.ResizeGrowWidth : ConfigAction.ResizeGrowHeight
                : width ? ConfigAction.ResizeShrinkWidth : ConfigAction.ResizeShrinkHeight,
            Parameter: amount);
    }
}

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
