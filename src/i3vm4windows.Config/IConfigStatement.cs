namespace i3vm4windows.Config;

/// <summary>
/// One statement of the configuration file, resolved from the IoC
/// container by its name: bindsym, exec, set — and the commands a
/// bindsym nests (exec "program", workspace 3, layout tabbed, ...).
/// Execute applies the statement with its parameters to the
/// configuration being parsed.
/// </summary>
public interface IConfigStatement
{
    /// <summary>The statement's name as it appears in the configuration.</summary>
    string Name { get; }

    /// <summary>
    /// Executes the statement with its parameters. A statement whose
    /// parameters are statements of their own (bindsym's command)
    /// resolves them through the context and collects their intent.
    /// </summary>
    void Execute(ConfigContext context, IReadOnlyList<string> parameters);
}

/// <summary>
/// The outcome of a command statement, collected by the statement that
/// resolves it: the action a binding triggers, or the rule a for_window
/// directive builds — and the command's runtime behavior, which the
/// statement that parsed it owns: the Run delegate executes against the
/// runtime, with the binding's criteria-carrying rule as its second
/// argument.
/// </summary>
public sealed record CommandIntent(
    ConfigAction? Action = null,
    WindowRuleAction? Rule = null,
    int Parameter = 0,
    string? Command = null,
    Action<IConfigRuntime, WindowRule?>? Run = null);

/// <summary>
/// Everything one parse accumulates: the bindings, rules, autostarts
/// and bar settings being built, the variables, the mode and bar
/// blocks' state, and the warnings. Statements resolve their own
/// parameters as statements through the resolver.
/// </summary>
public sealed class ConfigContext
{
    private readonly Dictionary<string, IConfigStatement> _statements =
        new(StringComparer.OrdinalIgnoreCase);

    public ConfigContext(IEnumerable<IConfigStatement> statements)
    {
        foreach (var statement in statements)
            _statements[statement.Name] = statement;
    }

    /// <summary>The statement with the given name, or null when the
    /// configuration carries a statement this app has no implementation
    /// for.</summary>
    public IConfigStatement? Resolve(string name) =>
        _statements.GetValueOrDefault(name);

    /// <summary>Later bindsym lines win over earlier ones for the same
    /// key combination, like i3 replaces bindings; the mode is part of
    /// the key so default and mode bindings coexist.</summary>
    public Dictionary<(string Mode, Modifiers Modifiers, string Key), Binding> Bindings { get; } = new();

    public List<WindowRule> Rules { get; } = new();

    public Dictionary<string, string> Variables { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> Autostarts { get; } = new();

    /// <summary>The smallest width a window keeps before the layout
    /// overlaps windows instead of shrinking them further.</summary>
    public int MinWindowWidth { get; set; } = 400;

    /// <summary>The binding mode whose block is open, or null in the
    /// default mode.</summary>
    public string? Mode { get; set; }

    /// <summary>Whether the bar block is open.</summary>
    public bool InBar { get; set; }

    /// <summary>The bar settings as they are parsed.</summary>
    public BarSettings Bar { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>The line being parsed, for warnings.</summary>
    public string Line { get; set; } = "";

    /// <summary>
    /// The intent of the command statement being collected by the
    /// statement that resolved it (bindsym, for_window); command
    /// statements write here, the collector reads and clears.
    /// </summary>
    public CommandIntent? Intent { get; set; }

    /// <summary>Whether a binding or rule builder is collecting the
    /// command being executed, so statements that mean different things
    /// at the top level (exec) act as commands.</summary>
    public bool CollectingIntent { get; set; }

    public void Warn(string message) => Warnings.Add(message);
}

/// <summary>The bar block's directives as they are parsed.</summary>
public sealed class BarSettings
{
    public string Position = "top";
    public int Height = 30;
    public bool Clock;
    public bool Date;
    public bool Cpu;
    public bool Memory;
    public List<string> Commands = new();

    public BarConfig ToConfig() => new(
        Position, Height, Clock, Date, Cpu, Memory, Commands);
}
