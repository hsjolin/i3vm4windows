using i3vm4windows.Core;
using Xunit;

namespace i3vm4windows.Core.Tests;

public sealed class AppStateTests
{
    [Fact]
    public void ResolveWorkspace_ExactImageAndTitleMatchWins()
    {
        var state = new AppState
        {
            Windows = new()
            {
                new WindowState(@"C:\app\app.exe", "Other", 2),
                new WindowState(@"C:\app\app.exe", "Report", 4),
            },
        };

        Assert.Equal(4, state.ResolveWorkspace(@"C:\APP\app.exe", "Report"));
    }

    [Fact]
    public void ResolveWorkspace_FallsBackToProcessImage()
    {
        var state = new AppState
        {
            Windows = new() { new WindowState(@"C:\app\app.exe", "Report", 4) },
        };

        Assert.Equal(4, state.ResolveWorkspace(@"C:\app\app.exe", "New document"));
    }

    [Fact]
    public void ResolveWorkspace_UnknownWindow_ReturnsNull()
    {
        var state = new AppState
        {
            Windows = new() { new WindowState(@"C:\app\app.exe", "Report", 4) },
        };

        Assert.Null(state.ResolveWorkspace(@"C:\other\other.exe", "Report"));
    }

    [Fact]
    public void ResolveWorkspace_Empty_ReturnsNull()
    {
        var state = new AppState();

        Assert.Null(state.ResolveWorkspace(@"C:\app\app.exe", "Report"));
    }

    [Fact]
    public void ResolveWorkspaceExact_RequiresTitleMatch()
    {
        var state = new AppState
        {
            Windows = new() { new WindowState(@"C:\app\app.exe", "Report", 4) },
        };

        Assert.Equal(4, state.ResolveWorkspaceExact(@"C:\app\app.exe", "Report"));
        Assert.Null(state.ResolveWorkspaceExact(@"C:\app\app.exe", "Other"));
    }

    [Fact]
    public void MergeRemembered_KeepsPlacementsNotSeenAgain()
    {
        var previous = new AppState
        {
            Windows = new()
            {
                new WindowState(@"C:\app\app.exe", "Report", 4),
                new WindowState(@"C:\gone\gone.exe", "Old", 2),
            },
        };
        var current = new AppState
        {
            Windows = new() { new WindowState(@"C:\app\app.exe", "Report", 5) },
        };

        current.MergeRemembered(previous);

        Assert.Equal(5, current.ResolveWorkspace(@"C:\app\app.exe", "Report"));
        Assert.Equal(2, current.ResolveWorkspace(@"C:\gone\gone.exe", "Old"));
        Assert.Equal(2, current.Windows.Count);
    }

    [Fact]
    public void MergeRemembered_KeepsActiveWorkspaceOfAbsentMonitors()
    {
        var previous = new AppState
        {
            ActiveWorkspaceByMonitor = new() { ["\\\\.\\DISPLAY1"] = 2, ["\\\\.\\DISPLAY2"] = 3 },
        };
        var current = new AppState
        {
            ActiveWorkspaceByMonitor = new() { ["\\\\.\\DISPLAY1"] = 1 },
        };

        current.MergeRemembered(previous);

        Assert.Equal(1, current.ActiveWorkspaceByMonitor["\\\\.\\DISPLAY1"]);
        Assert.Equal(3, current.ActiveWorkspaceByMonitor["\\\\.\\DISPLAY2"]);
    }

    [Fact]
    public void MergeRemembered_TrimsToMostRecentEntries()
    {
        var previous = new AppState
        {
            Windows = Enumerable.Range(1, 10)
                .Select(i => new WindowState($"C:\\app\\{i}.exe", $"W{i}", i))
                .ToList(),
        };
        var current = new AppState
        {
            Windows = new() { new WindowState(@"C:\new\new.exe", "New", 1) },
        };

        current.MergeRemembered(previous, maxRememberedWindows: 5);

        Assert.Equal(5, current.Windows.Count);
        Assert.Equal(1, current.ResolveWorkspace(@"C:\new\new.exe", "New"));
        Assert.Equal(4, current.ResolveWorkspace(@"C:\app\4.exe", "W4"));
        Assert.Null(current.ResolveWorkspace(@"C:\app\5.exe", "W5"));
    }
}
