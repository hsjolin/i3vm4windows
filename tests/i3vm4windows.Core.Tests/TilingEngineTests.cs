using i3vm4windows.Core;
using Xunit;

namespace i3vm4windows.Core.Tests;

public sealed class TilingEngineTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    private static (Workspace Workspace, TilingEngine Engine) Create(params long[] handles)
    {
        var workspace = new Workspace(1, "test", LayoutMode.Split);
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

        [Fact]
        public void ThirdWindow_AfterResize_TakesAnEqualShareOfTheSplit()
        {
            var (workspace, engine) = Create(100, 200);
            var options = NoGaps();

            // A resize turns the weights into pixel units.
            engine.ComputeLayout(workspace, Monitor, options);
            Assert.True(engine.ApplyUserResize(workspace, 100, ResizeAxis.Width, 600));

            engine.AddWindow(workspace, 300);
            var layout = engine.ComputeLayout(workspace, Monitor, options);

            Assert.Equal(3, layout.Count);
            // The newcomer takes a third of the area; the resized pair
            // shrinks proportionally, keeping its ratio.
            Assert.Equal(640, layout.Single(a => a.WindowHandle == 300).Rect.Width);
            Assert.Equal(400, layout.Single(a => a.WindowHandle == 100).Rect.Width);
            Assert.Equal(880, layout.Single(a => a.WindowHandle == 200).Rect.Width);
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

    public sealed class ResizeTests
    {
        [Fact]
        public void WidthResize_HorizontalSplit_WindowKeepsSizeSiblingsAdapt()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.True(engine.ApplyUserResize(workspace, 100, ResizeAxis.Width, 600));

            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());
            Assert.Equal(600, layout[0].Rect.Width);
            Assert.Equal(1320, layout[1].Rect.Width);
        }

        [Fact]
        public void HeightResize_VerticalSplit_WindowKeepsSizeSiblingsAdapt()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ToggleOrientation(workspace);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.True(engine.ApplyUserResize(workspace, 100, ResizeAxis.Height, 400));

            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());
            Assert.Equal(400, layout[0].Rect.Height);
            Assert.Equal(680, layout[1].Rect.Height);
        }

        [Fact]
        public void Resize_AcrossSplitAxis_ReturnsFalse()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.False(engine.ApplyUserResize(workspace, 100, ResizeAxis.Height, 500));
        }

        [Fact]
        public void Resize_SingleWindow_ReturnsFalse()
        {
            var (workspace, engine) = Create(100);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.False(engine.ApplyUserResize(workspace, 100, ResizeAxis.Width, 600));
        }

        [Fact]
        public void Resize_TooSmall_ReturnsFalse()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.False(engine.ApplyUserResize(workspace, 100, ResizeAxis.Width, 10));
        }

        [Fact]
        public void Resize_TooLarge_ReturnsFalse()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.False(engine.ApplyUserResize(workspace, 100, ResizeAxis.Width, 1900));
        }

        [Fact]
        public void Resize_UnknownWindow_ReturnsFalse()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.False(engine.ApplyUserResize(workspace, 999, ResizeAxis.Width, 600));
        }
    }

    public sealed class TabbedLayoutTests
    {
        [Fact]
        public void TabbedContainer_ShowsOnlyTheFocusedWindow()
        {
            var (workspace, engine) = Create(100, 200);
            engine.SetLayoutMode(workspace, LayoutMode.Tabbed);
            workspace.FocusedWindowHandle = 200;

            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());

            var assignment = Assert.Single(layout);
            Assert.Equal(200, assignment.WindowHandle);
            Assert.Equal(Monitor, assignment.Rect);
        }

        [Fact]
        public void SwitchingFocus_SwitchesTheVisibleTab()
        {
            var (workspace, engine) = Create(100, 200);
            engine.SetLayoutMode(workspace, LayoutMode.Tabbed);
            workspace.FocusedWindowHandle = 100;

            var first = engine.ComputeLayout(workspace, Monitor, NoGaps());
            workspace.FocusedWindowHandle = 200;
            var second = engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.Equal(100, Assert.Single(first).WindowHandle);
            Assert.Equal(200, Assert.Single(second).WindowHandle);
        }

        [Fact]
        public void WithoutFocus_TheFirstChildShows()
        {
            var (workspace, engine) = Create(100, 200);
            engine.SetLayoutMode(workspace, LayoutMode.Tabbed);
            workspace.FocusedWindowHandle = null;

            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.Equal(100, Assert.Single(layout).WindowHandle);
        }

        [Fact]
        public void ToggleOrientation_LeavesTabbedMode()
        {
            var (workspace, engine) = Create(100, 200);
            engine.SetLayoutMode(workspace, LayoutMode.Tabbed);

            engine.ToggleOrientation(workspace);
            var layout = engine.ComputeLayout(workspace, Monitor, NoGaps());

            Assert.Equal(2, layout.Count);
        }
    }

    public sealed class MinWindowWidthTests
    {
        private static LayoutOptions Cards(int minWidth) => new()
        {
            OuterMargin = 0,
            InnerGap = 0,
            MinWindowWidth = minWidth,
        };

        [Fact]
        public void NarrowSplit_OverlapsWindowsLikeACardDeck()
        {
            var (workspace, engine) = Create(100, 200, 300);

            var layout = engine.ComputeLayout(workspace, new Rect(0, 0, 900, 500), Cards(400));

            Assert.Equal(3, layout.Count);
            // Every card keeps the minimum width and full height.
            Assert.All(layout, a => Assert.Equal(400, a.Rect.Width));
            Assert.All(layout, a => Assert.Equal(500, a.Rect.Height));
            // The deck spreads across the area: each card overlaps the one
            // to its left and the last one ends at the right edge.
            Assert.Equal(0, layout[0].Rect.X);
            Assert.Equal(250, layout[1].Rect.X);
            Assert.Equal(500, layout[2].Rect.X);
            Assert.Equal(900, layout[2].Rect.Right);
        }

        [Fact]
        public void WideEnoughSplit_TilesNormally()
        {
            var (workspace, engine) = Create(100, 200);

            var layout = engine.ComputeLayout(workspace, new Rect(0, 0, 900, 500), Cards(400));

            Assert.Equal(2, layout.Count);
            Assert.Equal(450, layout[0].Rect.Width);
            Assert.Equal(450, layout[1].Rect.Width);
            Assert.Equal(450, layout[1].Rect.X);
        }

        [Fact]
        public void BoundarySpan_TurnsIntoACardDeck()
        {
            var (workspace, engine) = Create(100, 200);

            // Exactly the minimum for both children: still a normal tiling.
            var tiled = engine.ComputeLayout(workspace, new Rect(0, 0, 800, 500), Cards(400));
            Assert.Equal(400, tiled[0].Rect.Width);
            Assert.Equal(400, tiled[1].Rect.Width);

            // One pixel less and the deck takes over.
            var deck = engine.ComputeLayout(workspace, new Rect(0, 0, 799, 500), Cards(400));
            Assert.Equal(400, deck[0].Rect.Width);
            Assert.Equal(400, deck[1].Rect.Width);
            Assert.Equal(399, deck[1].Rect.X);
        }

        [Fact]
        public void VerticalSplit_IgnoresTheMinimumWidth()
        {
            var (workspace, engine) = Create(100, 200);
            engine.ToggleOrientation(workspace);

            var layout = engine.ComputeLayout(workspace, new Rect(0, 0, 700, 500), Cards(400));

            Assert.Equal(2, layout.Count);
            // Stacked windows keep the full width; the minimum applies to
            // horizontal splits only.
            Assert.All(layout, a => Assert.Equal(700, a.Rect.Width));
            Assert.Equal(250, layout[0].Rect.Height);
            Assert.Equal(250, layout[1].Rect.Height);
        }

        [Fact]
        public void NarrowerThanTheMinimum_ClampsTheCardWidth()
        {
            var (workspace, engine) = Create(100, 200);

            var layout = engine.ComputeLayout(workspace, new Rect(0, 0, 300, 500), Cards(400));

            // Not even one card fits at the minimum: the deck collapses,
            // every card the full (narrow) width of the area.
            Assert.Equal(2, layout.Count);
            Assert.All(layout, a => Assert.Equal(300, a.Rect.Width));
            Assert.All(layout, a => Assert.Equal(0, a.Rect.X));
        }
    }
}
