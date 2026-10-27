using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>One tab of the bar: the window's title, or whether it is the
/// active one.</summary>
public sealed record BarTab(string Title, bool Active);

/// <summary>
/// An i3bar-style strip floating along the top of every display: the
/// bound workspace numbers with the active one highlighted, and — when
/// the workspace shows tabs — every tab's title with the active one
/// highlighted. The tiling keeps its distance: the manager takes the
/// strip off the top of every work area, and this window covers the
/// freed strip, pinned above everything. Maximizing a floating window
/// can slide under it.
/// </summary>
internal sealed class WorkspaceBar : IDisposable
{
    public const int Height = 30;

    /// <summary>The widest a tab's title chip may become; longer titles
    /// end in an ellipsis.</summary>
    private const double TabMaxWidth = 180;

    /// <summary>One strip per display.</summary>
    private sealed class Bar : IDisposable
    {
        public required Window Window { get; init; }
        public required StackPanel Chips { get; init; }
        public required StackPanel Tabs { get; init; }

        public void Dispose() => Window.Close();
    }

    private readonly Dictionary<string, Bar> _bars = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

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
        foreach (var bar in _bars.Values)
            bar.Dispose();

        _bars.Clear();
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
        var content = new DockPanel
        {
            Background = new SolidColorBrush(
                System.Windows.Media.Color.FromArgb(235, 24, 26, 32)),
        };
        DockPanel.SetDock(chips, Dock.Left);
        content.Children.Add(chips);
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
        };
        return bar;
    }

    /// <summary>
    /// Covers the strip the manager freed at the top of the work area;
    /// both are in physical pixels, so the positioning goes through
    /// Win32, never through the DIP-space of the WPF window.
    /// </summary>
    private static void Position(Window window, i3vm4windows.Core.Rect workArea)
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
        Win32.SetWindowPos(
            handle, IntPtr.Zero,
            workArea.X, workArea.Y - Height, workArea.Width, Height,
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
}
