using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using i3vm4windows.App.Interop;

namespace i3vm4windows.App.Services;

/// <summary>
/// A brief on-screen display that shows a display's bound workspaces with
/// the active one highlighted whenever the active workspace changes, like
/// i3's status bar in miniature. The window never takes focus, is click
/// through, and hides itself after a moment.
/// </summary>
internal sealed class WorkspaceNotifier : IDisposable
{
    private const int DisplayMilliseconds = 1400;
    private const int FadeMilliseconds = 200;
    private const int BottomMargin = 24;

    private readonly DispatcherTimer _hideTimer;
    private Window? _window;
    private StackPanel? _chips;
    private bool _disposed;

    public WorkspaceNotifier()
    {
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(DisplayMilliseconds),
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    /// <summary>
    /// Shows the workspaces bound to a display with the active one
    /// highlighted, centered near the bottom of the work area.
    /// </summary>
    public void Show(i3vm4windows.Core.Rect workArea, IReadOnlyList<int> workspaces, int activeIndex)
    {
        if (_disposed)
            return;

        EnsureWindow();
        BuildChips(workspaces, activeIndex);
        _window!.UpdateLayout();

        if (!_window.IsVisible)
        {
            // Park off-screen until the position is known, then show.
            _window.Left = -32000;
            _window.Top = -32000;
            _window.Show();
            _window.UpdateLayout();
        }

        _window.BeginAnimation(UIElement.OpacityProperty, null);
        _window.Opacity = 1;
        _window.Left = workArea.X + (workArea.Width - _window.ActualWidth) / 2;
        _window.Top = workArea.Bottom - _window.ActualHeight - BottomMargin;

        // Restart the countdown so rapid switching keeps the display up.
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void EnsureWindow()
    {
        if (_window is not null)
            return;

        _chips = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };

        _window = new Window
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
                Child = _chips,
            },
        };

        // Tool window so the shell and Alt+Tab ignore it, no-activate so it
        // never steals focus, transparent so clicks fall through.
        _window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(_window).Handle;
            var extended = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE);
            var style = extended |
                (nint)Win32.WS_EX_TOOLWINDOW |
                (nint)Win32.WS_EX_NOACTIVATE |
                (nint)Win32.WS_EX_TRANSPARENT;
            Win32.SetWindowLongPtr(handle, Win32.GWL_EXSTYLE, style);
        };
    }

    private void BuildChips(IReadOnlyList<int> workspaces, int activeIndex)
    {
        _chips!.Children.Clear();
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
            _chips.Children.Add(chip);
        }
    }

    private void Hide()
    {
        if (_window is not { IsVisible: true })
            return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeMilliseconds));
        fade.Completed += (_, _) =>
        {
            _window.BeginAnimation(UIElement.OpacityProperty, null);
            _window.Hide();
        };
        _window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hideTimer.Stop();
        _window?.Close();
        _window = null;
    }
}
