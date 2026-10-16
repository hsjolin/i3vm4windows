using System.Windows.Input;
using System.Windows.Interop;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// Owns a message-only window that receives WM_HOTKEY messages from
/// RegisterHotKey registrations.
/// </summary>
internal sealed class HotkeyHost : IDisposable
{
    // HWND_MESSAGE: message-only windows are invisible and skipped by EnumWindows.
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly List<(int Id, Win32.Modifiers Modifiers, uint VirtualKey)> _registrations = new();
    private HwndSource? _source;
    private Action<int>? _onHotkey;

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

    public bool Register(int id, Win32.Modifiers modifiers, Key key)
    {
        if (_source is null)
            throw new InvalidOperationException("Hotkey host has not been started.");

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!Win32.RegisterHotKey(_source.Handle, id, (uint)modifiers, virtualKey))
            return false;

        _registrations.Add((id, modifiers, virtualKey));
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

    public void Dispose()
    {
        if (_source is null)
            return;

        foreach (var (id, _, _) in _registrations)
            Win32.UnregisterHotKey(_source.Handle, id);
        _registrations.Clear();

        _source.Dispose();
        _source = null;
    }
}
