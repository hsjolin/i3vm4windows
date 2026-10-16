using i3vm4windows.Core;
using Xunit;

namespace i3vm4windows.Core.Tests;

public sealed class WorkspaceManagerTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    private static LayoutOptions NoGaps() => new() { OuterMargin = 0, InnerGap = 0 };

    [Fact]
    public void Initially_Workspace1IsActive()
    {
        var manager = new WorkspaceManager();

        Assert.Equal(1, manager.ActiveIndex);
        Assert.Equal("1", manager.Active.Name);
    }

    [Fact]
    public void SwitchTo_ChangesActiveWorkspace()
    {
        var manager = new WorkspaceManager();

        Assert.True(manager.SwitchTo(3));
        Assert.Equal(3, manager.ActiveIndex);
        Assert.Equal("3", manager.Active.Name);
    }

    [Fact]
    public void SwitchTo_SameWorkspace_ReturnsFalse()
    {
        var manager = new WorkspaceManager();

        Assert.False(manager.SwitchTo(1));
        Assert.Equal(1, manager.ActiveIndex);
    }

    [Fact]
    public void AddWindow_GoesToActiveWorkspace()
    {
        var manager = new WorkspaceManager();
        manager.SwitchTo(2);

        manager.AddWindow(100);

        Assert.Equal(new[] { 100L }, manager.Get(2).WindowHandles);
        Assert.Empty(manager.Get(1).WindowHandles);
    }

    [Fact]
    public void RemoveWindow_SearchesAllWorkspaces()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100);
        manager.SwitchTo(2);
        manager.AddWindow(200);

        Assert.True(manager.RemoveWindow(100));

        Assert.Empty(manager.Get(1).WindowHandles);
        Assert.Equal(new[] { 200L }, manager.Get(2).WindowHandles);
    }

    [Fact]
    public void MoveFocusedTo_MovesFocusedWindowToTargetWorkspace()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100);
        manager.AddWindow(200);

        var moved = manager.MoveFocusedTo(2);

        Assert.Equal(200, moved);
        Assert.Equal(new[] { 100L }, manager.Get(1).WindowHandles);
        Assert.Equal(new[] { 200L }, manager.Get(2).WindowHandles);
    }

    [Fact]
    public void MoveFocusedTo_FallsBackFocusToRemainingWindow()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100);
        manager.AddWindow(200);

        manager.MoveFocusedTo(2);

        Assert.Equal(100, manager.Get(1).FocusedWindowHandle);
        Assert.Equal(200, manager.Get(2).FocusedWindowHandle);
    }

    [Fact]
    public void MoveFocusedTo_SameWorkspace_ReturnsNull()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100);

        Assert.Null(manager.MoveFocusedTo(1));
        Assert.Equal(new[] { 100L }, manager.Get(1).WindowHandles);
    }

    [Fact]
    public void SetFocusedWindow_UpdatesOwningWorkspace()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100);
        manager.SwitchTo(2);
        manager.AddWindow(200);

        Assert.True(manager.SetFocusedWindow(100));
        Assert.Equal(100, manager.Get(1).FocusedWindowHandle);
        Assert.Equal(2, manager.ActiveIndex);
    }

    [Fact]
    public void ComputeActiveLayout_OnlyIncludesActiveWorkspace()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100);
        manager.SwitchTo(2);
        manager.AddWindow(200);

        var layout = manager.ComputeActiveLayout(Monitor, NoGaps());

        var assignment = Assert.Single(layout);
        Assert.Equal(200, assignment.WindowHandle);
        Assert.Equal(Monitor, assignment.Rect);
    }
}
