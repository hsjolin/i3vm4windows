using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Config;

namespace i3vm4windows.App.Services;

/// <summary>One tab of the bar: the window's title, or whether it is the
/// active one.</summary>
public sealed record BarTab(string Title, bool Active);

/// <summary>
/// An i3bar-style strip along the top (or bottom) of every display: the
/// bound workspace numbers with the active one highlighted, every tab's
/// title when the workspace shows tabs, and the configured status items
/// — clock, date, CPU and memory usage, and the first output line of
/// exec commands — along the right edge. The tiling keeps its distance:
/// the manager takes the strip off the work area, and this window
/// covers the freed strip, pinned above everything.
/// </summary>
internal sealed class WorkspaceBar : IDisposable
{
    /// <summary>How often the status items refresh.</summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(2);

    /// <summary>The widest a tab's title chip may become; longer titles
    /// end in an ellipsis.</summary>
    private const double TabMaxWidth = 180;

    private readonly BarConfig _config;
    private readonly DispatcherTimer _statusTimer;
    private readonly Dictionary<string, Bar> _bars = new(StringComparer.OrdinalIgnoreCase);
    private (ulong Idle, ulong Total)? _lastCpu;
    private bool _statusRunning;
    private bool _disposed;

    /// <summary>One strip per display.</summary>
    private sealed class Bar : IDisposable
    {
        public required Window Window { get; init; }
        public required StackPanel Chips { get; init; }
        public required StackPanel Tabs { get; init; }
        public required StackPanel Status { get; init; }

        public void Dispose() => Window.Close();
    }

    public WorkspaceBar(BarConfig config)
    {
        _config = config;
        if (config.Clock || config.Date || config.Cpu || config.Memory ||
            config.Commands.Count > 0)
        {
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = StatusInterval,
            };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            _statusTimer.Start();
            UpdateStatus();
        }
        else
        {
            _statusTimer = null!;
        }
    }

    /// <summary>Brings the display's strip in sync with the workspaces.</summary>
    public void Update(
        string output,
        i3vm4windows.Core.Rect workArea,
        IReadOnlyList<int> workspaces,
        int activeIndex,
        IReadOnlyList<BarTab> tabs)
    {
        if (_disposed)
            return;

        var bar = EnsureBar(output);
        BuildChips(bar.Chips, workspaces, activeIndex);
        BuildTabs(bar.Tabs, tabs);
        Position(bar.Window, workArea);
    }

    /// <summary>Closes the strips of displays that are gone.</summary>
    public void CloseMissing(IReadOnlyCollection<string> outputs)
    {
        var present = new HashSet<string>(outputs, StringComparer.OrdinalIgnoreCase);
        foreach (var output in _bars.Keys.Where(o => !present.Contains(o)).ToList())
        {
            _bars[output].Dispose();
            _bars.Remove(output);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _statusTimer?.Stop();
        foreach (var bar in _bars.Values)
            bar.Dispose();

        _bars.Clear();
    }

    /// <summary>
    /// Refreshes the status items and shows them on every display's
    /// strip. Exec commands run in parallel off the dispatcher; a slow
    /// one delays its own tick, never the strip.
    /// </summary>
    private async void UpdateStatus()
    {
        if (_statusRunning)
            return;

        _statusRunning = true;
        try
        {
            var parts = new List<string>();
            if (_config.Clock)
                parts.Add(DateTime.Now.ToString("HH:mm"));
            if (_config.Date)
                parts.Add(DateTime.Now.ToString("yyyy-MM-dd"));
            if (_config.Cpu && CpuPercent() is { } cpu)
                parts.Add($"{cpu:F0}% CPU");
            if (_config.Memory && MemoryPercent() is { } memory)
                parts.Add($"RAM {memory}%");
            if (_config.Commands.Count > 0)
            {
                var outputs = await Task.WhenAll(_config.Commands.Select(RunCommand));
                parts.AddRange(outputs.Where(output => output.Length > 0));
            }

            foreach (var bar in _bars.Values)
                BuildStatus(bar.Status, parts);
        }
        finally
        {
            _statusRunning = false;
        }
    }

    /// <summary>
    /// The busy percentage between the previous sample and now, or null
    /// on the first sample: kernel time includes idle, so busy is
    /// kernel + user - idle.
    /// </summary>
    private int? CpuPercent()
    {
        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        var idleTicks = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
        var kernelTicks = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
        var userTicks = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
        var total = kernelTicks + userTicks;
        var previous = _lastCpu;
        _lastCpu = (idleTicks, total);
        if (previous is not { } before || total <= before.Total)
            return null;

        var busy = (total - before.Total) - (idleTicks - before.Idle);
        return Math.Clamp((int)(100 * busy / (total - before.Total)), 0, 100);
    }

    private static int? MemoryPercent()
    {
        var status = new Win32.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>(),
        };
        return Win32.GlobalMemoryStatusEx(ref status) ? (int)status.dwMemoryLoad : null;
    }

    /// <summary>
    /// Runs one status command through cmd.exe, so pipes and scripts
    /// work, and returns its first line of output.
    /// </summary>
    private static async Task<string> RunCommand(string command)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null)
                return "";

            return (await process.StandardOutput.ReadLineAsync() ?? "").Trim();
        }
        catch (Exception error)
        {
            AppLog.Warn($"bar command failed ({command}): {error.Message}");
            return "";
        }
    }

    private Bar EnsureBar(string output)
    {
        if (_bars.TryGetValue(output, out var bar))
            return bar;

        var chips = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        var tabs = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        var status = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        var content = new DockPanel
        {
            Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(235, 24, 26, 32)),
        };
        DockPanel.SetDock(chips, Dock.Left);
        DockPanel.SetDock(status, Dock.Right);
        content.Children.Add(chips);
        content.Children.Add(status);
        content.Children.Add(tabs);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Content = content,
        };

        // Tool window so the shell and Alt+Tab ignore it, no-activate so
        // it never steals focus.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var extended = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(
                handle, Win32.GWL_EXSTYLE,
                extended | (nint)Win32.WS_EX_TOOLWINDOW | (nint)Win32.WS_EX_NOACTIVATE);
        };

        _bars[output] = bar = new Bar
        {
            Window = window,
            Chips = chips,
            Tabs = tabs,
            Status = status,
        };
        return bar;
    }

    /// <summary>
    /// Covers the strip the manager freed at the top (or bottom) of the
    /// work area; both are in physical pixels, so the positioning goes
    /// through Win32, never through the DIP-space of the WPF window.
    /// </summary>
    private void Position(Window window, i3vm4windows.Core.Rect workArea)
    {
        window.UpdateLayout();
        if (!window.IsVisible)
        {
            // Park off-screen until the position is known.
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.UpdateLayout();
        }

        var handle = new WindowInteropHelper(window).Handle;
        var top = _config.Position == "bottom"
            ? workArea.Bottom
            : workArea.Y - _config.Height;
        Win32.SetWindowPos(
            handle, IntPtr.Zero,
            workArea.X, top, workArea.Width, _config.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    private static void BuildChips(StackPanel chips, IReadOnlyList<int> workspaces, int activeIndex)
    {
        chips.Children.Clear();
        foreach (var index in workspaces)
        {
            var active = index == activeIndex;
            var chip = new Border
            {
                MinWidth = 28,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(2, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = active
                    ? System.Windows.Media.Brushes.White
                    : System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = index.ToString(),
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = active
                        ? System.Windows.Media.Brushes.Black
                        : System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                },
            };
            chips.Children.Add(chip);
        }
    }

    private static void BuildTabs(StackPanel tabs, IReadOnlyList<BarTab> barTabs)
    {
        tabs.Children.Clear();
        foreach (var tab in barTabs)
        {
            tabs.Children.Add(new Border
            {
                MaxWidth = TabMaxWidth,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(2, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = tab.Active
                    ? System.Windows.Media.Brushes.White
                    : System.Windows.Media.Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = tab.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 13,
                    Foreground = tab.Active
                        ? System.Windows.Media.Brushes.Black
                        : System.Windows.Media.Brushes.White,
                },
            });
        }
    }

    private static void BuildStatus(StackPanel status, IReadOnlyList<string> parts)
    {
        status.Children.Clear();
        foreach (var part in parts)
        {
            status.Children.Add(new TextBlock
            {
                Text = part,
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
            });
        }
    }
}
