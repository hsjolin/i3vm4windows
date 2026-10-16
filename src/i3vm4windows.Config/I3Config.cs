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
}

/// <summary>
/// A bindsym entry: the key combination and the action it triggers. The key
/// is normalized (canonical names, upper-case letters, digits).
/// </summary>
public sealed record Binding(Modifiers Modifiers, string Key, ConfigAction Action, int Parameter);

/// <summary>The result of parsing a configuration file.</summary>
public sealed record I3Config(IReadOnlyList<Binding> Bindings, IReadOnlyList<string> Warnings);
