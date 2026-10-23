using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// A persistent indicator per display, showing the workspaces bound to the
/// display with the active one highlighted, like i3's status bar in
/// miniature. It rests dimmed in the bottom-left corner and lights up for
/// a moment whenever the active workspace changes. The windows never take
/// focus, are click through, and sit above the tiling.
/// </summary>
internal sealed class WorkspaceIndicator : IDisposable
{
    /// <summary>The opacity the indicators rest at, dimmed but readable.</summary>
    private const double RestingOpacity = 0.55;
    private const int EmphasisMilliseconds = 1400;
    private const int FadeMilliseconds = 200;
    private const int CornerMargin = 16;

    /// <summary>One indicator window and its settle-back countdown.</summary>
    private sealed class Indicator : IDisposable
    {
        public required Window Window { get; init; }
        public required StackPanel Chips { get; init; }
        public required DispatcherTimer SettleTimer { get; init; }

        public void Dispose()
        {
            SettleTimer.Stop();
            Window.Close();
        }
    }

    private readonly Dictionary<string, Indicator> _indicators = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Shows the display's workspaces and lights the indicator up, as a
    /// workspace switch does: full opacity, settling back to the resting
    /// state after a moment. Rapid switching keeps the emphasis up.
    /// </summary>
    public void Show(
        string output, i3vm4windows.Core.Rect workArea, IReadOnlyList<int> workspaces, int activeIndex)
    {
        if (_disposed)
            return;

        var indicator = EnsureIndicator(output, workArea, workspaces, activeIndex);
        indicator.Window.BeginAnimation(UIElement.OpacityProperty, null);
        indicator.Window.Opacity = 1;
        indicator.SettleTimer.Stop();
        indicator.SettleTimer.Start();
    }

    /// <summary>
    /// Brings the display's indicator in sync without announcing anything:
    /// content and position follow the current workspaces, while an
    /// emphasis from a recent switch runs its course undisturbed.
    /// </summary>
    public void Update(
        string output, i3vm4windows.Core.Rect workArea, IReadOnlyList<int> workspaces, int activeIndex)
    {
        if (_disposed)
            return;

        var indicator = EnsureIndicator(output, workArea, workspaces, activeIndex);
        if (!indicator.SettleTimer.IsEnabled)
        {
            indicator.Window.BeginAnimation(UIElement.OpacityProperty, null);
            indicator.Window.Opacity = RestingOpacity;
        }
    }

    /// <summary>Closes the indicators of displays that are gone.</summary>
    public void CloseMissing(IReadOnlyCollection<string> outputs)
    {
        var present = new HashSet<string>(outputs, StringComparer.OrdinalIgnoreCase);
        foreach (var output in _indicators.Keys.Where(output => !present.Contains(output)).ToList())
        {
            _indicators[output].Dispose();
            _indicators.Remove(output);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var indicator in _indicators.Values)
            indicator.Dispose();

        _indicators.Clear();
    }

    private Indicator EnsureIndicator(
        string output, i3vm4windows.Core.Rect workArea, IReadOnlyList<int> workspaces, int activeIndex)
    {
        if (!_indicators.TryGetValue(output, out var indicator))
        {
            indicator = CreateIndicator();
            _indicators[output] = indicator;
        }

        BuildChips(indicator.Chips, workspaces, activeIndex);
        Position(indicator.Window, workArea);
        return indicator;
    }

    private Indicator CreateIndicator()
    {
        var chips = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border
            {
                Background = new SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(230, 32, 32, 32)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Child = chips,
            },
        };

        // Tool window so the shell and Alt+Tab ignore it, no-activate so it
        // never steals focus, transparent so clicks fall through.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var extended = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE);
            var style = extended |
                (nint)Win32.WS_EX_TOOLWINDOW |
                (nint)Win32.WS_EX_NOACTIVATE |
                (nint)Win32.WS_EX_TRANSPARENT;
            Win32.SetWindowLongPtr(handle, Win32.GWL_EXSTYLE, style);
        };

        var settleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(EmphasisMilliseconds),
        };
        settleTimer.Tick += (_, _) =>
        {
            settleTimer.Stop();
            var fade = new DoubleAnimation(RestingOpacity, TimeSpan.FromMilliseconds(FadeMilliseconds));
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        };

        return new Indicator
        {
            Window = window,
            Chips = chips,
            SettleTimer = settleTimer,
        };
    }

    /// <summary>
    /// Places the indicator in the bottom-left corner of the work area.
    /// The work area is in physical pixels while WPF positions windows in
    /// device-independent units, so the final placement goes through Win32:
    /// on a display scaled above 100% the DIP coordinates would land far
    /// below the screen edge, leaving the indicator invisible.
    /// </summary>
    private static void Position(Window window, i3vm4windows.Core.Rect workArea)
    {
        window.UpdateLayout();
        if (!window.IsVisible)
        {
            // Park off-screen until the position is known, then show.
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.UpdateLayout();
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (!Win32.GetWindowRect(handle, out var rect))
            return;

        var height = rect.Bottom - rect.Top;
        Win32.SetWindowPos(
            handle,
            IntPtr.Zero,
            workArea.X + CornerMargin,
            workArea.Bottom - height - CornerMargin,
            0, 0,
            Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

        // SizeToContent re-applies the WPF position when the chips change
        // size; NaN makes it keep the window where the OS just put it
        // instead of moving it back to the park position.
        window.Left = double.NaN;
        window.Top = double.NaN;
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
                Padding = new Thickness(6, 3, 6, 3),
                CornerRadius = new CornerRadius(5),
                Background = active ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent,
                Margin = new Thickness(2, 0, 2, 0),
                Child = new TextBlock
                {
                    Text = index.ToString(),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = active ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                },
            };
            chips.Children.Add(chip);
        }
    }
}
