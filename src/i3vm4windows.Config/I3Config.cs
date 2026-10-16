using System.Text.RegularExpressions;

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
/// <paramref name="Command"/> carries the program for Launch bindings and
/// the canonical key combination for Emit bindings;
/// <paramref name="Rule"/> targets windows for criteria commands like
/// scratchpad show.
/// </summary>
public sealed record Binding(
    Modifiers Modifiers,
    string Key,
    ConfigAction Action,
    int Parameter,
    string? Command = null,
    WindowRule? Rule = null);

/// <summary>The result of parsing a configuration file.</summary>
public sealed record I3Config(
    IReadOnlyList<Binding> Bindings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<WindowRule> Rules);

/// <summary>
/// A for_window rule: windows whose class and/or title match the regular
/// expressions are left alone by the tiler ("floating enable" in i3 terms —
/// this app has no floating layout, so matching windows are simply never
/// managed).
/// </summary>
public sealed record WindowRule(Regex? Class, Regex? Title)
{
    public bool Matches(string windowClass, string title) =>
        (Class is null || Class.IsMatch(windowClass)) &&
        (Title is null || Title.IsMatch(title));
}
