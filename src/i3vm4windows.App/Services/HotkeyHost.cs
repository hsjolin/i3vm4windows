using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// Owns a message-only window that receives WM_HOTKEY messages from
/// RegisterHotKey registrations. Combinations that are already owned by
/// another process (typically the Windows shell, e.g. Win+number taskbar
/// activation) are intercepted with a low-level keyboard hook on a
/// dedicated thread, so slow work on the UI thread can never delay key
/// interception past the OS hook timeout.
/// </summary>
internal sealed class HotkeyHost : IDisposable
{
    // HWND_MESSAGE: message-only windows are invisible and skipped by EnumWindows.
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly object _lock = new();
    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _registrations = new();
    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _hooked = new();
    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private Action<int>? _onHotkey;
    private Thread? _hookThread;
    private volatile uint _hookThreadId;

    // Used only on the hook thread.
    private Win32.LowLevelKeyboardProc? _hookProc;
    private IntPtr _llHook;
    private bool _winComboConsumed;

    public void Start(Action<int> onHotkey)
    {
        if (_source is not null)
            throw new InvalidOperationException("Hotkey host already started.");

        var parameters = new HwndSourceParameters("i3vm4windowsHotkeyHost", 0, 0)
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _dispatcher = _source.Dispatcher;
        _onHotkey = onHotkey;
    }

    /// <summary>
    /// Registers a hotkey, falling back to a low-level keyboard hook when
    /// another process already owns the combination.
    /// </summary>
    public bool Register(int id, Win32.Modifiers modifiers, Key key)
    {
        if (_source is null)
            throw new InvalidOperationException("Hotkey host has not been started.");

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (Win32.RegisterHotKey(_source.Handle, id, (uint)modifiers, virtualKey))
        {
            _registrations.Add((id, modifiers, virtualKey));
            return true;
        }

        lock (_lock)
        {
            _hooked.Add((id, modifiers, virtualKey));
        }

        EnsureHook();
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            _onHotkey?.Invoke(wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void EnsureHook()
    {
        if (_hookThread?.IsAlive == true)
            return;

        _hookThread = new Thread(RunHook)
        {
            IsBackground = true,
            Name = "i3vm4windows.HotkeyHook",
        };
        _hookThread.Start();
    }

    /// <summary>
    /// The low-level hook needs a message pump on its installing thread; a
    /// busy UI thread would let key presses through whenever it cannot
    /// service the hook fast enough.
    /// </summary>
    private void RunHook()
    {
        _hookThreadId = (uint)Environment.CurrentManagedThreadId;
        _hookProc = LowLevelKeyboardHook;
        _llHook = Win32.SetWindowsHookEx(
            Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandle(null), 0);

        while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
            // The pump only keeps the hook alive; hotkeys dispatch from the
            // hook callback.
        }

        if (_llHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
        }
    }

    private IntPtr LowLevelKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == 0)
        {
            var data = System.Runtime.InteropServices.Marshal
                .PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            var message = wParam.ToInt64();

            if (message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN &&
                MatchHooked(data.vkCode) is { } id)
            {
                // Swallow the Win key release too, or the Start menu opens.
                _winComboConsumed = HeldModifiers().HasFlag(Win32.Modifiers.Win);
                _dispatcher?.BeginInvoke(() => _onHotkey?.Invoke(id));
                return new IntPtr(1);
            }

            if (message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP &&
                _winComboConsumed &&
                (data.vkCode is Win32.VK_LWIN or Win32.VK_RWIN))
            {
                _winComboConsumed = false;
                return new IntPtr(1);
            }
        }

        return Win32.CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    /// <summary>Finds the hooked combination whose modifiers are exactly the
    /// ones currently held, e.g. Win+Shift+1 instead of Win+1.</summary>
    private int? MatchHooked(uint virtualKey)
    {
        var held = HeldModifiers();
        lock (_lock)
        {
            foreach (var (id, modifiers, vk) in _hooked)
            {
                if (vk == virtualKey && held == modifiers)
                    return id;
            }
        }

        return null;
    }

    private static Win32.Modifiers HeldModifiers()
    {
        var modifiers = Win32.Modifiers.None;
        if (IsDown(Win32.VK_LWIN) || IsDown(Win32.VK_RWIN))
            modifiers |= Win32.Modifiers.Win;
        if (IsDown(Win32.VK_SHIFT))
            modifiers |= Win32.Modifiers.Shift;
        if (IsDown(Win32.VK_CONTROL))
            modifiers |= Win32.Modifiers.Control;
        if (IsDown(Win32.VK_MENU))
            modifiers |= Win32.Modifiers.Alt;
        return modifiers;
    }

    private static bool IsDown(int virtualKey) =>
        (Win32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_source is null)
            return;

        foreach (var (id, _, _) in _registrations)
            Win32.UnregisterHotKey(_source.Handle, id);
        _registrations.Clear();

        lock (_lock)
        {
            _hooked.Clear();
        }

        StopHookThread();

        _source.Dispose();
        _source = null;
    }

    private void StopHookThread()
    {
        var thread = _hookThread;
        if (thread is null)
            return;

        // The thread id is published right before the hook is installed.
        for (var attempt = 0; attempt < 20 && _hookThreadId == 0; attempt++)
            Thread.Sleep(50);

        if (_hookThreadId != 0)
            Win32.PostThreadMessage(_hookThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        thread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
        _hookThreadId = 0;
    }
}
