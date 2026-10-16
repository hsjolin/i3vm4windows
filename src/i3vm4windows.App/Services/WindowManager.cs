using System.Text;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Core;

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
    private IntPtr _winEventHook;
    private bool _disposed;

    public WindowManager()
    {
        _ownProcessId = (uint)Environment.ProcessId;
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

        RefreshWindows();
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
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshTimer.Stop();

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
                state.Info = info;
            else
                _monitors[info.Handle] = new MonitorState(info);
        }

        foreach (var disconnected in _monitors.Keys.Where(h => !current.Contains(h)).ToList())
            _monitors.Remove(disconnected);
    }

    /// <summary>
    /// Moves managed windows to the monitor they are currently on, e.g. after
    /// being dragged across screens or when their monitor was disconnected.
    /// </summary>
    private void ReassignWindows()
    {
        foreach (var (handle, owner) in _windowOwners.ToList())
        {
            var target = MonitorStateForWindow(new IntPtr(handle));
            if (target == owner)
                continue;

            owner.Workspaces.RemoveWindow(handle);
            target.Workspaces.AddWindow(handle);
            _windowOwners[handle] = target;
        }
    }

    private void DiscoverNewWindows()
    {
        Win32.EnumWindows((hwnd, _) =>
        {
            var handle = hwnd.ToInt64();
            if (!_managedHandles.Contains(handle) && IsTileable(hwnd))
            {
                var state = MonitorStateForWindow(hwnd);
                state.Workspaces.AddWindow(handle);
                _managedHandles.Add(handle);
                _windowOwners[handle] = state;
            }

            return true;
        }, IntPtr.Zero);
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

        return _monitors.Values.FirstOrDefault(m => m.Info.IsPrimary)
            ?? _monitors.Values.FirstOrDefault()
            ?? new MonitorState(MonitorInfo.Enumerate()[0]);
    }

    private MonitorState MonitorStateForWindow(IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        return _monitors.TryGetValue(monitor, out var state)
            ? state
            : _monitors.Values.FirstOrDefault() ?? new MonitorState(MonitorInfo.Enumerate()[0]);
    }

    private bool IsTileable(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd))
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
