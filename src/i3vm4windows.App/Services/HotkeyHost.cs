using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// Owns a message-only window that receives WM_HOTKEY messages from
/// RegisterHotKey registrations, and a low-level keyboard hook on a
/// dedicated thread that gives the app full control of the Win key: while
/// it is held, known combinations dispatch their actions and everything
/// else is consumed, so shell shortcuts (Start menu, Win+L, Win+D, ...)
/// never leak through. RegisterHotKey remains as a fallback for the rare
/// case of the hook being removed after OS hook timeouts.
/// </summary>
internal sealed class HotkeyHost : IDisposable
{
    // HWND_MESSAGE: message-only windows are invisible and skipped by EnumWindows.
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly object _lock = new();
    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _registrations = new();
    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _hooked = new();
    /// <summary>The bindings of the active i3 mode, dispatched by the hook
    /// while the mode is active.</summary>
    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _modeBindings = new();
    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private Action<int>? _onHotkey;
    private Thread? _hookThread;
    private volatile uint _hookThreadId;

    // Used only on the hook thread.
    private Win32.LowLevelKeyboardProc? _hookProc;
    private IntPtr _llHook;
    // Written on the keyboard hook thread, read by the mouse dragger's
    // hook thread: volatile, or a weak memory model can read it stale.
    private volatile bool _winKeyDown;
    private bool _shiftKeyDown;
    private bool _controlKeyDown;
    private bool _altKeyDown;
    private long _modifierDownAt;

    /// <summary>
    /// Safety valve for a modifier release the hook never saw (e.g. a
    /// session switch mid-press): nobody holds a modifier this long
    /// physically.
    /// </summary>
    private const long ModifierHoldTimeoutMs = 20_000;

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

        // The hook owns the Win key even when RegisterHotKey succeeds for
        // every combination.
        EnsureHook();
    }

    /// <summary>Whether the physical Win key is currently held, as the hook
    /// tracks it — the mouse dragger asks, since only this app sees the
    /// key at all.</summary>
    public bool WinKeyDown => _winKeyDown;

    /// <summary>
    /// Registers a hotkey, falling back to the low-level keyboard hook when
    /// another process already owns the combination.
    /// </summary>
    public bool Register(int id, Win32.Modifiers modifiers, Key key)
    {
        if (_source is null)
            throw new InvalidOperationException("Hotkey host has not been started.");

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        lock (_lock)
        {
            if (Win32.RegisterHotKey(_source.Handle, id, (uint)modifiers, virtualKey))
                _registrations.Add((id, modifiers, virtualKey));
            else
                _hooked.Add((id, modifiers, virtualKey));
        }

        return true;
    }

    /// <summary>
    /// Activates the bindings of an i3 binding mode: while the list is not
    /// empty, matched keys dispatch with or without the Win key and shadow
    /// the default bindings. An empty list returns to the default mode.
    /// </summary>
    public void SetModeBindings(IReadOnlyList<(int Id, Win32.Modifiers Modifiers, Key Key)> bindings)
    {
        lock (_lock)
        {
            _modeBindings.Clear();
            foreach (var (id, modifiers, key) in bindings)
                _modeBindings.Add((id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
        }
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
            var keyDown = message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            var keyUp = message is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

            // Safety valve for a stuck modifier state.
            if (Environment.TickCount64 - _modifierDownAt > ModifierHoldTimeoutMs)
            {
                _winKeyDown = false;
                _shiftKeyDown = false;
                _controlKeyDown = false;
                _altKeyDown = false;
            }

            if (data.vkCode is Win32.VK_LWIN or Win32.VK_RWIN)
            {
                // The app owns the Win key: the system never sees it at all,
                // so the Start menu cannot open and its key state cannot get
                // stuck half-pressed.
                if (keyDown)
                {
                    _winKeyDown = true;
                    _modifierDownAt = Environment.TickCount64;
                }
                else if (keyUp)
                {
                    _winKeyDown = false;
                }

                return new IntPtr(1);
            }

            // The low-level hook reports the physical modifier keys under
            // their left/right codes (VK_LSHIFT, ...), not the aggregate
            // VK_SHIFT; both forms are tracked and allowed through, because
            // modifier state read from the system can disagree with what the
            // user actually holds while the hook owns the Win key.
            if (IsModifier(data.vkCode))
            {
                if (keyDown)
                {
                    TrackModifier(data.vkCode);
                    _modifierDownAt = Environment.TickCount64;
                }
                else if (keyUp)
                {
                    ClearModifier(data.vkCode);
                }

                return Win32.CallNextHookEx(_llHook, nCode, wParam, lParam);
            }

            if (ModeActive())
            {
                // A binding mode (i3's mode command) owns its keys while it
                // is active: matched keys dispatch and are consumed, and
                // unmatched ones still reach the focused window unless the
                // Win key is held.
                if (keyDown && MatchMode(data.vkCode) is { } modeId)
                {
                    _dispatcher?.BeginInvoke(() => _onHotkey?.Invoke(modeId));
                    return new IntPtr(1);
                }

                if (_winKeyDown)
                    return new IntPtr(1);

                return Win32.CallNextHookEx(_llHook, nCode, wParam, lParam);
            }

            if (_winKeyDown)
            {
                // Every key pressed while Win is held belongs to the app:
                // known combinations dispatch their action, anything else is
                // consumed so no shell shortcut leaks through.
                if (keyDown && MatchAny(data.vkCode) is { } id)
                    _dispatcher?.BeginInvoke(() => _onHotkey?.Invoke(id));

                return new IntPtr(1);
            }
        }

        return Win32.CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private bool ModeActive()
    {
        lock (_lock)
            return _modeBindings.Count > 0;
    }

    /// <summary>
    /// Finds the mode binding whose modifiers are exactly the ones held,
    /// with or without the Win key.
    /// </summary>
    private int? MatchMode(uint virtualKey)
    {
        var held = HeldModifiers();
        lock (_lock)
        {
            foreach (var (id, modifiers, vk) in _modeBindings)
            {
                if (vk == virtualKey && held == modifiers)
                    return id;
            }
        }

        return null;
    }

    /// <summary>Finds the combination whose modifiers are exactly the ones
    /// currently held, e.g. Win+Shift+1 instead of Win+1.</summary>
    private int? MatchAny(uint virtualKey)
    {
        var held = HeldModifiers();
        lock (_lock)
        {
            foreach (var (id, modifiers, vk) in _registrations.Concat(_hooked))
            {
                if (vk == virtualKey && held == modifiers)
                    return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the event belongs to a modifier key. The low-level hook
    /// delivers physical modifiers as their left/right variants, so both
    /// forms count.
    /// </summary>
    private static bool IsModifier(uint virtualKey) =>
        virtualKey is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
            or Win32.VK_LSHIFT or Win32.VK_RSHIFT
            or Win32.VK_LCONTROL or Win32.VK_RCONTROL
            or Win32.VK_LMENU or Win32.VK_RMENU;

    private void TrackModifier(uint virtualKey)
    {
        switch (virtualKey)
        {
            case Win32.VK_SHIFT or Win32.VK_LSHIFT or Win32.VK_RSHIFT:
                _shiftKeyDown = true;
                break;
            case Win32.VK_CONTROL or Win32.VK_LCONTROL or Win32.VK_RCONTROL:
                _controlKeyDown = true;
                break;
            case Win32.VK_MENU or Win32.VK_LMENU or Win32.VK_RMENU:
                _altKeyDown = true;
                break;
        }
    }

    private void ClearModifier(uint virtualKey)
    {
        switch (virtualKey)
        {
            case Win32.VK_SHIFT or Win32.VK_LSHIFT or Win32.VK_RSHIFT:
                _shiftKeyDown = false;
                break;
            case Win32.VK_CONTROL or Win32.VK_LCONTROL or Win32.VK_RCONTROL:
                _controlKeyDown = false;
                break;
            case Win32.VK_MENU or Win32.VK_LMENU or Win32.VK_RMENU:
                _altKeyDown = false;
                break;
        }
    }

    /// <summary>The modifier state as seen by the hook: the Win key never
    /// reaches the system, so all modifier state comes from the hook's own
    /// tracking.</summary>
    private Win32.Modifiers HeldModifiers()
    {
        var modifiers = Win32.Modifiers.None;
        if (_winKeyDown)
            modifiers |= Win32.Modifiers.Win;
        if (_shiftKeyDown)
            modifiers |= Win32.Modifiers.Shift;
        if (_controlKeyDown)
            modifiers |= Win32.Modifiers.Control;
        if (_altKeyDown)
            modifiers |= Win32.Modifiers.Alt;
        return modifiers;
    }

    /// <summary>
    /// Drops every registration, as a configuration reload needs: the Win32
    /// hotkeys are unregistered and the hook matches nothing until the
    /// caller registers the new set.
    /// </summary>
    public void UnregisterAll()
    {
        if (_source is null)
            return;

        lock (_lock)
        {
            foreach (var (id, _, _) in _registrations)
                Win32.UnregisterHotKey(_source.Handle, id);
            _registrations.Clear();
            _hooked.Clear();
        }
    }

    public void Dispose()
    {
        if (_source is null)
            return;

        UnregisterAll();

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
