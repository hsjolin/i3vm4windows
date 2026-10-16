using i3vm4windows.Core;
using Xunit;

namespace i3vm4windows.Core.Tests;

public sealed class WorkspaceManagerTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    private static LayoutOptions NoGaps() => new() { OuterMargin = 0, InnerGap = 0 };

    [Fact]
    public void Initially_EachOutputGetsAConsecutiveWorkspace()
    {
        var manager = new WorkspaceManager();

        Assert.Equal(1, manager.ActiveOn("a").Index);
        Assert.Equal("a", manager.TryGet(1)?.OutputName);
        Assert.Equal(2, manager.ActiveOn("b").Index);
        Assert.Equal("b", manager.TryGet(2)?.OutputName);
    }

    [Fact]
    public void SwitchTo_NewWorkspace_CreatedOnFocusedOutput()
    {
        var manager = new WorkspaceManager();

        var result = manager.SwitchTo(3, "b");

        var change = Assert.Single(result.Changes);
        Assert.True(change.Changed);
        Assert.Equal("b", change.Output);
        Assert.Equal(3, manager.GetActiveIndex("b"));
        Assert.Equal("b", manager.TryGet(3)?.OutputName);
    }

    [Fact]
    public void SwitchTo_WorkspaceOnOtherOutput_SwapsWithFocusedActive()
    {
        var manager = new WorkspaceManager();
        manager.ActiveOn("a");
        manager.ActiveOn("b");

        var result = manager.SwitchTo(2, "a");

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("a", result.Changes[0].Output);
        Assert.Equal(1, result.Changes[0].PreviousIndex);
        Assert.Equal(2, result.Changes[0].NewIndex);
        Assert.Equal("b", result.Changes[1].Output);
        Assert.Equal(2, result.Changes[1].PreviousIndex);
        Assert.Equal(1, result.Changes[1].NewIndex);
        Assert.Equal("a", manager.TryGet(2)?.OutputName);
        Assert.Equal("b", manager.TryGet(1)?.OutputName);
        Assert.Equal(2, manager.GetActiveIndex("a"));
        Assert.Equal(1, manager.GetActiveIndex("b"));
    }

    [Fact]
    public void SwitchTo_InactiveWorkspaceOnOtherOutput_SwapsInAndDisplacesActive()
    {
        var manager = new WorkspaceManager();
        manager.ActiveOn("a");
        manager.ActiveOn("b");
        manager.SwitchTo(3, "a");

        // Workspace 1 is bound to "a" but inactive there; "a" shows 3.
        var result = manager.SwitchTo(1, "b");

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("b", result.Changes[0].Output);
        Assert.Equal(2, result.Changes[0].PreviousIndex);
        Assert.Equal(1, result.Changes[0].NewIndex);
        Assert.Equal("a", result.Changes[1].Output);
        Assert.Equal(3, result.Changes[1].PreviousIndex);
        Assert.Equal(2, result.Changes[1].NewIndex);

        Assert.Equal(1, manager.GetActiveIndex("b"));
        Assert.Equal(2, manager.GetActiveIndex("a"));
        Assert.Equal("b", manager.TryGet(1)?.OutputName);
        Assert.Equal("a", manager.TryGet(2)?.OutputName);
        Assert.Equal("a", manager.TryGet(3)?.OutputName);
    }

    [Fact]
    public void SwitchTo_AlreadyActiveOnFocusedOutput_ReportedUnchanged()
    {
        var manager = new WorkspaceManager();
        manager.SwitchTo(2, "a");

        var result = manager.SwitchTo(2, "a");

        var change = Assert.Single(result.Changes);
        Assert.False(change.Changed);
        Assert.Equal(2, manager.GetActiveIndex("a"));
    }

    [Fact]
    public void MoveActiveWorkspaceToOutput_SwapsActiveWorkspaces()
    {
        var manager = new WorkspaceManager();
        manager.ActiveOn("a");
        manager.ActiveOn("b");
        manager.SwitchTo(3, "a");

        var result = manager.MoveActiveWorkspaceToOutput("a", "b");

        Assert.NotNull(result);
        Assert.Equal(3, result.MovedIndex);
        Assert.Equal("b", result.TargetOutput);
        Assert.Equal(2, result.SourceReplacementIndex);
        Assert.Equal("b", manager.TryGet(3)?.OutputName);
        Assert.Equal(3, manager.GetActiveIndex("b"));
        Assert.Equal(2, manager.GetActiveIndex("a"));
        Assert.Equal("a", manager.TryGet(1)?.OutputName);
    }

    [Fact]
    public void MoveActiveWorkspaceToOutput_TargetWithoutWorkspace_CreatesSwapPartner()
    {
        var manager = new WorkspaceManager();
        manager.ActiveOn("a");

        var result = manager.MoveActiveWorkspaceToOutput("a", "b");

        Assert.NotNull(result);
        Assert.Equal(1, result.MovedIndex);
        Assert.Equal(2, result.SourceReplacementIndex);
        Assert.Equal("a", manager.TryGet(2)?.OutputName);
        Assert.Equal(2, manager.GetActiveIndex("a"));
        Assert.Equal(1, manager.GetActiveIndex("b"));
    }

    [Fact]
    public void MoveActiveWorkspaceToOutput_SameOutput_ReturnsNull()
    {
        var manager = new WorkspaceManager();

        Assert.Null(manager.MoveActiveWorkspaceToOutput("a", "A"));
    }

    [Fact]
    public void MoveFocusedTo_MovesFocusedWindowToTargetWorkspace()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.AddWindow(200, "a");

        var move = manager.MoveFocusedTo(2, "a");

        Assert.NotNull(move);
        Assert.Equal(200, move.WindowHandle);
        Assert.Equal(2, move.Target.Index);
        Assert.Equal(new[] { 100L }, manager.TryGet(1)?.WindowHandles);
        Assert.Equal(new[] { 200L }, manager.TryGet(2)?.WindowHandles);
    }

    [Fact]
    public void MoveFocusedTo_TargetCreatedOnSourceOutput()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");

        var move = manager.MoveFocusedTo(2, "a");

        Assert.Equal("a", move?.Target.OutputName);
    }

    [Fact]
    public void MoveFocusedTo_SameWorkspace_ReturnsNull()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");

        Assert.Null(manager.MoveFocusedTo(1, "a"));
        Assert.Equal(new[] { 100L }, manager.TryGet(1)?.WindowHandles);
    }

    [Fact]
    public void MoveFocusedTo_FallsBackFocusToRemainingWindow()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.AddWindow(200, "a");

        manager.MoveFocusedTo(2, "a");

        Assert.Equal(100, manager.TryGet(1)?.FocusedWindowHandle);
        Assert.Equal(200, manager.TryGet(2)?.FocusedWindowHandle);
    }

    [Fact]
    public void AddWindow_GoesToActiveWorkspaceOfOutput()
    {
        var manager = new WorkspaceManager();
        manager.SwitchTo(2, "a");

        manager.AddWindow(100, "a");

        Assert.Equal(new[] { 100L }, manager.TryGet(2)?.WindowHandles);
        Assert.Empty(manager.TryGet(1)?.WindowHandles ?? Array.Empty<long>());
    }

    [Fact]
    public void AddWindowTo_PlacesWindowInGivenWorkspace_NotActive()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");

        manager.AddWindowTo(3, 200, "a");

        Assert.Equal(new[] { 100L }, manager.TryGet(1)?.WindowHandles);
        Assert.Equal(new[] { 200L }, manager.TryGet(3)?.WindowHandles);
        Assert.Equal("a", manager.TryGet(3)?.OutputName);
        Assert.Equal(1, manager.GetActiveIndex("a"));
    }

    [Fact]
    public void RemoveWindow_SearchesAllWorkspaces()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.SwitchTo(2, "a");
        manager.AddWindow(200, "a");

        Assert.True(manager.RemoveWindow(100));

        Assert.Empty(manager.TryGet(1)?.WindowHandles ?? Array.Empty<long>());
        Assert.Equal(new[] { 200L }, manager.TryGet(2)?.WindowHandles);
    }

    [Fact]
    public void SetFocusedWindow_UpdatesOwningWorkspace()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.SwitchTo(2, "a");
        manager.AddWindow(200, "a");

        Assert.True(manager.SetFocusedWindow(100));
        Assert.Equal(100, manager.TryGet(1)?.FocusedWindowHandle);
        Assert.Equal(2, manager.GetActiveIndex("a"));
    }

    [Fact]
    public void ComputeActiveLayout_OnlyIncludesActiveWorkspaceOfOutput()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.SwitchTo(2, "a");
        manager.AddWindow(200, "a");

        var layout = manager.ComputeActiveLayout("a", Monitor, NoGaps());

        var assignment = Assert.Single(layout);
        Assert.Equal(200, assignment.WindowHandle);
        Assert.Equal(Monitor, assignment.Rect);
    }

    [Fact]
    public void FocusRelative_MovesFocusOnActiveWorkspaceOfOutput()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.AddWindow(200, "a");

        Assert.True(manager.FocusRelative("a", 1));
        Assert.Equal(100, manager.ActiveOn("a").FocusedWindowHandle);
    }

    [Fact]
    public void MoveFocused_MovesWithinActiveWorkspaceOfOutput()
    {
        var manager = new WorkspaceManager();
        manager.AddWindow(100, "a");
        manager.AddWindow(200, "a");
        manager.AddWindow(300, "a");

        Assert.True(manager.MoveFocused("a", -1));

        Assert.Equal(new[] { 100L, 300L, 200L }, manager.TryGet(1)?.WindowHandles);
    }
}
