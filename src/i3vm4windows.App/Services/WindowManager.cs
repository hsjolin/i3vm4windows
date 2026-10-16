using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Core;

namespace i3vm4windows.App.Services;

/// <summary>
/// Tracks top-level windows on the primary monitor, keeps them in an i3-style
/// layout tree and applies the computed layout via SetWindowPos.
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

    private readonly Workspace _workspace = new("1");
    private readonly TilingEngine _engine = new();
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
        DiscoverNewWindows();
        ApplyLayout();
    }

    public void ApplyLayout()
    {
        var monitor = GetPrimaryWorkArea();
        var assignments = _engine.ComputeLayout(_workspace, monitor, _options);

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

    public void FocusRelative(int offset)
    {
        if (!_engine.FocusRelative(_workspace, offset))
            return;

        if (_workspace.FocusedWindowHandle is { } handle)
            Win32.SetForegroundWindow(new IntPtr(handle));
    }

    public void MoveFocused(int offset)
    {
        if (_engine.MoveFocused(_workspace, offset))
            ApplyLayout();
    }

    public void ToggleOrientation()
    {
        _engine.ToggleOrientation(_workspace);
        ApplyLayout();
    }

    public void CloseFocused()
    {
        if (_workspace.FocusedWindowHandle is { } handle)
            Win32.PostMessage(new IntPtr(handle), Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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
                if (_managedHandles.Contains(hwnd.ToInt64()))
                    _workspace.FocusedWindowHandle = hwnd.ToInt64();
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

            _engine.RemoveWindow(_workspace, handle);
            _managedHandles.Remove(handle);
        }
    }

    private void DiscoverNewWindows()
    {
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!_managedHandles.Contains(hwnd.ToInt64()) && IsTileable(hwnd))
            {
                var handle = hwnd.ToInt64();
                _engine.AddWindow(_workspace, handle);
                _managedHandles.Add(handle);
            }
            return true;
        }, IntPtr.Zero);
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

        return IntersectsPrimaryMonitor(rect);
    }

    private static bool IntersectsPrimaryMonitor(Win32.RECT rect)
    {
        var monitor = GetPrimaryMonitorInfo();
        return rect.Right > monitor.rcWork.Left &&
               rect.Left < monitor.rcWork.Right &&
               rect.Bottom > monitor.rcWork.Top &&
               rect.Top < monitor.rcWork.Bottom;
    }

    private static Rect GetPrimaryWorkArea()
    {
        var info = GetPrimaryMonitorInfo();
        return new Rect(
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left,
            info.rcWork.Bottom - info.rcWork.Top);
    }

    private static Win32.MONITORINFO GetPrimaryMonitorInfo()
    {
        var info = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        // MonitorFromWindow(NULL, MONITOR_DEFAULTTOPRIMARY) yields the primary monitor.
        var monitor = Win32.MonitorFromWindow(IntPtr.Zero, Win32.MONITOR_DEFAULTTOPRIMARY);
        Win32.GetMonitorInfo(monitor, ref info);
        return info;
    }
}
