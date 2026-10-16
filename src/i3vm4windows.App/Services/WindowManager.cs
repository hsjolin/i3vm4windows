using System.Text;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Core;
using Microsoft.Win32;

namespace i3vm4windows.App.Services;

/// <summary>
/// Tracks top-level windows across all connected monitors and tiles the
/// global workspaces on the outputs they are bound to, applying the computed
/// layout via SetWindowPos.
/// </summary>
internal sealed class WindowManager : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "MSCTFIME UI",
        "Default IME",
    };

    private readonly WorkspaceManager _workspaces = new();
    private readonly Dictionary<IntPtr, MonitorInfo> _monitors = new();
    private readonly Dictionary<long, Rect> _assignedRects = new();
    private readonly LayoutOptions _options = new() { OuterMargin = 8, InnerGap = 8 };
    private readonly HashSet<long> _managedHandles = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly uint _ownProcessId;
    private readonly Win32.WinEventDelegate _winEventDelegate;
    private AppState? _state;
    private bool _bindingsRestored;
    private IntPtr _winEventHook;
    private bool _disposed;

    public WindowManager()
    {
        _ownProcessId = (uint)Environment.ProcessId;
        _state = StateStore.Load();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshWindows();
        };
        _winEventDelegate = OnWinEvent;
    }

    public void Start()
    {
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_DESTROY,
            Win32.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RefreshWindows();
    }

    /// <summary>
    /// Reacts to display configuration changes (a monitor being connected,
    /// disconnected or reconfigured): the debounced refresh re-syncs the
    /// monitor list, migrates orphaned windows and re-tiles.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        DebounceRefresh();
    }

    /// <summary>Restarts the refresh timer so it fires once changes settle.</summary>
    private void DebounceRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void RefreshWindows()
    {
        if (_disposed)
            return;

        ForgetDeadWindows();
        ReconcileMonitors();
        ReassignWindows();
        ReconcileUserWindowChanges();
        DiscoverNewWindows();
        ApplyLayout();
        SaveState();
    }

    public void ApplyLayout()
    {
        foreach (var monitor in _monitors.Values)
        {
            var assignments = _workspaces.ComputeActiveLayout(monitor.DeviceName, monitor.WorkArea, _options);
            foreach (var (handle, rect) in assignments)
            {
                // Skip windows that are already in place: our own SetWindowPos
                // calls would otherwise re-trigger the refresh cycle.
                var hwnd = new IntPtr(handle);
                if (Win32.GetWindowRect(hwnd, out var current) &&
                    current.Left == rect.X && current.Top == rect.Y &&
                    current.Right == rect.Right && current.Bottom == rect.Bottom)
                {
                    _assignedRects[handle] = rect;
                    continue;
                }

                PositionWindow(handle, rect);
                _assignedRects[handle] = rect;
            }
        }
    }

    public void FocusRelative(int offset)
    {
        var output = ActiveMonitor().DeviceName;
        if (!_workspaces.FocusRelative(output, offset))
            return;

        if (_workspaces.ActiveOn(output).FocusedWindowHandle is { } handle)
            Win32.SetForegroundWindow(new IntPtr(handle));
    }

    public void MoveFocused(int offset)
    {
        if (_workspaces.MoveFocused(ActiveMonitor().DeviceName, offset))
            ApplyLayout();
    }

    public void ToggleOrientation()
    {
        _workspaces.ToggleOrientation(ActiveMonitor().DeviceName);
        ApplyLayout();
    }

    public void CloseFocused()
    {
        if (_workspaces.ActiveOn(ActiveMonitor().DeviceName).FocusedWindowHandle is { } handle)
            Win32.PostMessage(new IntPtr(handle), Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Activates the workspace with the given number on the output it is
    /// bound to (new workspaces are created on the focused output): hides the
    /// windows of the output's previous workspace, shows the target's and
    /// moves focus to it.
    /// </summary>
    public void SwitchWorkspace(int index)
    {
        var result = _workspaces.SwitchTo(index, ActiveMonitor().DeviceName);

        if (result.Changed)
        {
            if (_workspaces.TryGet(result.PreviousIndex) is { } previous)
            {
                foreach (var handle in previous.WindowHandles)
                    Win32.ShowWindow(new IntPtr(handle), Win32.SW_HIDE);
            }

            ShowAllWindows(result.NewIndex);
        }

        ApplyLayout();
        SaveState();

        if (_workspaces.TryGet(result.NewIndex)?.FocusedWindowHandle is { } focused)
            Win32.SetForegroundWindow(new IntPtr(focused));
    }

    /// <summary>
    /// Moves the focused window to the workspace with the given number; it
    /// becomes visible when that workspace is active on its output.
    /// </summary>
    public void MoveFocusedToWorkspace(int index)
    {
        var output = ActiveMonitor().DeviceName;
        if (_workspaces.MoveFocusedTo(index, output) is not { } moved)
            return;

        var visible = _workspaces.GetActiveIndex(moved.Target.OutputName) == moved.Target.Index;
        Win32.ShowWindow(new IntPtr(moved.WindowHandle), visible ? Win32.SW_SHOW : Win32.SW_HIDE);

        if (_workspaces.ActiveOn(output).FocusedWindowHandle is { } next)
            Win32.SetForegroundWindow(new IntPtr(next));

        ApplyLayout();
        SaveState();
    }

    /// <summary>
    /// Moves the workspace that is active on the focused monitor to the next
    /// monitor (ordered left to right, wrapping around) and activates it
    /// there; the source monitor activates a replacement workspace.
    /// </summary>
    public void MoveActiveWorkspaceToNextMonitor()
    {
        var monitors = _monitors.Values
            .OrderBy(m => m.WorkArea.X)
            .ThenBy(m => m.WorkArea.Y)
            .ToList();
        if (monitors.Count < 2)
            return;

        var source = ActiveMonitor();
        var target = monitors[(monitors.IndexOf(source) + 1) % monitors.Count];

        var move = _workspaces.MoveActiveWorkspaceToOutput(source.DeviceName, target.DeviceName);
        if (move is null)
            return;

        ShowAllWindows(move.MovedIndex);
        ShowAllWindows(move.SourceReplacementIndex);

        ApplyLayout();
        SaveState();

        if (_workspaces.TryGet(move.MovedIndex)?.FocusedWindowHandle is { } focused)
            Win32.SetForegroundWindow(new IntPtr(focused));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SaveState();

        // Without the manager running, a hidden window would be unreachable;
        // hand every managed window back to the shell visible.
        foreach (var handle in _managedHandles)
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);

        if (_winEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0)
            return;

        switch (eventType)
        {
            case Win32.EVENT_SYSTEM_FOREGROUND:
                _workspaces.SetFocusedWindow(hwnd.ToInt64());
                break;

            case Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_HIDE or Win32.EVENT_OBJECT_DESTROY:
                DebounceRefresh();
                break;

            case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                // Only managed windows matter; everything else moves freely.
                if (_managedHandles.Contains(hwnd.ToInt64()))
                    DebounceRefresh();
                break;
        }
    }

    private void ForgetDeadWindows()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            if (Win32.IsWindow(new IntPtr(handle)))
                continue;

            _workspaces.RemoveWindow(handle);
            _managedHandles.Remove(handle);
            _assignedRects.Remove(handle);
        }
    }

    /// <summary>
    /// Brings the monitor list in sync with the system and maintains the
    /// workspace-output bindings: workspaces of a disconnected display move to
    /// the primary display (the one that was active there stays visible), and
    /// a reconnecting display gets its remembered workspace back.
    /// </summary>
    private void ReconcileMonitors()
    {
        RestoreBindings();

        var current = new HashSet<IntPtr>();
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in MonitorInfo.Enumerate())
        {
            current.Add(info.Handle);
            devices.Add(info.DeviceName);
            // Replace the entry so work areas follow resolution changes.
            _monitors[info.Handle] = info;
        }

        foreach (var disconnected in _monitors.Keys.Where(h => !current.Contains(h)).ToList())
            _monitors.Remove(disconnected);

        var primary = PrimaryMonitor().DeviceName;

        foreach (var output in _workspaces.OutputBindings.Values.Distinct().ToList())
        {
            if (devices.Contains(output))
                continue;

            var orphanedActive = _workspaces.GetActiveIndex(output);
            foreach (var (index, binding) in _workspaces.OutputBindings)
            {
                if (string.Equals(binding, output, StringComparison.OrdinalIgnoreCase))
                    _workspaces.Bind(index, primary);
            }

            _workspaces.ClearActive(output);
            if (orphanedActive is int moved && _workspaces.TryGet(moved)?.OutputName == primary)
                _workspaces.SetActiveOn(primary, moved);
        }

        // A display that comes back shows the workspace it last had active.
        foreach (var device in devices)
        {
            if (_workspaces.GetActiveIndex(device) is not null)
                continue;

            if (_state?.ActiveWorkspaceByMonitor.TryGetValue(device, out var remembered) == true &&
                remembered is >= 1 and <= 9)
            {
                _workspaces.Bind(remembered, device);
                _workspaces.SetActiveOn(device, remembered);
            }
        }

        // Every connected display ends up with an active workspace.
        foreach (var device in devices)
            _workspaces.ActiveOn(device);
    }

    private void RestoreBindings()
    {
        if (_bindingsRestored)
            return;

        _bindingsRestored = true;
        if (_state is null)
            return;

        foreach (var (index, output) in _state.OutputByWorkspace)
            _workspaces.Bind(index, output);
    }

    /// <summary>
    /// Moves windows that were dragged to another display into the workspace
    /// that is active there.
    /// </summary>
    private void ReassignWindows()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            if (_workspaces.FindOwner(handle) is not { } workspace)
                continue;

            var hwnd = new IntPtr(handle);
            var device = MonitorFor(hwnd).DeviceName;
            if (string.Equals(workspace.OutputName, device, StringComparison.OrdinalIgnoreCase))
                continue;

            _workspaces.RemoveWindow(handle);
            _workspaces.AddWindow(handle, device);
            _assignedRects.Remove(handle);

            // The window joined an active workspace; it must not stay hidden.
            if (!Win32.IsWindowVisible(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        }
    }

    /// <summary>
    /// Reacts to windows the user moved or resized manually: a resize along
    /// the window's split axis is applied so sibling windows adapt, and
    /// everything else snaps back to the tiled rectangle.
    /// </summary>
    private void ReconcileUserWindowChanges()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            var hwnd = new IntPtr(handle);
            if (Win32.IsIconic(hwnd) || !Win32.IsWindowVisible(hwnd) ||
                !_assignedRects.TryGetValue(handle, out var assigned) ||
                !Win32.GetWindowRect(hwnd, out var current))
            {
                continue;
            }

            var rect = new Rect(
                current.Left, current.Top, current.Right - current.Left, current.Bottom - current.Top);
            if (rect == assigned)
                continue;

            var widthChanged = rect.Width != assigned.Width;
            var heightChanged = rect.Height != assigned.Height;

            var applied = (widthChanged, heightChanged) switch
            {
                (true, false) => _workspaces.ApplyUserResize(handle, ResizeAxis.Width, rect.Width),
                (false, true) => _workspaces.ApplyUserResize(handle, ResizeAxis.Height, rect.Height),
                _ => false,
            };

            if (!applied)
                PositionWindow(handle, assigned);
        }
    }

    private void DiscoverNewWindows()
    {
        Win32.EnumWindows((hwnd, _) =>
        {
            var handle = hwnd.ToInt64();
            if (_managedHandles.Contains(handle) || !Win32.IsWindow(hwnd))
                return true;

            // Visible windows join as usual. A hidden window is only adopted
            // when it exactly matches a remembered placement: that can only be
            // a window a previous instance parked on an inactive workspace and
            // never got to show again (e.g. after a crash).
            var visible = Win32.IsWindowVisible(hwnd);
            if (!IsTileable(hwnd, requireVisible: false))
                return true;

            var (exePath, title) = GetWindowIdentity(hwnd);
            var remembered = visible
                ? _state?.ResolveWorkspace(exePath, title)
                : _state?.ResolveWorkspaceExact(exePath, title);
            if (!visible && remembered is null)
                return true;

            var device = MonitorFor(hwnd).DeviceName;
            if (remembered is >= 1 and <= 9)
            {
                var workspace = _workspaces.GetOrCreate(remembered.Value, device);
                _workspaces.AddWindowTo(remembered.Value, handle, device);

                // The window belongs to its remembered workspace wherever
                // that workspace lives; it is visible only when the workspace
                // is active on its output.
                var activeOnOutput = _workspaces.GetActiveIndex(workspace.OutputName) == workspace.Index;
                if (activeOnOutput)
                {
                    if (!visible)
                        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
                }
                else if (visible)
                {
                    Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                }
            }
            else
            {
                _workspaces.AddWindow(handle, device);
            }

            _managedHandles.Add(handle);
            return true;
        }, IntPtr.Zero);
    }

    private void ShowAllWindows(int index)
    {
        if (_workspaces.TryGet(index) is not { } workspace)
            return;

        foreach (var handle in workspace.WindowHandles)
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);
    }

    /// <summary>
    /// Snapshots the current state: workspace-output bindings, the active
    /// workspace of each monitor and the workspace of every managed window,
    /// keyed by a persistent identity.
    /// </summary>
    private AppState CaptureState()
    {
        var state = new AppState
        {
            OutputByWorkspace = _workspaces.OutputBindings.ToDictionary(
                pair => pair.Key, pair => pair.Value),
        };

        foreach (var monitor in _monitors.Values)
            state.ActiveWorkspaceByMonitor[monitor.DeviceName] =
                _workspaces.ActiveOn(monitor.DeviceName).Index;

        foreach (var workspace in _workspaces.All)
        {
            foreach (var handle in workspace.WindowHandles)
            {
                var (exePath, title) = GetWindowIdentity(new IntPtr(handle));
                state.Windows.Add(new WindowState(exePath, title, workspace.Index));
            }
        }

        // Windows that were not seen again this session stay remembered, so
        // they are recognized whenever they reopen.
        state.MergeRemembered(_state);
        return state;
    }

    private void SaveState()
    {
        if (_monitors.Count == 0)
            return;

        _state = CaptureState();
        StateStore.Save(_state);
    }

    private void PositionWindow(long handle, Rect rect)
    {
        var hwnd = new IntPtr(handle);

        // A maximized or minimized window ignores SetWindowPos; restore first.
        if (Win32.IsIconic(hwnd) || Win32.IsZoomed(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// The monitor that hotkeys act on: the one showing the foreground window,
    /// falling back to the primary display.
    /// </summary>
    private MonitorInfo ActiveMonitor()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            var monitor = Win32.MonitorFromWindow(foreground, Win32.MONITOR_DEFAULTTONEAREST);
            if (_monitors.TryGetValue(monitor, out var info))
                return info;
        }

        return PrimaryMonitor();
    }

    private MonitorInfo MonitorFor(IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (_monitors.TryGetValue(monitor, out var info))
            return info;

        // The window sits on a monitor we have not synced yet (or none at
        // all, transiently, during a display reconfiguration).
        ReconcileMonitors();
        return _monitors.TryGetValue(monitor, out info) ? info : PrimaryMonitor();
    }

    /// <summary>
    /// The primary display, re-syncing the monitor list first when it is
    /// empty, e.g. in the middle of a display reconfiguration.
    /// </summary>
    private MonitorInfo PrimaryMonitor()
    {
        if (_monitors.Count == 0)
            ReconcileMonitors();

        return _monitors.Values.FirstOrDefault(m => m.IsPrimary)
            ?? _monitors.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No displays are connected.");
    }

    /// <summary>
    /// Whether a window should be tiled. When <paramref name="requireVisible"/>
    /// is false, hidden windows pass too; the caller must then place them
    /// on a remembered workspace only.
    /// </summary>
    private bool IsTileable(IntPtr hwnd, bool requireVisible)
    {
        if (!Win32.IsWindow(hwnd) || (requireVisible && !Win32.IsWindowVisible(hwnd)))
            return false;

        Win32.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == _ownProcessId)
            return false;

        var className = new StringBuilder(256);
        Win32.GetClassName(hwnd, className, className.Capacity);
        if (IgnoredClasses.Contains(className.ToString()))
            return false;

        if ((Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0)
            return false;

        var title = new StringBuilder(512);
        if (Win32.GetWindowText(hwnd, title, title.Capacity) == 0)
            return false;

        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
            return false;

        if (!Win32.GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return false;

        return true;
    }

    private static (string ExePath, string Title) GetWindowIdentity(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        Win32.GetWindowText(hwnd, title, title.Capacity);
        return (GetProcessImagePath(hwnd) ?? string.Empty, title.ToString());
    }

    private static string? GetProcessImagePath(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var processId);
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
            return null;

        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return Win32.QueryFullProcessImageName(process, 0, path, ref size)
                ? path.ToString()
                : null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}
