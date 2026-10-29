using Microsoft.Extensions.DependencyInjection;

namespace i3vm4windows.Config.Statements;

/// <summary>
/// A statement that belongs at the top level of the configuration: it
/// warns when the bar block carries it instead.
/// </summary>
public abstract class TopLevelStatement : IConfigStatement
{
    public abstract string Name { get; }

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (context.InBar)
        {
            context.Warn($"{Name} does not belong in the bar block: {context.Line}");
            return;
        }

        Apply(context, parameters);
    }

    protected abstract void Apply(ConfigContext context, IReadOnlyList<string> parameters);
}

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

/// <summary>
/// for_window [criteria] command: applies the command — a statement of
/// its own — to the windows matching the criteria: floating enable
/// leaves them alone, move container to workspace makes them join that
/// workspace when they appear.
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
        if (intent?.Rule == WindowRuleAction.Floating)
        {
            context.Rules.Add(rule!);
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

/// <summary>
/// set $name value: stores a variable verbatim, substituted where it is
/// used, not on the set line itself.
/// </summary>
public sealed class SetStatement : TopLevelStatement
{
    public override string Name => "set";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count < 2 || !parameters[0].StartsWith('$'))
        {
            context.Warn($"Unsupported set directive: {context.Line}");
            return;
        }

        var value = string.Join(' ', ConfigGrammar.Rest(parameters, 1));
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            value = value[1..^1];
        context.Variables[parameters[0]] = value;
    }
}

/// <summary>
/// min_window_width n: the smallest width a window keeps before the
/// layout overlaps windows instead of shrinking them further.
/// </summary>
public sealed class MinWindowWidthStatement : TopLevelStatement
{
    public override string Name => "min_window_width";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            int.TryParse(parameters[0], out var width) && width > 0)
        {
            context.MinWindowWidth = width;
            return;
        }

        context.Warn($"Unsupported min_window_width: {context.Line}");
    }
}

/// <summary>bindcode: not supported, and says so.</summary>
public sealed class BindcodeStatement : TopLevelStatement
{
    public override string Name => "bindcode";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters) =>
        context.Warn($"bindcode is not supported: {context.Line}");
}

/// <summary>bar { ... }: opens the bar block.</summary>
public sealed class BarStatement : IConfigStatement
{
    public string Name => "bar";

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 && parameters[0] == "{")
        {
            if (context.Mode is not null || context.InBar)
            {
                context.Warn($"Nested block: {context.Line}");
                return;
            }

            context.InBar = true;
            return;
        }

        context.Warn($"Unsupported bar directive: {context.Line}");
    }
}

/// <summary>
/// A directive of the bar block; it warns when it appears anywhere
/// else.
/// </summary>
public abstract class BarDirectiveStatement : IConfigStatement
{
    public abstract string Name { get; }

    public void Execute(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (!context.InBar)
        {
            context.Warn($"{Name} does not belong outside the bar block: {context.Line}");
            return;
        }

        Apply(context, parameters);
    }

    protected abstract void Apply(ConfigContext context, IReadOnlyList<string> parameters);
}

/// <summary>position top|bottom: the bar's edge.</summary>
public sealed class BarPositionStatement : BarDirectiveStatement
{
    public override string Name => "position";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 &&
            parameters[0].Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            context.Bar.Position = "top";
            return;
        }

        if (parameters.Count == 1 &&
            parameters[0].Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            context.Bar.Position = "bottom";
            return;
        }

        context.Warn($"Unsupported bar position: {context.Line}");
    }
}

/// <summary>height n: the bar's height in pixels.</summary>
public sealed class BarHeightStatement : BarDirectiveStatement
{
    public override string Name => "height";

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 1 && int.TryParse(parameters[0], out var height))
        {
            if (height is < 16 or > 200)
            {
                context.Warn($"Bar height out of range: {context.Line}");
                return;
            }

            context.Bar.Height = height;
            return;
        }

        context.Warn($"Unsupported bar height: {context.Line}");
    }
}

/// <summary>
/// A yes/no flag of the bar block: clock, date, cpu or memory.
/// </summary>
public sealed class BarFlagStatement : BarDirectiveStatement
{
    private readonly Action<BarSettings, bool> _assign;

    public BarFlagStatement(string name, Action<BarSettings, bool> assign)
    {
        Name = name;
        _assign = assign;
    }

    public override string Name { get; }

    protected override void Apply(ConfigContext context, IReadOnlyList<string> parameters)
    {
        var enabled = parameters.Count == 1 &&
            parameters[0].Equals("yes", StringComparison.OrdinalIgnoreCase);
        var disabled = parameters.Count == 0 ||
            (parameters.Count == 1 && parameters[0].Equals("no", StringComparison.OrdinalIgnoreCase));
        if (!enabled && !disabled)
        {
            context.Warn($"Unsupported bar value: {context.Line}");
            return;
        }

        _assign(context.Bar, enabled);
    }
}

/// <summary>
/// Registers every built-in statement, so parses resolve them by name
/// from the container. Adding a statement means implementing
/// <see cref="IConfigStatement"/> and adding a line here.
/// </summary>
public static class ConfigStatementRegistration
{
    public static IServiceCollection AddConfigStatements(this IServiceCollection services)
    {
        services.AddSingleton<IConfigStatement, BindsymStatement>();
        services.AddSingleton<IConfigStatement, ForWindowStatement>();
        services.AddSingleton<IConfigStatement, SetStatement>();
        services.AddSingleton<IConfigStatement, ExecStatement>();
        services.AddSingleton<IConfigStatement, MinWindowWidthStatement>();
        services.AddSingleton<IConfigStatement, BindcodeStatement>();
        services.AddSingleton<IConfigStatement, ModeStatement>();
        services.AddSingleton<IConfigStatement, BarStatement>();
        services.AddSingleton<IConfigStatement, BarPositionStatement>();
        services.AddSingleton<IConfigStatement, BarHeightStatement>();
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "clock", (bar, value) => bar.Clock = value));
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "date", (bar, value) => bar.Date = value));
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "cpu", (bar, value) => bar.Cpu = value));
        services.AddSingleton<IConfigStatement>(new BarFlagStatement(
            "memory", (bar, value) => bar.Memory = value));
        services.AddSingleton<IConfigStatement, KillStatement>();
        services.AddSingleton<IConfigStatement, FocusStatement>();
        services.AddSingleton<IConfigStatement, MoveStatement>();
        services.AddSingleton<IConfigStatement, SplitStatement>();
        services.AddSingleton<IConfigStatement, LayoutStatement>();
        services.AddSingleton<IConfigStatement, FloatingStatement>();
        services.AddSingleton<IConfigStatement, ScratchpadStatement>();
        services.AddSingleton<IConfigStatement, WorkspaceStatement>();
        services.AddSingleton<IConfigStatement, ResizeStatement>();
        services.AddSingleton<IConfigStatement, ReloadStatement>();
        return services;
    }
}
