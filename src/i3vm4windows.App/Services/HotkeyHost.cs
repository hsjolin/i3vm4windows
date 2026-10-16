using System.Windows.Input;
using System.Windows.Interop;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// Owns a message-only window that receives WM_HOTKEY messages from
/// RegisterHotKey registrations. Combinations that are already owned by
/// another process (typically the Windows shell, e.g. Win+number taskbar
/// activation) are intercepted with a low-level keyboard hook instead, so
/// every binding works no matter who registered first.
/// </summary>
internal sealed class HotkeyHost : IDisposable
{
    // HWND_MESSAGE: message-only windows are invisible and skipped by EnumWindows.
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _registrations = new();
    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _hooked = new();
    private HwndSource? _source;
    private Action<int>? _onHotkey;
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

        _hooked.Add((id, modifiers, virtualKey));
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
        if (_llHook != IntPtr.Zero || _hooked.Count == 0)
            return;

        _hookProc = LowLevelKeyboardHook;
        _llHook = Win32.SetWindowsHookEx(
            Win32.WH_KEYBOARD_LL, _hookProc, Win32.GetModuleHandle(null), 0);
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
                _source?.Dispatcher.BeginInvoke(() => _onHotkey?.Invoke(id));
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
        foreach (var (id, modifiers, vk) in _hooked)
        {
            if (vk == virtualKey && held == modifiers)
                return id;
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
        _hooked.Clear();

        if (_llHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
            _hookProc = null;
        }

        _source.Dispose();
        _source = null;
    }
}
