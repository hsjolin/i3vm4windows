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
    private ConfigRuntime? _runtime;
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

        _windowManager = new WindowManager(config.Rules, LayoutOptionsFor(config), config.Bar);
        _windowManager.Start();

        _hotkeyHost = new HotkeyHost();
        _hotkeyHost.Start(HandleHotkey);
        RegisterHotkeys(config);

        // The statements carry their own runtime behavior; this is what
        // they execute it against.
        _runtime = new ConfigRuntime(_windowManager, name => SetMode(name));

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
            Launcher.Start(command);

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
    /// Restarts the app completely, like exiting and starting it again:
    /// the running instance shuts down — restoring hidden windows and
    /// releasing its hooks — while a fresh instance starts and loads the
    /// configuration from scratch.
    /// </summary>
    private void RestartApplication()
    {
        AppLog.Info("restarting on request");

        if (Environment.ProcessPath is { } exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            }
            catch (Exception error)
            {
                AppLog.Warn($"cannot restart {exe}: {error.Message}");
            }
        }

        Shutdown();
    }

    /// <summary>
    /// The layout tuning the configuration controls: gaps are fixed for
    /// now, the minimum window width comes from the min_window_width
    /// directive (400 when the configuration does not set one) and the
    /// layout of new workspaces from workspace_layout.
    /// </summary>
    private static LayoutOptions LayoutOptionsFor(I3Config config) => new()
    {
        OuterMargin = 8,
        InnerGap = 8,
        MinWindowWidth = config.MinWindowWidth,
        DefaultLayoutMode = config.DefaultWorkspaceLayout,
    };

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
        if (!_bindings.TryGetValue(id, out var binding))
            return;

        AppLog.Info($"hotkey: {binding.Action} ({binding.Key}, {binding.Mode} mode)");

        // The command's own statement parsed its behavior; running it is
        // all the app does here.
        binding.Run?.Invoke(_runtime!, binding.Rule);
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Reload i3vm4windows", null, (_, _) => RestartApplication());
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
