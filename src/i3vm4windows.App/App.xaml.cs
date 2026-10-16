using System.Windows;
using System.Windows.Input;
using i3vm4windows.App.Interop;
using i3vm4windows.App.Services;

namespace i3vm4windows.App;

public partial class App : System.Windows.Application
{
    private const int HotkeyFocusPrevious = 1;
    private const int HotkeyFocusNext = 2;
    private const int HotkeyMovePrevious = 3;
    private const int HotkeyMoveNext = 4;
    private const int HotkeyToggleOrientation = 5;
    private const int HotkeyCloseFocused = 6;
    private const int HotkeyRefresh = 7;

    private WindowManager? _windowManager;
    private HotkeyHost? _hotkeyHost;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _windowManager = new WindowManager();
        _windowManager.Start();

        _hotkeyHost = new HotkeyHost();
        _hotkeyHost.Start(HandleHotkey);
        RegisterHotkeys();

        _trayIcon = CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _hotkeyHost?.Dispose();
        _windowManager?.Dispose();

        base.OnExit(e);
    }

    private void RegisterHotkeys()
    {
        if (_hotkeyHost is null)
            return;

        _hotkeyHost.Register(HotkeyFocusPrevious, Win32.Modifiers.Win, Key.Left);
        _hotkeyHost.Register(HotkeyFocusNext, Win32.Modifiers.Win, Key.Right);
        _hotkeyHost.Register(HotkeyMovePrevious, Win32.Modifiers.Win | Win32.Modifiers.Shift, Key.Left);
        _hotkeyHost.Register(HotkeyMoveNext, Win32.Modifiers.Win | Win32.Modifiers.Shift, Key.Right);
        _hotkeyHost.Register(HotkeyToggleOrientation, Win32.Modifiers.Win, Key.Enter);
        _hotkeyHost.Register(HotkeyCloseFocused, Win32.Modifiers.Win, Key.Q);
        _hotkeyHost.Register(HotkeyRefresh, Win32.Modifiers.Win, Key.Y);
    }

    private void HandleHotkey(int id)
    {
        if (_windowManager is null)
            return;

        switch (id)
        {
            case HotkeyFocusPrevious:
                _windowManager.FocusRelative(-1);
                break;
            case HotkeyFocusNext:
                _windowManager.FocusRelative(1);
                break;
            case HotkeyMovePrevious:
                _windowManager.MoveFocused(-1);
                break;
            case HotkeyMoveNext:
                _windowManager.MoveFocused(1);
                break;
            case HotkeyToggleOrientation:
                _windowManager.ToggleOrientation();
                break;
            case HotkeyCloseFocused:
                _windowManager.CloseFocused();
                break;
            case HotkeyRefresh:
                _windowManager.RefreshWindows();
                break;
        }
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Exit i3vm4windows", null, (_, _) => Shutdown());

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "i3vm4windows",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };

        return icon;
    }
}
