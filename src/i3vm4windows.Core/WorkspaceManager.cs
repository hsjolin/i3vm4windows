namespace i3vm4windows.Core;

/// <summary>
/// Owns a numbered set of workspaces (1-9), tracks which one is active and
/// moves windows between them. All state is plain data, so the whole class
/// is unit-testable without Windows.
/// </summary>
public sealed class WorkspaceManager
{
    private readonly Dictionary<int, Workspace> _workspaces = new();
    private readonly TilingEngine _engine = new();

    public int ActiveIndex { get; private set; } = 1;

    /// <summary>The workspace that currently receives new windows and layout.</summary>
    public Workspace Active => Get(ActiveIndex);

    /// <summary>Returns the workspace with the given number, creating it on first use.</summary>
    public Workspace Get(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        if (!_workspaces.TryGetValue(index, out var workspace))
        {
            workspace = new Workspace(index.ToString());
            _workspaces[index] = workspace;
        }

        return workspace;
    }

    /// <summary>Makes the workspace with the given number active.</summary>
    public bool SwitchTo(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        if (index == ActiveIndex)
            return false;

        ActiveIndex = index;
        return true;
    }

    public void AddWindow(long windowHandle) => _engine.AddWindow(Active, windowHandle);

    /// <summary>
    /// Adds a window to the workspace with the given number, creating it on
    /// first use, regardless of which workspace is active.
    /// </summary>
    public void AddWindowTo(int index, long windowHandle) => _engine.AddWindow(Get(index), windowHandle);

    /// <summary>Removes the window from whichever workspace owns it.</summary>
    public bool RemoveWindow(long windowHandle)
    {
        foreach (var workspace in _workspaces.Values)
        {
            if (_engine.RemoveWindow(workspace, windowHandle))
                return true;
        }

        return false;
    }

    public Workspace? FindOwner(long windowHandle) =>
        _workspaces.Values.FirstOrDefault(ws => ws.WindowHandles.Contains(windowHandle));

    /// <summary>Points the owning workspace at the given window, e.g. when it gains keyboard focus.</summary>
    public bool SetFocusedWindow(long windowHandle)
    {
        var owner = FindOwner(windowHandle);
        if (owner is null)
            return false;

        owner.FocusedWindowHandle = windowHandle;
        return true;
    }

    /// <summary>Moves the focused window of the active workspace to the workspace with the given number.
    /// Returns the moved handle, or null when nothing was moved.</summary>
    public long? MoveFocusedTo(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        if (index == ActiveIndex || Active.FocusedWindowHandle is not { } handle)
            return null;

        if (!_engine.RemoveWindow(Active, handle))
            return null;

        _engine.AddWindow(Get(index), handle);
        return handle;
    }

    public bool FocusRelative(int offset) => _engine.FocusRelative(Active, offset);

    public bool MoveFocused(int offset) => _engine.MoveFocused(Active, offset);

    public void ToggleOrientation() => _engine.ToggleOrientation(Active);

    public IReadOnlyList<AssignedRect> ComputeActiveLayout(
        Rect monitor, LayoutOptions? options = null) =>
        _engine.ComputeLayout(Active, monitor, options);
}
