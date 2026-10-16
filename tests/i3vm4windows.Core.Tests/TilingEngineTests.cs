using i3vm4windows.Core;
using Xunit;

namespace i3vm4windows.Core.Tests;

public sealed class TilingEngineTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    private static (Workspace Workspace, TilingEngine Engine) Create(params long[] handles)
    {
        var workspace = new Workspace("1");
        var engine = new TilingEngine();
        foreach (var handle in handles)
            engine.AddWindow(workspace, handle);
        return (workspace, engine);
    }

    private static LayoutOptions NoGaps() => new() { OuterMargin = 0, InnerGap = 0 };

    public sealed class AddWindowTests
    {
        [Fact]
        public void FirstWindow_BecomesOnlyChildOfRoot()
        {
            var (workspace, _) = Create(100);

            var handle = Assert.Single(workspace.WindowHandles);
            Assert.Equal(100, handle);
            Assert.Equal(100, workspace.FocusedWindowHandle);
        }

        [Fact]
        public void DuplicateHandle_IsIgnored()
        {
            var (workspace, _) = Create(100, 100);

            var handle = Assert.Single(workspace.WindowHandles);
            Assert.Equal(100, handle);
        }
    }

    public sealed class ComputeLayoutTests
    {
        [Fact]
        public void SingleWindow_CoversUsableArea()
        {
            var (workspace, engine) = Create(100);
            var options = NoGaps();

            var layout = engine.ComputeLayout(workspace, Monitor, options);

            var assignment = Assert.Single(layout);
            Assert.Equal(100, assignment.WindowHandle);
            Assert.Equal(Monitor, assignment.Rect);
        }

        [Fact]
        public void HorizontalSplit_DividesWidthEqually()
        {
            var (workspace, engine) = Create(100, 200);
            var options = NoGaps();

            var layout = engine.ComputeLayout(workspace, Monitor, options);

            Assert.Equal(2, layout.Count);
            Assert.Equal(100, layout[0].WindowHandle);
            Assert.Equal(200, layout[1].WindowHandle);
            Assert.Equal(960, layout[0].Rect.Width);
            Assert.Equal(960, layout[1].Rect.Width);
            Assert.Equal(0, layout[0].Rect.X);
            Assert.Equal(960, layout[1].Rect.X);
            Assert.Equal(Monitor.Height, layout[0].Rect.Height);
        }

        [Fact]
        public void VerticalSplit_DividesHeightEqually()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ToggleOrientation(workspace);
            var options = NoGaps();

            var layout = engine.ComputeLayout(workspace, Monitor, options);

            Assert.Equal(2, layout.Count);
            Assert.Equal(540, layout[0].Rect.Height);
            Assert.Equal(540, layout[1].Rect.Height);
            Assert.Equal(Monitor.Width, layout[0].Rect.Width);
        }

        [Fact]
        public void InnerGap_IsAppliedBetweenWindows()
        {
            var (workspace, engine) = Create(100, 200);
            var options = new LayoutOptions { OuterMargin = 0, InnerGap = 20 };

            var layout = engine.ComputeLayout(workspace, Monitor, options);

            Assert.Equal(950, layout[0].Rect.Width);
            Assert.Equal(950, layout[1].Rect.Width);
            Assert.Equal(970, layout[1].Rect.X);
        }

        [Fact]
        public void OuterMargin_ShrinksUsableArea()
        {
            var (workspace, engine) = Create(100);
            var options = new LayoutOptions { OuterMargin = 10, InnerGap = 0 };

            var layout = engine.ComputeLayout(workspace, Monitor, options);

            Assert.Equal(new Rect(10, 10, 1900, 1060), layout[0].Rect);
        }

        [Fact]
        public void FullscreenWindow_GetsWholeUsableArea()
        {
            var (workspace, engine) = Create(100, 200);
            workspace.Root.Windows().First(w => w.WindowHandle == 100).IsFullscreen = true;

            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());

            var assignment = Assert.Single(layout);
            Assert.Equal(100, assignment.WindowHandle);
            Assert.Equal(Monitor, assignment.Rect);
        }
    }

    public sealed class RemoveWindowTests
    {
        [Fact]
        public void RemovedWindow_DisappearsFromTreeAndLayout()
        {
            var (workspace, engine) = Create(100, 200);

            var removed = engine.RemoveWindow(workspace, 100);
            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.True(removed);
            var assignment = Assert.Single(layout);
            Assert.Equal(200, assignment.WindowHandle);
        }

        [Fact]
        public void RemovingLastWindow_ClearsFocus()
        {
            var (workspace, engine) = Create(100);

            engine.RemoveWindow(workspace, 100);

            Assert.Empty(workspace.WindowHandles);
            Assert.Null(workspace.FocusedWindowHandle);
        }

        [Fact]
        public void RemovingFocusedWindow_MovesFocusToRemainingWindow()
        {
            var (workspace, engine) = Create(100, 200);

            engine.RemoveWindow(workspace, 200);

            Assert.Equal(100, workspace.FocusedWindowHandle);
        }

        [Fact]
        public void UnknownHandle_ReturnsFalse()
        {
            var (workspace, engine) = Create(100);

            Assert.False(engine.RemoveWindow(workspace, 999));
        }
    }

    public sealed class FocusTests
    {
        [Fact]
        public void FocusRelative_WrapsAroundBothEnds()
        {
            // Focus ends up on the most recently added window (300).
            var (workspace, engine) = Create(100, 200, 300);

            Assert.True(engine.FocusRelative(workspace, 1));
            Assert.Equal(100, workspace.FocusedWindowHandle);

            Assert.True(engine.FocusRelative(workspace, -1));
            Assert.Equal(300, workspace.FocusedWindowHandle);

            Assert.True(engine.FocusRelative(workspace, 2));
            Assert.Equal(200, workspace.FocusedWindowHandle);
        }

        [Fact]
        public void FocusRelative_WithoutFocusedWindow_PicksFirstOrLast()
        {
            var (workspace, engine) = Create(100, 200);
            workspace.FocusedWindowHandle = null;

            Assert.True(engine.FocusRelative(workspace, 1));
            Assert.Equal(100, workspace.FocusedWindowHandle);

            workspace.FocusedWindowHandle = null;
            Assert.True(engine.FocusRelative(workspace, -1));
            Assert.Equal(200, workspace.FocusedWindowHandle);
        }

        [Fact]
        public void FocusRelative_OnEmptyWorkspace_ReturnsFalse()
        {
            var (workspace, engine) = Create();

            Assert.False(engine.FocusRelative(workspace, 1));
        }
    }

    public sealed class MoveTests
    {
        [Fact]
        public void MoveFocused_SwapsLayoutOrder()
        {
            var (workspace, engine) = Create(100, 200);
            workspace.FocusedWindowHandle = 200;

            Assert.True(engine.MoveFocused(workspace, -1));
            Assert.Equal(new[] { 200L, 100L }, workspace.WindowHandles);

            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());
            Assert.Equal(0, layout[0].Rect.X);
            Assert.Equal(200, layout[0].WindowHandle);
            Assert.Equal(960, layout[1].Rect.X);
            Assert.Equal(100, layout[1].WindowHandle);
        }

        [Fact]
        public void MoveFocused_AtSplitBoundary_ReturnsFalse()
        {
            var (workspace, engine) = Create(100, 200);
            workspace.FocusedWindowHandle = 200;

            Assert.False(engine.MoveFocused(workspace, 1));
        }
    }

    public sealed class OrientationTests
    {
        [Fact]
        public void ToggleOrientation_FlipsBackAndForth()
        {
            var (workspace, engine) = Create(100, 200);

            engine.ToggleOrientation(workspace);
            Assert.Equal(SplitOrientation.Vertical, workspace.Root.Orientation);

            engine.ToggleOrientation(workspace);
            Assert.Equal(SplitOrientation.Horizontal, workspace.Root.Orientation);
        }
    }
}
