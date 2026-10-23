using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// Moves and resizes windows with the mouse while the Win key is held,
/// AltSnap-style but for the key this app owns: a left drag moves the
/// window under the pointer (a tiled one is released from the tiling
/// first, free to be tiled back with floating toggle), a right drag
/// resizes it by the corner nearest the click. A click without dragging
/// is replayed to the window, so Win+click behaves normally. The desktop,
/// the taskbar and this app's own windows are left alone.
/// </summary>
/// <remarks>
/// The hook thread only notes the pointer and consumes the events: a
/// low-level mouse hook must never wait on another process, and
/// SetWindowPos does. All positioning runs on the dispatcher, where the
/// drag start's release from the tiling is queued before every move.
/// </remarks>
internal sealed class WinDragger : IDisposable
{
    /// <summary>The movement below which a press counts as a plain click.</summary>
    private const int ClickSlop = 4;
    private const int MinExtent = 32;

    private readonly Func<bool> _winKeyDown;
    private readonly Action<IntPtr> _releaseFromTiling;
    private readonly Dispatcher _dispatcher;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private Thread? _thread;
    private volatile uint _hookThreadId;
    private Win32.LowLevelMouseProc? _proc;
    private IntPtr _hook;
    private bool _disposed;

    // Drag state; the window handle and pointer are written on the hook
    // thread and read on the dispatcher, every other field lives on the
    // dispatcher.
    private volatile IntPtr _window;
    private volatile int _pointerX;
    private volatile int _pointerY;
    // Written on the dispatcher, read on the hook thread: volatile, or a
    // weak memory model can keep reading a stale queued flag and stop
    // queueing moves.
    private volatile bool _moveQueued;
    private bool _resizing;
    private bool _moved;
    private int _originX;
    private int _originY;
    private Win32.RECT _start;
    private bool _leftEdge;
    private bool _topEdge;

    /// <summary>
    /// Windows that must not be dragged: the shell surfaces, which would
    /// swallow the clicks without moving.
    /// </summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    public WinDragger(
        Func<bool> winKeyDown,
        Action<IntPtr> releaseFromTiling,
        Dispatcher dispatcher)
    {
        _winKeyDown = winKeyDown;
        _releaseFromTiling = releaseFromTiling;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_thread is not null)
            return;

        _thread = new Thread(RunHook)
        {
            IsBackground = true,
            Name = "i3vm4windows.WinDragger",
        };
        _thread.Start();
    }

    /// <summary>
    /// A low-level mouse hook needs a message pump on its installing
    /// thread, like the keyboard hook does.
    /// </summary>
    private void RunHook()
    {
        _hookThreadId = (uint)Environment.CurrentManagedThreadId;
        _proc = MouseHook;
        _hook = Win32.SetWindowsHookEx(
            Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);

        while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // The pump only keeps the hook alive.
        }

        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0)
        {
            var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            var message = (uint)wParam.ToInt64();

            // Replayed clicks — ours or other programs' — pass through,
            // or the replayed click would loop straight back to us.
            if ((data.flags & Win32.LLKHF_INJECTED) != 0)
                return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

            if (_winKeyDown())
            {
                switch (message)
                {
                    case Win32.WM_LBUTTONDOWN:
                    case Win32.WM_RBUTTONDOWN:
                        var target = DragTarget(data.pt.X, data.pt.Y);
                        if (target == IntPtr.Zero)
                            break;

                        _window = target;
                        _resizing = message == Win32.WM_RBUTTONDOWN;
                        _moved = false;
                        _moveQueued = false;
                        _pointerX = _originX = data.pt.X;
                        _pointerY = _originY = data.pt.Y;
                        _dispatcher.BeginInvoke(() => BeginDrag(target));
                        return new IntPtr(1);

                    case Win32.WM_MOUSEMOVE:
                        if (_window == IntPtr.Zero)
                            break;

                        // Moves pass through: consuming them freezes the
                        // pointer on screen, and the window under it sees
                        // harmless hover moves — it never saw the button.
                        _pointerX = data.pt.X;
                        _pointerY = data.pt.Y;
                        QueueMove();
                        break;

                    case Win32.WM_LBUTTONUP:
                    case Win32.WM_RBUTTONUP:
                        if (_window == IntPtr.Zero)
                            break;

                        _window = IntPtr.Zero;
                        _dispatcher.BeginInvoke(() =>
                            EndDrag(replayRight: message == Win32.WM_RBUTTONUP));
                        return new IntPtr(1);
                }
            }
            else if (_window != IntPtr.Zero)
            {
                // The Win key was released mid-drag: end the drag and
                // consume the orphaned button event, since the window
                // never saw the press.
                _window = IntPtr.Zero;
                AppLog.Info("drag: the Win key was released mid-drag");
                return new IntPtr(1);
            }
        }

        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// The window the drag would move, or zero when the click belongs to
    /// the desktop, the taskbar, this app or nothing at all.
    /// </summary>
    private IntPtr DragTarget(int x, int y)
    {
        var target = Win32.WindowFromPoint(new Win32.POINT { X = x, Y = y });
        if (target != IntPtr.Zero)
            target = Win32.GetAncestor(target, Win32.GA_ROOT);
        if (target == IntPtr.Zero)
            return IntPtr.Zero;

        Win32.GetWindowThreadProcessId(target, out var processId);
        if (processId == _ownProcessId)
            return IntPtr.Zero;

        var className = new StringBuilder(256);
        Win32.GetClassName(target, className, className.Capacity);
        if (IgnoredClasses.Contains(className.ToString()))
            return IntPtr.Zero;

        return target;
    }

    private void QueueMove()
    {
        if (_moveQueued)
            return;

        _moveQueued = true;
        _dispatcher.BeginInvoke(() =>
        {
            _moveQueued = false;
            ApplyMove();
        });
    }

    /// <summary>Everything the drag start needs but the hook must not wait
    /// for: restoring a maximized window, the starting rectangle and the
    /// release from the tiling, queued before every move.</summary>
    private void BeginDrag(IntPtr target)
    {
        if (Win32.IsZoomed(target))
            Win32.ShowWindow(target, Win32.SW_RESTORE);
        if (!Win32.GetWindowRect(target, out _start))
            return;

        // Resizing follows the corner nearest the click.
        _leftEdge = _originX - _start.Left < _start.Right - _originX;
        _topEdge = _originY - _start.Top < _start.Bottom - _originY;

        _releaseFromTiling(target);

        var className = new StringBuilder(256);
        Win32.GetClassName(target, className, className.Capacity);
        AppLog.Info($"drag: {(_resizing ? "resizing" : "moving")} {className}");
    }

    private void ApplyMove()
    {
        var x = _pointerX;
        var y = _pointerY;
        var dx = x - _originX;
        var dy = y - _originY;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) <= ClickSlop)
            return;

        _moved = true;
        var left = _start.Left;
        var top = _start.Top;
        var right = _start.Right;
        var bottom = _start.Bottom;
        if (_resizing)
        {
            if (_leftEdge)
                left = Math.Min(_start.Left + dx, right - MinExtent);
            else
                right = Math.Max(_start.Right + dx, left + MinExtent);
            if (_topEdge)
                top = Math.Min(_start.Top + dy, bottom - MinExtent);
            else
                bottom = Math.Max(_start.Bottom + dy, top + MinExtent);
        }
        else
        {
            left += dx;
            top += dy;
            right += dx;
            bottom += dy;
        }

        Win32.SetWindowPos(
            _window, IntPtr.Zero, left, top, right - left, bottom - top,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private void EndDrag(bool replayRight)
    {
        if (_moved)
        {
            AppLog.Info($"drag: moved by " +
                $"({_pointerX - _originX}, {_pointerY - _originY})");
            return;
        }

        // A click without a drag reaches the window like it would have
        // without the Win key.
        InjectClick(replayRight);
    }

    /// <summary>Replays the swallowed click at the current pointer position.</summary>
    private static void InjectClick(bool right)
    {
        var down = right ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_LEFTDOWN;
        var up = right ? Win32.MOUSEEVENTF_RIGHTUP : Win32.MOUSEEVENTF_LEFTUP;
        Win32.SendInput(
            2,
            new[]
            {
                new Win32.INPUT { type = Win32.INPUT_MOUSE, mi = new Win32.MOUSEINPUT { dwFlags = down } },
                new Win32.INPUT { type = Win32.INPUT_MOUSE, mi = new Win32.MOUSEINPUT { dwFlags = up } },
            },
            Marshal.SizeOf<Win32.INPUT>());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hookThreadId != 0)
            Win32.PostThreadMessage(_hookThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
