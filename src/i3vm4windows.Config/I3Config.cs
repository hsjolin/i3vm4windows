using System.Text.RegularExpressions;
using i3vm4windows.Core;

namespace i3vm4windows.Config;

/// <summary>
/// Modifier flags parsed from i3 key names. The values match the Win32
/// MOD_* constants the hotkey host registers with.
/// </summary>
[Flags]
public enum Modifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>The tiling actions a binding can trigger.</summary>
public enum ConfigAction
{
    FocusPrevious,
    FocusNext,
    MovePrevious,
    MoveNext,
    SwitchWorkspace,
    MoveToWorkspace,
    ToggleOrientation,
    SetTabbed,
    SetSplit,
    SwitchBackAndForth,
    Mode,
    ResizeGrowWidth,
    ResizeShrinkWidth,
    ResizeGrowHeight,
    ResizeShrinkHeight,
    CloseFocused,
    Refresh,
    MoveWorkspaceToNextMonitor,
    Launch,
    FloatingToggle,
    ScratchpadShow,
}

/// <summary>
/// A bindsym entry: the key combination and the action it triggers. The key
/// is normalized (canonical names, upper-case letters, digits);
/// <paramref name="Command"/> carries the program for Launch bindings, the
/// mode name for Mode bindings;
/// <paramref name="Rule"/> targets windows for criteria commands like
/// scratchpad show; <paramref name="Mode"/> names the binding mode
/// the binding lives in ("default" outside mode blocks); and
/// <paramref name="Run"/> is the parsed command's runtime behavior,
/// executed against the runtime when the binding fires.
/// </summary>
public sealed record Binding(
    Modifiers Modifiers,
    string Key,
    ConfigAction Action,
    int Parameter,
    string? Command = null,
    WindowRule? Rule = null,
    string Mode = "default",
    Action<IConfigRuntime, WindowRule?>? Run = null);

/// <summary>
/// The bar's configuration, from the bar block of the configuration
/// file. The defaults describe the plain bar: top of every display,
/// 30 pixels tall, no status output.
/// </summary>
public sealed record BarConfig(
    string Position = "top",
    int Height = 30,
    bool Clock = false,
    bool Date = false,
    bool Cpu = false,
    bool Memory = false,
    IReadOnlyList<string>? StatusCommands = null)
{
    public IReadOnlyList<string> Commands => StatusCommands ?? Array.Empty<string>();
}

/// <summary>The result of parsing a configuration file.</summary>
public sealed record I3Config(
    IReadOnlyList<Binding> Bindings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<WindowRule> Rules,
    IReadOnlyList<string> Exec,
    int MinWindowWidth = 400,
    LayoutMode DefaultWorkspaceLayout = LayoutMode.Split,
    BarConfig? Bar = null);

/// <summary>What a for_window rule does with the windows it matches.</summary>
public enum WindowRuleAction
{
    /// <summary>Matching windows are never managed ("floating enable").</summary>
    Floating,

    /// <summary>Matching windows join the given workspace when they appear.</summary>
    MoveToWorkspace,

    /// <summary>Matching windows appear directly into the scratchpad,
    /// hidden until a scratchpad show binding reveals them.</summary>
    Scratchpad,
}

/// <summary>
/// A for_window rule: windows whose class and/or title match the regular
/// expressions get the rule's action applied when they appear — "floating
/// enable" leaves them entirely alone (this app has no floating layout, so
/// they are simply never managed), "move container to workspace" makes
/// them join that workspace instead of the active one.
/// </summary>
public sealed record WindowRule(
    Regex? Class,
    Regex? Title,
    WindowRuleAction Action = WindowRuleAction.Floating,
    int Parameter = 0)
{
    public bool Matches(string windowClass, string title) =>
        (Class is null || Class.IsMatch(windowClass)) &&
        (Title is null || Title.IsMatch(title));
}
