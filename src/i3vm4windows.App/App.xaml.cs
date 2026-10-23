using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using i3vm4windows.App.Interop;
using i3vm4windows.App.Services;
using i3vm4windows.Config;
using i3vm4windows.Core;
using Binding = i3vm4windows.Config.Binding;

namespace i3vm4windows.App;

public partial class App : System.Windows.Application
{
    private readonly Dictionary<int, Binding> _bindings = new();
    /// <summary>The bindings of each mode except the default one, by mode
    /// name; the hook activates them while the mode is active.</summary>
    private readonly Dictionary<string, List<(int Id, Binding Binding)>> _modes = new();
    private string _configPath = "";
    private WindowManager? _windowManager;
    private HotkeyHost? _hotkeyHost;
    private WinDragger? _dragger;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Session();

        _configPath = e.Args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "i3", "config");
        AppLog.Info($"configuration: {_configPath}");

        var config = LoadConfig(_configPath);
        foreach (var warning in config.Warnings)
            AppLog.Warn($"configuration: {warning}");
        AppLog.Info($"configuration: {config.Bindings.Count} bindings, " +
            $"{config.Rules.Count} rules, {config.Exec.Count} autostarts");

        _windowManager = new WindowManager(config.Rules);
        _windowManager.Start();

        _hotkeyHost = new HotkeyHost();
        _hotkeyHost.Start(HandleHotkey);
        RegisterHotkeys(config);

        // Win + mouse drag moves and resizes windows; only this app can
        // see the Win key, so it owns the whole gesture.
        _dragger = new WinDragger(
            () => _hotkeyHost.WinKeyDown,
            hwnd => _windowManager?.ReleaseForDragging(hwnd),
            Dispatcher);
        _dragger.Start();

        // i3's autostart: exec directives run when the app starts with
        // this configuration; a reload does not run them again.
        foreach (var command in config.Exec)
            LaunchProcess(command);

        _trayIcon = CreateTrayIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _dragger?.Dispose();
        _hotkeyHost?.Dispose();
        _windowManager?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// The configuration to use: the file named on the command line, else
    /// the i3-style default location in the user profile. When the file is
    /// missing or yields no usable bindings, the built-in defaults apply.
    /// </summary>
    private static I3Config LoadConfig(string path) =>
        TryReadConfig(path) ?? I3ConfigParser.Parse(I3Defaults.Text);

    /// <summary>
    /// Reads and parses the configuration file, when it yields usable
    /// bindings; null means the caller should keep what it has.
    /// </summary>
    private static I3Config? TryReadConfig(string path)
    {
        try
        {
            var config = I3ConfigParser.Parse(File.ReadAllText(path));
            if (config.Bindings.Count > 0)
                return config;

            AppLog.Warn($"configuration: {path} has no usable bindings");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"configuration: cannot read {path}: {error.Message}");
        }

        return null;
    }

    /// <summary>
    /// Re-reads the configuration file and swaps the registered hotkeys
    /// and window rules for the new set, like i3's reload. The current
    /// configuration survives when the file cannot be read.
    /// </summary>
    private void ReloadConfiguration()
    {
        if (TryReadConfig(_configPath) is not { } config)
        {
            _trayIcon?.ShowBalloonTip(
                3000, "i3vm4windows",
                $"Could not read {_configPath}; keeping the current configuration.",
                System.Windows.Forms.ToolTipIcon.Error);
            return;
        }

        foreach (var warning in config.Warnings)
            AppLog.Warn($"configuration: {warning}");

        AppLog.Info($"reload: {config.Bindings.Count} bindings, " +
            $"{config.Rules.Count} rules");

        _hotkeyHost?.UnregisterAll();
        _bindings.Clear();
        _modes.Clear();
        SetMode("default");
        RegisterHotkeys(config);
        _windowManager?.UpdateRules(config.Rules);

        _trayIcon?.ShowBalloonTip(
            2000, "i3vm4windows",
            $"Reloaded {config.Bindings.Count} key bindings.",
            System.Windows.Forms.ToolTipIcon.Info);
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
            if (binding.Mode == "default")
            {
                // Config.Modifiers uses the Win32 MOD_* values.
                _hotkeyHost.Register(id, (Win32.Modifiers)binding.Modifiers, key);
            }
            else
            {
                // Mode bindings dispatch through the hook's mode matching
                // only while their mode is active.
                if (!_modes.TryGetValue(binding.Mode, out var list))
                {
                    list = new List<(int, Binding)>();
                    _modes[binding.Mode] = list;
                }

                list.Add((id, binding));
            }

            _bindings[id] = binding;
        }
    }

    /// <summary>
    /// Enters the named binding mode, like i3's mode command: its bindings
    /// replace the default ones until another mode command leaves it.
    /// </summary>
    private void SetMode(string? name)
    {
        var mode = string.IsNullOrEmpty(name) ||
            name.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? "default"
                : name;
        var bindings = mode != "default" && _modes.TryGetValue(mode, out var list)
            ? list
            : new List<(int Id, Binding Binding)>();

        var registered = new List<(int Id, Win32.Modifiers Modifiers, Key Key)>();
        foreach (var (id, binding) in bindings)
        {
            if (ToWpfKey(binding.Key) is { } key)
                registered.Add((id, (Win32.Modifiers)binding.Modifiers, key));
        }

        _hotkeyHost?.SetModeBindings(registered);
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

        AppLog.Info($"hotkey: {binding.Action} ({binding.Key}, {binding.Mode} mode)");

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
            case ConfigAction.SwitchBackAndForth:
                _windowManager.SwitchBackAndForth();
                break;
            case ConfigAction.Mode:
                SetMode(binding.Command);
                break;
            case ConfigAction.ResizeGrowWidth:
                _windowManager.ResizeFocused(ResizeAxis.Width, binding.Parameter);
                break;
            case ConfigAction.ResizeShrinkWidth:
                _windowManager.ResizeFocused(ResizeAxis.Width, -binding.Parameter);
                break;
            case ConfigAction.ResizeGrowHeight:
                _windowManager.ResizeFocused(ResizeAxis.Height, binding.Parameter);
                break;
            case ConfigAction.ResizeShrinkHeight:
                _windowManager.ResizeFocused(ResizeAxis.Height, -binding.Parameter);
                break;
            case ConfigAction.MoveToWorkspace:
                _windowManager.MoveFocusedToWorkspace(binding.Parameter);
                break;
            case ConfigAction.ToggleOrientation:
                _windowManager.ToggleOrientation();
                break;
            case ConfigAction.SetTabbed:
                _windowManager.SetTabbed();
                break;
            case ConfigAction.SetSplit:
                _windowManager.SetSplit();
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
            case ConfigAction.FloatingToggle:
                _windowManager.ToggleFloating();
                break;
            case ConfigAction.ScratchpadShow:
                if (binding.Rule is { } rule)
                    _windowManager.ToggleScratchpad(rule);
                break;
        }
    }

    /// <summary>
    /// Starts the bound program, like i3's exec: the first token names the
    /// program and the rest are its arguments; a quoted first token carries
    /// a path with spaces. Windows environment variables (%USERPROFILE%,
    /// ...) and a leading ~ expand to the user profile.
    /// </summary>
    private static void LaunchProcess(string? command)
    {
        if (command is null)
            return;

        command = Environment.ExpandEnvironmentVariables(command);
        if (command.StartsWith('~') &&
            (command.Length == 1 || command[1] is '/' or '\\'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            command = home + command[1..].Replace('/', '\\');
        }

        var trimmed = command.Trim();
        string fileName;
        string arguments;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0)
            {
                AppLog.Warn($"unterminated program path in {command}");
                return;
            }

            fileName = trimmed[1..end];
            arguments = trimmed[(end + 1)..].Trim();

            // A quoted first token carries a path with spaces — but when
            // the program and its arguments ended up inside the same
            // quotes and only the program part exists as a file, that
            // part is the program and the rest are its arguments.
            var space = fileName.IndexOf(' ');
            if (space > 0 && !File.Exists(fileName))
            {
                var program = fileName[..space];
                if (File.Exists(program) || File.Exists(program + ".exe"))
                {
                    arguments = $"{fileName[(space + 1)..]} {arguments}".Trim();
                    fileName = program;
                }
            }
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
            AppLog.Warn($"cannot start {fileName}: {error.Message}");
        }
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Reload configuration", null, (_, _) => ReloadConfiguration());
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo
        {
            FileName = AppLog.Path,
            UseShellExecute = true,
        }));
        menu.Items.Add("Exit i3vm4windows", null, (_, _) => Shutdown());

        var icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "i3vm4windows",
            Icon = AppIcon.Create(),
            Visible = true,
            ContextMenuStrip = menu,
        };

        return icon;
    }
}
