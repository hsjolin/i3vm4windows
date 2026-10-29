using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using i3vm4windows.App.Interop;
using i3vm4windows.Config;
using i3vm4windows.Core;
using Microsoft.Win32;

namespace i3vm4windows.App.Services;

/// <summary>
/// Tracks top-level windows across all connected monitors and tiles the
/// global workspaces on the outputs they are bound to, applying the computed
/// layout via SetWindowPos.
/// </summary>
internal sealed class WindowManager : IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "MSCTFIME UI",
        "Default IME",
        "#32768",
        "tooltips_class32",
        "ComboLBox",
    };

    private readonly WorkspaceManager _workspaces = new();
    private readonly Dictionary<IntPtr, MonitorInfo> _monitors = new();
    /// <summary>
    /// The rectangle a window was asked to occupy and the rectangle it ended
    /// up with; windows that clamp sizes or block moves stop the layout from
    /// fighting them.
    /// </summary>
    private readonly record struct LayoutPlacement(Rect Requested, Rect Actual);

    private readonly Dictionary<long, LayoutPlacement> _placements = new();
    private LayoutOptions _options = new() { OuterMargin = 8, InnerGap = 8 };
    private readonly HashSet<long> _managedHandles = new();
    /// <summary>
    /// Windows the user released from the tiling with floating toggle:
    /// discovery must not re-tile them on later rescans.
    /// </summary>
    private readonly HashSet<long> _floatedByUser = new();
    private readonly HashSet<long> _hiddenByUs = new();
    /// <summary>
    /// The workspace a scratchpad window was last shown on, by window
    /// handle; toggle decisions compare it with the active workspace.
    /// </summary>
    private readonly Dictionary<long, (string Output, int Index)> _scratchpadHomes = new();
    /// <summary>
    /// The windows a for_window move scratchpad rule has already taken
    /// (or that predate the run), so showing one never hides it again.
    /// </summary>
    private readonly HashSet<long> _scratchpadSeen = new();
    private readonly BarConfig _barConfig;
    private readonly WorkspaceBar _bar;
    private readonly DispatcherTimer _refreshTimer;
    private readonly uint _ownProcessId;
    private readonly Win32.WinEventDelegate _winEventDelegate;
    private IReadOnlyList<WindowRule> _rules;
    private IntPtr _winEventHook;
    private bool _disposed;

    public WindowManager(
        IReadOnlyList<WindowRule>? rules = null,
        LayoutOptions? options = null,
        BarConfig? bar = null)
    {
        _rules = rules ?? Array.Empty<WindowRule>();
        _options = options ?? _options;
        _workspaces.DefaultLayoutMode = _options.DefaultLayoutMode;
        _barConfig = bar ?? new BarConfig();
        _bar = new WorkspaceBar(_barConfig);
        _ownProcessId = (uint)Environment.ProcessId;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshWindows();
        };
        _winEventDelegate = OnWinEvent;
    }

    public void Start()
    {
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_DESTROY,
            Win32.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SeedScratchpadWindows();

        RefreshWindows();
    }

    /// <summary>
    /// Records the windows that exist before the tiling starts: a
    /// for_window move scratchpad rule applies to windows that appear
    /// while the app runs, like i3's on-appear rules — never to what a
    /// restart finds already open.
    /// </summary>
    private void SeedScratchpadWindows()
    {
        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.IsWindow(hwnd))
                _scratchpadSeen.Add(hwnd.ToInt64());
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// Reacts to display configuration changes (a monitor being connected,
    /// disconnected or reconfigured): the debounced refresh re-syncs the
    /// monitor list, migrates orphaned windows and re-tiles.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        AppLog.Info("display settings changed; resyncing");

        // SystemEvents may deliver the event on a thread that is not ours;
        // the refresh touches dispatcher-affine state, so it runs on ours.
        _refreshTimer.Dispatcher.BeginInvoke(DebounceRefresh);
    }

    /// <summary>Restarts the refresh timer so it fires once changes settle.</summary>
    private void DebounceRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>
    /// Releases the window from the tiling for a mouse drag: a managed
    /// window becomes floating where it stands (free to join the tiling
    /// again with floating toggle), an already-floating one stays.
    /// </summary>
    public void ReleaseForDragging(IntPtr hwnd)
    {
        var handle = hwnd.ToInt64();
        if (!_managedHandles.Contains(handle))
            return;

        UnmanageWindow(handle);
        _floatedByUser.Add(handle);
        AppLog.Info($"drag: released 0x{handle:X} from the tiling");
    }

    public void RefreshWindows()
    {
        if (_disposed)
            return;

        try
        {
            ForgetDeadWindows();
            ReconcileMonitors();
            ReassignWindows();
            ReconcileUserWindowChanges();
            DiscoverNewWindows();
            ApplyLayout();
            RefreshIndicators();
        }
        catch (Exception error)
        {
            // A refresh can meet the system mid-display-reconfiguration, a
            // dead window handle or anything else transient; the tiling
            // must survive it and the next event re-syncs.
            AppLog.Warn($"refresh failed: {error.Message}");
            DebounceRefresh();
        }
    }

    /// <summary>
    /// Coordinate comparison tolerance: DPI rounding differences below this
    /// are ignored so our own positioning never fights itself.
    /// </summary>
    private const int PositionTolerance = 1;

    /// <summary>
    /// A window refusing its tiled rectangle by more than this feeds its
    /// actual extent back into the layout; smaller differences are
    /// rounding noise that would make the weights oscillate.
    /// </summary>
    private const int RefusalTolerance = 4;

    public void ApplyLayout()
    {
        // Windows that refuse to shrink below a program-imposed minimum
        // keep that size as their layout weight and the siblings share what
        // remains; a few passes let the layout settle.
        for (var pass = 0; pass < 4; pass++)
        {
            if (!ApplyLayoutPass())
                break;
        }
    }

    /// <summary>
    /// One layout application; returns true when a window refused its
    /// rectangle and the new weights need another pass.
    /// </summary>
    private bool ApplyLayoutPass()
    {
        var resized = false;
        foreach (var monitor in _monitors.Values)
        {
            var assignments = _workspaces.ComputeActiveLayout(monitor.DeviceName, monitor.WorkArea, _options);
            var assigned = new HashSet<long>(assignments.Select(a => a.WindowHandle));
            foreach (var (handle, rect) in assignments)
            {
                var hwnd = new IntPtr(handle);
                if (Win32.IsIconic(hwnd) || !Win32.GetWindowRect(hwnd, out var current))
                    continue;

                // A window that just became visible (an activated tab) shows
                // first; members without an area hide after the loop.
                if (!Win32.IsWindowVisible(hwnd))
                {
                    Win32.ShowWindow(hwnd, Win32.SW_SHOW);
                    _hiddenByUs.Remove(handle);
                }

                if (NearlyAt(current, rect))
                {
                    _placements[handle] = new LayoutPlacement(rect, rect);
                    continue;
                }

                // Skip windows that are already in place, and windows that
                // refused this exact rectangle before: our own SetWindowPos
                // calls would otherwise re-trigger the refresh cycle.
                if (_placements.TryGetValue(handle, out var placement) &&
                    placement.Requested == rect && NearlyAt(current, placement.Actual))
                {
                    continue;
                }

                PositionWindow(handle, rect);
                var actual = Win32.GetWindowRect(hwnd, out var placed)
                    ? new Rect(placed.Left, placed.Top, placed.Right - placed.Left, placed.Bottom - placed.Top)
                    : rect;
                _placements[handle] = new LayoutPlacement(rect, actual);

                // A window that kept a bigger size than the layout asked
                // for (its program's minimum) would overlap its siblings;
                // its actual extent becomes its weight so the siblings
                // share the rest on the next pass.
                if (actual.Width - rect.Width > RefusalTolerance &&
                    _workspaces.ApplyUserResize(handle, ResizeAxis.Width, actual.Width))
                {
                    resized = true;
                }
                else if (actual.Height - rect.Height > RefusalTolerance &&
                    _workspaces.ApplyUserResize(handle, ResizeAxis.Height, actual.Height))
                {
                    resized = true;
                }
            }

            // Tabbed containers give only one member an area; the others
            // stay hidden until focus switches to them. Hidden here, not
            // unmanaged: they come back when their tab is activated.
            var active = _workspaces.ActiveOn(monitor.DeviceName);
            foreach (var handle in active.WindowHandles)
            {
                if (assigned.Contains(handle))
                    continue;

                var hwnd = new IntPtr(handle);
                if (Win32.IsWindowVisible(hwnd))
                {
                    Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                    _hiddenByUs.Add(handle);
                }
            }
        }

        return resized;
    }

    public void FocusRelative(int offset)
    {
        var output = ActiveMonitor().DeviceName;
        if (!_workspaces.FocusRelative(output, offset))
            return;

        // In a tabbed container the newly focused window had no area; the
        // re-applied layout shows it and hides the previous tab.
        ApplyLayout();

        if (_workspaces.ActiveOn(output).FocusedWindowHandle is { } handle)
            Win32.SetForegroundWindow(new IntPtr(handle));

        // The pointer follows the focus, like the workspace switch does.
        WarpCursorToWorkspace(output);
    }

    public void MoveFocused(int offset)
    {
        if (_workspaces.MoveFocused(ActiveMonitor().DeviceName, offset))
            ApplyLayout();
    }

    public void ToggleOrientation()
    {
        _workspaces.ToggleOrientation(ActiveMonitor().DeviceName);
        ApplyLayout();
    }

    /// <summary>
    /// Presents the split of the focused window as tabs, like i3's
    /// <c>layout tabbed</c>: one window shows at a time and focus left/right
    /// switches between them.
    /// </summary>
    public void SetTabbed() => SetLayoutMode(LayoutMode.Tabbed);

    /// <summary>
    /// Returns the split of the focused window to normal tiling, like i3's
    /// <c>layout default</c>.
    /// </summary>
    public void SetSplit() => SetLayoutMode(LayoutMode.Split);

    private void SetLayoutMode(LayoutMode mode)
    {
        _workspaces.SetLayoutMode(ActiveMonitor().DeviceName, mode);
        ApplyLayout();
    }

    /// <summary>
    /// Toggles the focused window between the tiling and floating: a
    /// managed window is released to move and resize freely — visibly
    /// inset from the tile it leaves, staying floating across rescans —
    /// and a floating window joins the tiling of the display it is on,
    /// even when a floating rule excluded it: the explicit toggle wins.
    /// </summary>
    public void ToggleFloating()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return;

        var handle = foreground.ToInt64();
        if (_managedHandles.Contains(handle))
        {
            if (Win32.GetWindowRect(foreground, out var current))
            {
                // Inset from the tile so the toggle is visible, like a
                // floating window detached from the layout.
                var width = (current.Right - current.Left) * 3 / 4;
                var height = (current.Bottom - current.Top) * 3 / 4;
                Win32.SetWindowPos(
                    foreground,
                    IntPtr.Zero,
                    current.Left + (current.Right - current.Left - width) / 2,
                    current.Top + (current.Bottom - current.Top - height) / 2,
                    width,
                    height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            }

            UnmanageWindow(handle);
            _floatedByUser.Add(handle);
            return;
        }

        if (!IsTileable(foreground, requireVisible: true, ignoreFloatingRules: true))
            return;

        _floatedByUser.Remove(handle);
        _workspaces.AddWindow(handle, ActiveMonitor().DeviceName);
        _managedHandles.Add(handle);
        _placements.Remove(handle);
        ApplyLayout();
    }

    /// <summary>
    /// Toggles the windows matching the rule onto the workspace that is
    /// active: a hidden one shows there, one visible elsewhere follows
    /// the user instead of hiding, and one already on the active
    /// workspace hides. The state is the window's own visibility, so the
    /// toggle survives restarts.
    /// </summary>
    public void ToggleScratchpad(WindowRule rule)
    {
        var output = ActiveMonitor().DeviceName;
        var active = _workspaces.ActiveOn(output).Index;

        if (FindRuleMatch(rule, requireVisible: true) is { } visible)
        {
            if (_scratchpadHomes.TryGetValue(visible, out var home) &&
                home.Output == output && home.Index == active)
            {
                Win32.ShowWindow(new IntPtr(visible), Win32.SW_HIDE);
                return;
            }

            // Visible, but not on the workspace the user is on.
            ShowScratchpad(visible, output, active);
            return;
        }

        if (FindRuleMatch(rule, requireVisible: false) is { } hidden)
            ShowScratchpad(hidden, output, active);
    }

    /// <summary>
    /// Brings a scratchpad window onto the workspace that is active:
    /// inside the active display's work area when it is not there yet,
    /// always above the tiling and focused, remembering the workspace it
    /// now belongs to.
    /// </summary>
    private void ShowScratchpad(long handle, string output, int active)
    {
        var hwnd = new IntPtr(handle);
        var monitor = MonitorByName(output) ?? PrimaryMonitor();
        if (Win32.GetWindowRect(hwnd, out var rect) &&
            (rect.Left < monitor.WorkArea.X ||
             rect.Top < monitor.WorkArea.Y ||
             rect.Right > monitor.WorkArea.Right ||
             rect.Bottom > monitor.WorkArea.Bottom))
        {
            // Bring it inside the work area, keeping its size.
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            Win32.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                monitor.WorkArea.X + (monitor.WorkArea.Width - width) / 2,
                monitor.WorkArea.Y + (monitor.WorkArea.Height - height) / 2,
                0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        Win32.ShowWindow(hwnd, Win32.SW_SHOW);

        // Like i3's floating windows sit above the tiling, a shown
        // scratchpad window is pinned above the tiled layer.
        Win32.SetWindowPos(
            hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        Win32.SetForegroundWindow(hwnd);
        _scratchpadHomes[handle] = (output, active);
    }

    /// <summary>The first window in z-order whose class and title match the
    /// rule, visible or hidden as required. EnumWindows walks top-down, so
    /// the visible pass finds the topmost match.</summary>
    private long? FindRuleMatch(WindowRule rule, bool requireVisible)
    {
        long? found = null;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindow(hwnd) || Win32.IsWindowVisible(hwnd) != requireVisible)
                return true;

            Win32.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == _ownProcessId)
                return true;

            var className = new StringBuilder(256);
            Win32.GetClassName(hwnd, className, className.Capacity);
            var title = new StringBuilder(512);
            Win32.GetWindowText(hwnd, title, title.Capacity);

            if (rule.Matches(className.ToString(), title.ToString()))
            {
                found = hwnd.ToInt64();
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void CloseFocused()
    {
        if (_workspaces.ActiveOn(ActiveMonitor().DeviceName).FocusedWindowHandle is { } handle)
            Win32.PostMessage(new IntPtr(handle), Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Activates the workspace with the given number on the focused monitor
    /// (new workspaces are created there): every output whose view changed
    /// swaps the visibility of its windows, and focus follows.
    /// </summary>
    public void SwitchWorkspace(int index)
    {
        var result = _workspaces.SwitchTo(index, ActiveMonitor().DeviceName);
        ApplySwitch(result, index);
    }

    /// <summary>
    /// Switches to the workspace that was active before the current one on
    /// this display, like i3's workspace back_and_forth.
    /// </summary>
    public void SwitchBackAndForth()
    {
        var output = ActiveMonitor().DeviceName;
        var result = _workspaces.SwitchToBackAndForth(output);
        ApplySwitch(result, _workspaces.ActiveOn(output).Index);
    }

    /// <summary>Shows and hides the windows the switch commands and moves
    /// focus, feedback and the pointer onto the new workspace.</summary>
    private void ApplySwitch(SwitchResult result, int index)
    {
        foreach (var change in result.Changes)
        {
            if (!change.Changed)
                continue;

            if (_workspaces.TryGet(change.PreviousIndex) is { } previous)
            {
                foreach (var handle in previous.WindowHandles)
                {
                    Win32.ShowWindow(new IntPtr(handle), Win32.SW_HIDE);
                    _hiddenByUs.Add(handle);
                }
            }

            if (_workspaces.TryGet(change.NewIndex) is { } next)
            {
                foreach (var handle in next.WindowHandles)
                {
                    Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);
                    _hiddenByUs.Remove(handle);
                }
            }
        }

        ApplyLayout();

        // The switch's effect shows through the bar's highlight.
        if (_workspaces.TryGet(index)?.FocusedWindowHandle is { } focused)
            Win32.SetForegroundWindow(new IntPtr(focused));

        // The pointer follows the switch, like i3 warps it into the newly
        // focused window of the new workspace.
        WarpCursorToWorkspace(result.Changes[0].Output);
    }

    /// <summary>
    /// Resizes the focused window along the split axis by the delta, like
    /// i3's resize command: the sibling windows in the split adapt, and
    /// nothing happens when the window has no sibling to share with.
    /// </summary>
    public void ResizeFocused(ResizeAxis axis, int delta)
    {
        var output = ActiveMonitor().DeviceName;
        if (_workspaces.ActiveOn(output).FocusedWindowHandle is not { } handle)
            return;

        if (!Win32.GetWindowRect(new IntPtr(handle), out var rect))
            return;

        var size = axis == ResizeAxis.Width ? rect.Right - rect.Left : rect.Bottom - rect.Top;
        if (_workspaces.ApplyUserResize(handle, axis, size + delta))
        {
            _placements.Remove(handle);
            ApplyLayout();
        }
    }

    /// <summary>
    /// Moves the mouse pointer onto the workspace that just became active:
    /// into the middle of its focused window when there is one, else the
    /// middle of the display it is bound to.
    /// </summary>
    private void WarpCursorToWorkspace(string deviceName)
    {
        int x;
        int y;
        if (_workspaces.ActiveOn(deviceName).FocusedWindowHandle is { } handle &&
            Win32.GetWindowRect(new IntPtr(handle), out var rect))
        {
            x = (rect.Left + rect.Right) / 2;
            y = (rect.Top + rect.Bottom) / 2;
        }
        else if (MonitorByName(deviceName) is { } monitor)
        {
            x = monitor.WorkArea.X + monitor.WorkArea.Width / 2;
            y = monitor.WorkArea.Y + monitor.WorkArea.Height / 2;
        }
        else
        {
            return;
        }

        Win32.SetCursorPos(x, y);
    }

    /// <summary>
    /// Moves the focused window to the workspace with the given number; it
    /// becomes visible when that workspace is active on its output.
    /// </summary>
    public void MoveFocusedToWorkspace(int index)
    {
        var output = ActiveMonitor().DeviceName;
        if (_workspaces.MoveFocusedTo(index, output) is not { } moved)
            return;

        var visible = _workspaces.GetActiveIndex(moved.Target.OutputName) == moved.Target.Index;
        if (visible)
            Win32.ShowWindow(new IntPtr(moved.WindowHandle), Win32.SW_SHOW);
        else
        {
            Win32.ShowWindow(new IntPtr(moved.WindowHandle), Win32.SW_HIDE);
            _hiddenByUs.Add(moved.WindowHandle);
        }

        if (_workspaces.ActiveOn(output).FocusedWindowHandle is { } next)
            Win32.SetForegroundWindow(new IntPtr(next));

        ApplyLayout();
    }

    /// <summary>
    /// Moves the workspace that is active on the focused monitor to the next
    /// monitor (ordered left to right, wrapping around) and activates it
    /// there; the source monitor activates a replacement workspace.
    /// </summary>
    public void MoveActiveWorkspaceToNextMonitor()
    {
        var monitors = _monitors.Values
            .OrderBy(m => m.WorkArea.X)
            .ThenBy(m => m.WorkArea.Y)
            .ToList();
        if (monitors.Count < 2)
            return;

        var source = ActiveMonitor();
        var target = monitors[(monitors.IndexOf(source) + 1) % monitors.Count];

        // The workspace showing on the target stays bound there, but must
        // not stay visible under the incoming one.
        var previousOnTarget = _workspaces.GetActiveIndex(target.DeviceName);

        var move = _workspaces.MoveActiveWorkspaceToOutput(source.DeviceName, target.DeviceName);
        if (move is null)
            return;

        if (previousOnTarget is int previous &&
            previous != move.MovedIndex &&
            _workspaces.TryGet(previous) is { } previousWorkspace)
        {
            foreach (var handle in previousWorkspace.WindowHandles)
            {
                Win32.ShowWindow(new IntPtr(handle), Win32.SW_HIDE);
                _hiddenByUs.Add(handle);
            }
        }

        ShowAllWindows(move.MovedIndex);
        ShowAllWindows(move.SourceReplacementIndex);

        ApplyLayout();

        // The moved workspace's hidden members (tabs) still sit at their
        // old display's coordinates; park them inside the target's work
        // area, so nothing treats their stale position as another
        // display's and their reveal lands where it should.
        var workArea = target.WorkArea;
        var movedWindows = _workspaces.TryGet(move.MovedIndex)?.WindowHandles ??
            Array.Empty<long>();
        foreach (var handle in movedWindows)
        {
            var hwnd = new IntPtr(handle);
            if (Win32.IsWindowVisible(hwnd) ||
                !Win32.GetWindowRect(hwnd, out var rect) ||
                (rect.Left >= workArea.X && rect.Top >= workArea.Y &&
                 rect.Right <= workArea.Right && rect.Bottom <= workArea.Bottom))
            {
                continue;
            }

            Win32.SetWindowPos(
                hwnd, IntPtr.Zero, workArea.X + 16, workArea.Y + 16, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        NotifyBar(target.DeviceName);

        if (_workspaces.TryGet(move.MovedIndex)?.FocusedWindowHandle is { } focused)
            Win32.SetForegroundWindow(new IntPtr(focused));

        // The pointer follows the workspace to the display it moved to,
        // like the workspace switch does.
        WarpCursorToWorkspace(target.DeviceName);
    }

    /// <summary>
    /// Syncs the display's bar with its workspaces, as a workspace switch
    /// or move changes what it should show.
    /// </summary>
    private void NotifyBar(string deviceName)
    {
        if (MonitorByName(deviceName) is not { } monitor)
            return;

        var (workspaces, active) = WorkspaceView(deviceName);
        _bar.Update(deviceName, monitor.WorkArea, workspaces, active, TabsOf(deviceName));
    }

    /// <summary>
    /// The tab titles the bar shows for the workspace: every member of
    /// the focused window's tabbed split, the active one marked.
    /// </summary>
    private List<BarTab> TabsOf(string deviceName)
    {
        var focused = _workspaces.ActiveOn(deviceName).FocusedWindowHandle;
        return _workspaces.TabbedWindows(deviceName)
            .Select(handle => new BarTab(TitleOf(handle), handle == focused))
            .ToList();
    }

    /// <summary>
    /// Brings every display's corner indicator in sync without announcing
    /// anything; the switches themselves announce through
    /// <summary>
    /// Brings every display's bar in sync with its workspaces.
    /// </summary>
    private void RefreshIndicators()
    {
        foreach (var monitor in _monitors.Values)
            NotifyBar(monitor.DeviceName);

        _bar.CloseMissing(_monitors.Values.Select(m => m.DeviceName).ToList());
    }

    /// <summary>The window's title, empty when it has none.</summary>
    private static string TitleOf(long handle)
    {
        var builder = new StringBuilder(512);
        return Win32.GetWindowText(new IntPtr(handle), builder, builder.Capacity) > 0
            ? builder.ToString()
            : "";
    }

    /// <summary>
    /// The workspaces bound to the output with the active one among them,
    /// in order — what the bar displays for the display.
    /// </summary>
    private (IReadOnlyList<int> Workspaces, int Active) WorkspaceView(string deviceName)
    {
        var workspaces = _workspaces.OutputBindings
            .Where(pair => string.Equals(pair.Value, deviceName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .OrderBy(index => index)
            .ToList();
        var active = _workspaces.ActiveOn(deviceName).Index;
        if (!workspaces.Contains(active))
        {
            workspaces.Add(active);
            workspaces.Sort();
        }

        return (workspaces, active);
    }

    private MonitorInfo? MonitorByName(string deviceName) =>
        _monitors.Values.FirstOrDefault(m =>
            string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _refreshTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Without the manager running, a hidden window would be unreachable;
        // hand every managed window back to the shell visible.
        foreach (var handle in _managedHandles)
        {
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);
            _hiddenByUs.Remove(handle);
        }

        if (_winEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _bar.Dispose();
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0)
            return;

        switch (eventType)
        {
            case Win32.EVENT_SYSTEM_FOREGROUND:
                _workspaces.SetFocusedWindow(hwnd.ToInt64());
                break;

            case Win32.EVENT_OBJECT_HIDE:
                // A window hidden by its own app is withdrawn: stop managing
                // it, like i3 unmanages unmapped windows. Hides we issued
                // ourselves (workspace switches) keep the window managed.
                var handle = hwnd.ToInt64();
                if (_managedHandles.Contains(handle) &&
                    !_hiddenByUs.Contains(handle) &&
                    !Win32.IsIconic(hwnd))
                {
                    UnmanageWindow(handle);
                }

                DebounceRefresh();
                break;

            case Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_DESTROY:
                DebounceRefresh();
                break;

            case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                // Only managed windows matter; everything else moves freely.
                if (_managedHandles.Contains(hwnd.ToInt64()))
                    DebounceRefresh();
                break;
        }
    }

    private void UnmanageWindow(long handle)
    {
        _workspaces.RemoveWindow(handle);
        _managedHandles.Remove(handle);
        _placements.Remove(handle);
        _hiddenByUs.Remove(handle);
    }

    private void ForgetDeadWindows()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            if (Win32.IsWindow(new IntPtr(handle)))
                continue;

            _workspaces.RemoveWindow(handle);
            _managedHandles.Remove(handle);
            _placements.Remove(handle);
        }

        foreach (var handle in _floatedByUser.ToList())
        {
            if (!Win32.IsWindow(new IntPtr(handle)))
                _floatedByUser.Remove(handle);
        }

        // A closed scratchpad window's next life is a new window: the
        // appearing pass takes it again.
        foreach (var handle in _scratchpadSeen.ToList())
        {
            if (!Win32.IsWindow(new IntPtr(handle)))
                _scratchpadSeen.Remove(handle);
        }
    }

    /// <summary>
    /// Brings the monitor list in sync with the system and maintains the
    /// workspace-output bindings: workspaces of a disconnected display move to
    /// the primary display (the one that was active there stays visible).
    /// </summary>
    private void ReconcileMonitors()
    {
        var current = new HashSet<IntPtr>();
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var atBottom = _barConfig.Position == "bottom";
        foreach (var info in MonitorInfo.Enumerate())
        {
            current.Add(info.Handle);
            devices.Add(info.DeviceName);
            // Replace the entry so work areas follow resolution changes,
            // with the bar's strip taken off its edge: the tiling, and
            // everything else that reads work areas, uses the adjusted
            // area, and the bar covers the freed strip.
            var work = info.WorkArea;
            var adjusted = atBottom
                ? new Rect(work.X, work.Y, work.Width, Math.Max(1, work.Height - _barConfig.Height))
                : new Rect(
                    work.X, work.Y + _barConfig.Height,
                    work.Width, Math.Max(1, work.Height - _barConfig.Height));
            _monitors[info.Handle] = info with { WorkArea = adjusted };
        }

        // A display reconfiguration can transiently enumerate no displays
        // at all; keeping the last known displays beats acting on an
        // empty list.
        if (current.Count == 0)
            return;

        foreach (var disconnected in _monitors.Keys.Where(h => !current.Contains(h)).ToList())
            _monitors.Remove(disconnected);

        var primary = PrimaryMonitor().DeviceName;

        foreach (var output in _workspaces.OutputBindings.Values.Distinct().ToList())
        {
            if (devices.Contains(output))
                continue;

            var orphanedActive = _workspaces.GetActiveIndex(output);
            foreach (var (index, binding) in _workspaces.OutputBindings)
            {
                if (string.Equals(binding, output, StringComparison.OrdinalIgnoreCase))
                    _workspaces.Bind(index, primary);
            }

            _workspaces.ClearActive(output);
            if (orphanedActive is int moved && _workspaces.TryGet(moved)?.OutputName == primary)
                _workspaces.SetActiveOn(primary, moved);
        }

        // Every connected display ends up with an active workspace.
        foreach (var device in devices)
            _workspaces.ActiveOn(device);
    }

    /// <summary>
    /// Moves windows that were dragged to another display into the workspace
    /// that is active there.
    /// </summary>
    private void ReassignWindows()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            if (_workspaces.FindOwner(handle) is not { } workspace)
                continue;

            var hwnd = new IntPtr(handle);

            // Hidden windows (workspace members, tabs) follow their
            // workspace, never their stale physical position: reassigning
            // them would steal them into whatever workspace replaced the
            // display they happen to sit on the coordinates of.
            if (!Win32.IsWindowVisible(hwnd))
                continue;

            var device = MonitorFor(hwnd).DeviceName;
            if (string.Equals(workspace.OutputName, device, StringComparison.OrdinalIgnoreCase))
                continue;

            _workspaces.RemoveWindow(handle);
            _workspaces.AddWindow(handle, device);
            _placements.Remove(handle);

            // The window joined an active workspace; it must not stay hidden.
            if (!Win32.IsWindowVisible(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        }
    }

    /// <summary>
    /// Reacts to windows the user moved or resized manually: a resize along
    /// the window's split axis is applied so sibling windows adapt, and
    /// everything else snaps back to the tiled rectangle.
    /// </summary>
    private void ReconcileUserWindowChanges()
    {
        foreach (var handle in _managedHandles.ToList())
        {
            var hwnd = new IntPtr(handle);
            if (Win32.IsIconic(hwnd) || !Win32.IsWindowVisible(hwnd) ||
                !_placements.TryGetValue(handle, out var placement) ||
                !Win32.GetWindowRect(hwnd, out var current))
            {
                continue;
            }

            if (NearlyAt(current, placement.Actual))
                continue;

            var widthChanged =
                Math.Abs(current.Right - current.Left - placement.Actual.Width) > PositionTolerance;
            var heightChanged =
                Math.Abs(current.Bottom - current.Top - placement.Actual.Height) > PositionTolerance;

            var applied = (widthChanged, heightChanged) switch
            {
                (true, false) => _workspaces.ApplyUserResize(handle, ResizeAxis.Width, current.Right - current.Left),
                (false, true) => _workspaces.ApplyUserResize(handle, ResizeAxis.Height, current.Bottom - current.Top),
                _ => false,
            };

            if (applied)
            {
                // The layout changes right after this; let it position the
                // window with the new weights.
                _placements.Remove(handle);
            }
            else
            {
                PositionWindow(handle, placement.Actual);
                if (Win32.GetWindowRect(hwnd, out var reverted))
                {
                    _placements[handle] = new LayoutPlacement(
                        placement.Requested,
                        new Rect(reverted.Left, reverted.Top,
                            reverted.Right - reverted.Left, reverted.Bottom - reverted.Top));
                }
            }
        }
    }

    private static bool NearlyAt(Win32.RECT current, Rect rect) =>
        Math.Abs(current.Left - rect.X) <= PositionTolerance &&
        Math.Abs(current.Top - rect.Y) <= PositionTolerance &&
        Math.Abs(current.Right - rect.Right) <= PositionTolerance &&
        Math.Abs(current.Bottom - rect.Bottom) <= PositionTolerance;

    private void DiscoverNewWindows()
    {
        Win32.EnumWindows((hwnd, _) =>
        {
            var handle = hwnd.ToInt64();
            if (_managedHandles.Contains(handle) ||
                _floatedByUser.Contains(handle) ||
                !Win32.IsWindow(hwnd))
                return true;

            // A for_window move scratchpad rule hides matching windows the
            // moment they appear — once per window, so the scratchpad
            // show toggle owns them after that.
            if (!_scratchpadSeen.Contains(handle) && ScratchpadRuleFor(hwnd) is not null)
            {
                _scratchpadSeen.Add(handle);
                Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                AppLog.Info($"scratchpad: 0x{handle:X} hidden as it appeared");
                return true;
            }

            if (!IsTileable(hwnd, requireVisible: true))
                return true;

            var device = MonitorFor(hwnd).DeviceName;
            if (MoveRuleFor(hwnd) is { } move)
            {
                // A for_window move rule sends the window to its workspace
                // instead of the active one, hidden until shown there.
                _workspaces.AddWindowTo(move.Parameter, handle, device);
                _managedHandles.Add(handle);
                if (_workspaces.GetActiveIndex(device) != move.Parameter)
                {
                    Win32.ShowWindow(hwnd, Win32.SW_HIDE);
                    _hiddenByUs.Add(handle);
                }

                return true;
            }

            // New windows join the workspace the user is on, even when the
            // program opens its window on another display.
            _workspaces.AddWindow(handle, ActiveMonitor().DeviceName);
            _managedHandles.Add(handle);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// The first for_window rule that moves this window to a workspace, if
    /// the window matches one.
    /// </summary>
    /// <summary>
    /// The first for_window rule that sends this window to the
    /// scratchpad as it appears, if the window matches one.
    /// </summary>
    private WindowRule? ScratchpadRuleFor(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        Win32.GetClassName(hwnd, className, className.Capacity);
        var title = new StringBuilder(512);
        Win32.GetWindowText(hwnd, title, title.Capacity);

        return _rules.FirstOrDefault(rule =>
            rule.Action == WindowRuleAction.Scratchpad &&
            rule.Matches(className.ToString(), title.ToString()));
    }

    private WindowRule? MoveRuleFor(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        Win32.GetClassName(hwnd, className, className.Capacity);
        var title = new StringBuilder(512);
        Win32.GetWindowText(hwnd, title, title.Capacity);

        return _rules.FirstOrDefault(rule =>
            rule.Action == WindowRuleAction.MoveToWorkspace &&
            rule.Matches(className.ToString(), title.ToString()));
    }

    private void ShowAllWindows(int index)
    {
        if (_workspaces.TryGet(index) is not { } workspace)
            return;

        foreach (var handle in workspace.WindowHandles)
        {
            Win32.ShowWindow(new IntPtr(handle), Win32.SW_SHOW);
            _hiddenByUs.Remove(handle);
        }
    }

    private void PositionWindow(long handle, Rect rect)
    {
        var hwnd = new IntPtr(handle);

        // A maximized or minimized window ignores SetWindowPos; restore first.
        if (Win32.IsIconic(hwnd) || Win32.IsZoomed(hwnd))
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// The monitor that hotkeys act on: the one showing the foreground window,
    /// falling back to the primary display.
    /// </summary>
    private MonitorInfo ActiveMonitor()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            var monitor = Win32.MonitorFromWindow(foreground, Win32.MONITOR_DEFAULTTONEAREST);
            if (_monitors.TryGetValue(monitor, out var info))
                return info;
        }

        return PrimaryMonitor();
    }

    private MonitorInfo MonitorFor(IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (_monitors.TryGetValue(monitor, out var info))
            return info;

        // The window sits on a monitor we have not synced yet (or none at
        // all, transiently, during a display reconfiguration).
        ReconcileMonitors();
        return _monitors.TryGetValue(monitor, out info) ? info : PrimaryMonitor();
    }

    /// <summary>
    /// The primary display, re-syncing the monitor list first when it is
    /// empty, e.g. in the middle of a display reconfiguration.
    /// </summary>
    private MonitorInfo PrimaryMonitor()
    {
        if (_monitors.Count == 0)
            ReconcileMonitors();

        return _monitors.Values.FirstOrDefault(m => m.IsPrimary)
            ?? _monitors.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No displays are connected.");
    }

    /// <summary>
    /// Whether a window should be tiled. When <paramref name="requireVisible"/>
    /// is false, hidden windows pass too; the caller must then place them
    /// on a remembered workspace only.
    /// </summary>
    private bool IsTileable(IntPtr hwnd, bool requireVisible, bool ignoreFloatingRules = false)
    {
        if (!Win32.IsWindow(hwnd) || (requireVisible && !Win32.IsWindowVisible(hwnd)))
            return false;

        // Chromium parks the windows of restored sessions minimized far
        // off-screen, yet reports them as visible; they would reserve a tile
        // they never fill. A minimized window has not been shown this
        // session: it joins when restoring it fires EVENT_OBJECT_SHOW and
        // triggers the rescan.
        if (Win32.IsIconic(hwnd))
            return false;

        Win32.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == _ownProcessId)
            return false;

        var className = new StringBuilder(256);
        Win32.GetClassName(hwnd, className, className.Capacity);
        if (IgnoredClasses.Contains(className.ToString()))
            return false;

        var extendedStyle = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);

        // Tool windows and no-activate windows (notifications, tray
        // pop-ups) are chrome, not tiled applications.
        if ((extendedStyle & Win32.WS_EX_TOOLWINDOW) != 0 ||
            (extendedStyle & Win32.WS_EX_NOACTIVATE) != 0)
            return false;

        // Pop-ups without a caption are menus, flyouts and similar
        // transient surfaces; real application windows have a caption.
        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
        if ((style & Win32.WS_POPUP) != 0 && (style & Win32.WS_CAPTION) == 0)
            return false;

        // Owned top-level windows are dialog boxes, message boxes and the
        // like — transient surfaces of another window that ride above
        // their owner, never tiles. This is Win32's counterpart of i3
        // floating the dialog window type automatically.
        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero)
            return false;

        var title = new StringBuilder(512);
        if (Win32.GetWindowText(hwnd, title, title.Capacity) == 0)
            return false;

        // for_window floating rules decide first: matched windows are never
        // managed, like i3's floating enable leaves a window outside the
        // tiling; scratchpad rules leave the appearing pass to hide them.
        // Move rules leave discovery to assign the workspace.
        foreach (var rule in _rules)
        {
            if ((rule.Action == WindowRuleAction.Floating ||
                 rule.Action == WindowRuleAction.Scratchpad) &&
                !ignoreFloatingRules &&
                rule.Matches(className.ToString(), title.ToString()))
                return false;
        }

        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
            return false;

        if (!Win32.GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return false;

        return true;
    }
}
