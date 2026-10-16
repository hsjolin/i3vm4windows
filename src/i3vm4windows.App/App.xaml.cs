using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using i3vm4windows.App.Interop;
using i3vm4windows.App.Services;
using i3vm4windows.Config;
using Binding = i3vm4windows.Config.Binding;

namespace i3vm4windows.App;

public partial class App : System.Windows.Application
{
    private readonly Dictionary<int, Binding> _bindings = new();
    private WindowManager? _windowManager;
    private HotkeyHost? _hotkeyHost;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = LoadConfig(e.Args);
        foreach (var warning in config.Warnings)
            Debug.WriteLine($"i3vm4windows configuration: {warning}");

        _windowManager = new WindowManager(config.Rules);
        _windowManager.Start();

        _hotkeyHost = new HotkeyHost();
        _hotkeyHost.Start(HandleHotkey);
        RegisterHotkeys(config);

        _trayIcon = CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _hotkeyHost?.Dispose();
        _windowManager?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// The configuration to use: the file named on the command line, else
    /// the i3-style default location in the user profile. When the file is
    /// missing or yields no usable bindings, the built-in defaults apply.
    /// </summary>
    private static I3Config LoadConfig(IReadOnlyList<string> args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "i3", "config");

        try
        {
            var config = I3ConfigParser.Parse(File.ReadAllText(path));
            if (config.Bindings.Count > 0)
                return config;

            Debug.WriteLine(
                $"i3vm4windows configuration: {path} has no usable bindings; using defaults.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"i3vm4windows configuration: cannot read {path}: {error.Message}");
        }

        return I3ConfigParser.Parse(I3Defaults.Text);
    }

    private void RegisterHotkeys(I3Config config)
    {
        if (_hotkeyHost is null)
            return;

        var nextId = 1;
        foreach (var binding in config.Bindings)
        {
            if (ToWpfKey(binding.Key) is not { } key)
                continue;

            var id = nextId++;
            // Config.Modifiers uses the Win32 MOD_* values.
            _hotkeyHost.Register(id, (Win32.Modifiers)binding.Modifiers, key);
            _bindings[id] = binding;
        }
    }

    private static Key? ToWpfKey(string name)
    {
        if (name.Length == 1)
        {
            if (char.IsAsciiDigit(name[0]))
                return Key.D0 + (name[0] - '0');
            if (char.IsAsciiLetter(name[0]))
                return Key.A + (char.ToUpperInvariant(name[0]) - 'A');
        }

        return name switch
        {
            "Left" => Key.Left,
            "Right" => Key.Right,
            "Up" => Key.Up,
            "Down" => Key.Down,
            "Enter" => Key.Enter,
            "Space" => Key.Space,
            "Tab" => Key.Tab,
            "Escape" => Key.Escape,
            _ => null,
        };
    }

    private void HandleHotkey(int id)
    {
        if (_windowManager is null || !_bindings.TryGetValue(id, out var binding))
            return;

        switch (binding.Action)
        {
            case ConfigAction.FocusPrevious:
                _windowManager.FocusRelative(-1);
                break;
            case ConfigAction.FocusNext:
                _windowManager.FocusRelative(1);
                break;
            case ConfigAction.MovePrevious:
                _windowManager.MoveFocused(-1);
                break;
            case ConfigAction.MoveNext:
                _windowManager.MoveFocused(1);
                break;
            case ConfigAction.SwitchWorkspace:
                _windowManager.SwitchWorkspace(binding.Parameter);
                break;
            case ConfigAction.MoveToWorkspace:
                _windowManager.MoveFocusedToWorkspace(binding.Parameter);
                break;
            case ConfigAction.ToggleOrientation:
                _windowManager.ToggleOrientation();
                break;
            case ConfigAction.CloseFocused:
                _windowManager.CloseFocused();
                break;
            case ConfigAction.Refresh:
                _windowManager.RefreshWindows();
                break;
            case ConfigAction.MoveWorkspaceToNextMonitor:
                _windowManager.MoveActiveWorkspaceToNextMonitor();
                break;
            case ConfigAction.Launch:
                LaunchProcess(binding.Command);
                break;
        }
    }

    /// <summary>
    /// Starts the bound program, like i3's exec: the first token names the
    /// program and the rest are its arguments; a quoted first token carries
    /// a path with spaces.
    /// </summary>
    private static void LaunchProcess(string command)
    {
        if (command is null)
            return;

        var trimmed = command.Trim();
        string fileName;
        string arguments;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0)
            {
                Debug.WriteLine($"i3vm4windows: unterminated program path in {command}");
                return;
            }

            fileName = trimmed[1..end];
            arguments = trimmed[(end + 1)..].Trim();
        }
        else
        {
            var split = trimmed.IndexOf(' ');
            fileName = split < 0 ? trimmed : trimmed[..split];
            arguments = split < 0 ? "" : trimmed[(split + 1)..].Trim();
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            Debug.WriteLine($"i3vm4windows: cannot start {fileName}: {error.Message}");
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
