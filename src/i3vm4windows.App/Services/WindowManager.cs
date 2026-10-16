using System.Text;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Core;
using Microsoft.Win32;

namespace i3vm4windows.App.Services;

/// <summary>
/// Tracks top-level windows across all connected monitors, tiling each on its
/// own work area with an independent set of workspaces per monitor, and
/// applies the computed layout via SetWindowPos.
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

    /// <summary>A connected display with its own independent set of workspaces.</summary>
    private sealed class MonitorState
    {
        public MonitorState(MonitorInfo info) => Info = info;

        public MonitorInfo Info { get; set; }

        public WorkspaceManager Workspaces { get; } = new();
    }

    private readonly Dictionary<IntPtr, MonitorState> _monitors = new();
    private readonly Dictionary<long, MonitorState> _windowOwners = new();
    private readonly LayoutOptions _options = new() { OuterMargin = 8, InnerGap = 8 };
    private readonly HashSet<long> _managedHandles = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly uint _ownProcessId;
    private readonly Win32.WinEventDelegate _winEventDelegate;
    private AppState? _state;
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
            Win32.EVENT_OBJECT_SHOW,
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

        if (!_refreshTimer.IsEnabled)
            _refreshTimer.Start();
    }

    public void RefreshWindows()
    {
        if (_disposed)
            return;

        ForgetDeadWindows();
        ReconcileMonitors();
        ReassignWindows();
        DiscoverNewWindows();
        ApplyLayout();
        SaveState();
    }

    public void ApplyLayout()
    {
        foreach (var state in _monitors.Values)
        {
            var assignments = state.Workspaces.ComputeActiveLayout(state.Info.WorkArea, _options);

            foreach (var (handle, rect) in assignments)
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
        }
    }

    public void FocusRelative(int offset)
    {
        var workspaces = ActiveMonitor().Workspaces;
        if (!workspaces.FocusRelative(offset))
            return;

        if (workspaces.Active.FocusedWindowHandle is { } handle)
            Win32.SetForegroundWindow(new IntPtr(handle));
    }

    public void MoveFocused(int offset)
    {
        if (ActiveMonitor().Workspaces.MoveFocused(offset))
            ApplyLayout();
    }

    public void ToggleOrientation()
    {
        ActiveMonitor().Workspaces.ToggleOrientation();
        ApplyLayout();
    }

    public void CloseFocused()
    {
        if (ActiveMonitor().Workspaces.Active.FocusedWindowHandle is { } handle)
            Win32.PostMessage(new IntPtr(handle), Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Activates the given workspace on the monitor that currently has focus:
    /// hides the windows of the current one, shows the windows of the target
    /// and re-applies the layout.
    /// </summary>
    public void SwitchWorkspace(int index)
    {
        var workspaces = ActiveMonitor().Workspaces;
        var previous = workspaces.ActiveIndex;
        if (!workspaces.SwitchTo(index))
            return;

        foreach (var handle in workspaces.Get(previous).WindowHandles)
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_HIDE);

        foreach (var handle in workspaces.Get(index).WindowHandles)
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);

        ApplyLayout();
        SaveState();

        if (workspaces.Active.FocusedWindowHandle is { } focused)
            Win32.SetForegroundWindow(new IntPtr(focused));
    }

    /// <summary>
    /// Moves the focused window to the given workspace on its own monitor
    /// and hides it there.
    /// </summary>
    public void MoveFocusedToWorkspace(int index)
    {
        var workspaces = ActiveMonitor().Workspaces;
        if (workspaces.MoveFocusedTo(index) is not { } moved)
            return;

        Win32.ShowWindow(new IntPtr(moved), Win32.SW_HIDE);

        if (workspaces.Active.FocusedWindowHandle is { } next)
            Win32.SetForegroundWindow(new IntPtr(next));

        ApplyLayout();
        SaveState();
    }

    /// <summary>
    /// Moves all windows of the active workspace on the focused monitor to
    /// the active workspace of the next monitor (ordered left to right,
    /// wrapping around) and shows them there.
    /// </summary>
    public void MoveActiveWorkspaceToNextMonitor()
    {
        var monitors = _monitors.Values
            .OrderBy(m => m.Info.WorkArea.X)
            .ThenBy(m => m.Info.WorkArea.Y)
            .ToList();
        if (monitors.Count < 2)
            return;

        var source = ActiveMonitor();
        var target = monitors[(monitors.IndexOf(source) + 1) % monitors.Count];

        var handles = source.Workspaces.Active.WindowHandles.ToList();
        if (handles.Count == 0)
            return;

        var focusedBefore = source.Workspaces.Active.FocusedWindowHandle;
        foreach (var handle in handles)
        {
            source.Workspaces.RemoveWindow(handle);
            target.Workspaces.AddWindow(handle);
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);
            _windowOwners[handle] = target;
        }

        // AddWindow re-points focus at each added window; keep the original one.
        if (focusedBefore is { } focused && handles.Contains(focused))
            target.Workspaces.Active.FocusedWindowHandle = focused;

        ApplyLayout();
        SaveState();

        if (target.Workspaces.Active.FocusedWindowHandle is { } next)
            Win32.SetForegroundWindow(new IntPtr(next));
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
                if (_windowOwners.TryGetValue(hwnd.ToInt64(), out var owner))
                    owner.Workspaces.SetFocusedWindow(hwnd.ToInt64());
                break;

            case Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_HIDE or Win32.EVENT_OBJECT_DESTROY:
                // Batch events instead of rescanning on every single one.
                if (!_refreshTimer.IsEnabled)
                    _refreshTimer.Start();
                break;
        }
    }

    private void ForgetDeadWindows()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            if (Win32.IsWindow(new IntPtr(handle)))
                continue;

            if (_windowOwners.TryGetValue(handle, out var owner))
            {
                owner.Workspaces.RemoveWindow(handle);
                _windowOwners.Remove(handle);
            }

            _managedHandles.Remove(handle);
        }
    }

    /// <summary>
    /// Brings the monitor list in sync with the system: adds states for new
    /// displays, refreshes work areas (resolution changes) and drops states
    /// for displays that were disconnected.
    /// </summary>
    private void ReconcileMonitors()
    {
        var current = new HashSet<IntPtr>();
        foreach (var info in MonitorInfo.Enumerate())
        {
            current.Add(info.Handle);
            if (_monitors.TryGetValue(info.Handle, out var state))
            {
                state.Info = info;
            }
            else
            {
                state = new MonitorState(info);
                if (_state?.ActiveWorkspaceByMonitor.TryGetValue(info.DeviceName, out var active) == true &&
                    active is >= 1 and <= 9)
                    state.Workspaces.SwitchTo(active);

                _monitors[info.Handle] = state;
            }
        }

        foreach (var disconnected in _monitors.Keys.Where(h => !current.Contains(h)).ToList())
            _monitors.Remove(disconnected);
    }

    /// <summary>
    /// Moves managed windows to the monitor they are currently on, e.g. after
    /// being dragged across screens or when their monitor was disconnected.
    /// A window that was hidden on its old monitor's inactive workspace joins
    /// the target's active workspace and becomes visible again.
    /// </summary>
    private void ReassignWindows()
    {
        foreach (var (handle, owner) in _windowOwners.ToList())
        {
            var hwnd = new IntPtr(handle);
            var target = MonitorStateForWindow(hwnd);
            if (target == owner)
                continue;

            owner.Workspaces.RemoveWindow(handle);
            target.Workspaces.AddWindow(handle);
            _windowOwners[handle] = target;

            if (!Win32.IsWindowVisible(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_SHOW);
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

            var state = MonitorStateForWindow(hwnd);
            if (remembered is >= 1 and <= 9)
            {
                state.Workspaces.AddWindowTo(remembered.Value, handle);
                if (state.Workspaces.ActiveIndex == remembered.Value)
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
                state.Workspaces.AddWindow(handle);
            }

            _managedHandles.Add(handle);
            _windowOwners[handle] = state;
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Snapshots the current state: active workspace per monitor and the
    /// workspace of every managed window, keyed by a persistent identity.
    /// </summary>
    private AppState CaptureState()
    {
        var state = new AppState();

        foreach (var monitor in _monitors.Values)
            state.ActiveWorkspaceByMonitor[monitor.Info.DeviceName] = monitor.Workspaces.ActiveIndex;

        foreach (var (handle, owner) in _windowOwners)
        {
            if (owner.Workspaces.FindOwner(handle) is not { } workspace ||
                !int.TryParse(workspace.Name, out var index))
                continue;

            var (exePath, title) = GetWindowIdentity(new IntPtr(handle));
            state.Windows.Add(new WindowState(exePath, title, index));
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

    /// <summary>
    /// The monitor that hotkeys act on: the one showing the foreground window,
    /// falling back to the primary display.
    /// </summary>
    private MonitorState ActiveMonitor()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            if (_windowOwners.TryGetValue(foreground.ToInt64(), out var owner))
                return owner;

            var monitor = Win32.MonitorFromWindow(foreground, Win32.MONITOR_DEFAULTTONEAREST);
            if (_monitors.TryGetValue(monitor, out var state))
                return state;
        }

        return PrimaryMonitor();
    }

    private MonitorState MonitorStateForWindow(IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (_monitors.TryGetValue(monitor, out var state))
            return state;

        // The window sits on a monitor we have not synced yet (or none at
        // all, transiently, during a display reconfiguration).
        ReconcileMonitors();
        return _monitors.TryGetValue(monitor, out state)
            ? state
            : PrimaryMonitor();
    }

    /// <summary>
    /// The primary display, re-syncing the monitor list first when it is
    /// empty, e.g. in the middle of a display reconfiguration.
    /// </summary>
    private MonitorState PrimaryMonitor()
    {
        if (_monitors.Count == 0)
            ReconcileMonitors();

        return _monitors.Values.FirstOrDefault(m => m.Info.IsPrimary)
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
}
