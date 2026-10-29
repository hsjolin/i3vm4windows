using i3vm4windows.Config;
using i3vm4windows.Core;

namespace i3vm4windows.App.Services;

/// <summary>
/// The statements' runtime: what a bound command asks the app to do
/// when its binding fires, implemented against the window manager, the
/// binding modes and the process launcher. The statements own their
/// behavior — this only carries it out.
/// </summary>
internal sealed class ConfigRuntime : IConfigRuntime
{
    private readonly WindowManager _windowManager;
    private readonly Action<string> _setMode;

    public ConfigRuntime(WindowManager windowManager, Action<string> setMode)
    {
        _windowManager = windowManager;
        _setMode = setMode;
    }

    public void FocusRelative(int offset) => _windowManager.FocusRelative(offset);

    public void MoveRelative(int offset) => _windowManager.MoveFocused(offset);

    public void SwitchWorkspace(int index) => _windowManager.SwitchWorkspace(index);

    public void SwitchBackAndForth() => _windowManager.SwitchBackAndForth();

    public void MoveFocusedToWorkspace(int index) => _windowManager.MoveFocusedToWorkspace(index);

    public void MoveActiveWorkspaceToNextMonitor() =>
        _windowManager.MoveActiveWorkspaceToNextMonitor();

    public void ToggleOrientation() => _windowManager.ToggleOrientation();

    public void SetTabbed() => _windowManager.SetTabbed();

    public void SetSplit() => _windowManager.SetSplit();

    public void CloseFocused() => _windowManager.CloseFocused();

    public void Refresh() => _windowManager.RefreshWindows();

    public void ToggleFloating() => _windowManager.ToggleFloating();

    public void ToggleScratchpad(WindowRule rule) => _windowManager.ToggleScratchpad(rule);

    public void ResizeFocused(ResizeAxis axis, int delta) =>
        _windowManager.ResizeFocused(axis, delta);

    public void SetMode(string name) => _setMode(name);

    public void Launch(string command) => Launcher.Start(command);
}
