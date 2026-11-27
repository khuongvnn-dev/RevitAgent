using RevitAgent.Core.Ui;
using Xunit;

namespace RevitAgent.Tests.Ui;

/// <summary>Windowless: the pure capture decision of ui_screenshot.</summary>
public sealed class ScreenshotPlanTests
{
    private static ScreenshotFacts Facts(
        bool blank = true,
        bool revitMain = false,
        bool bridge = false,
        SessionRenderState session = SessionRenderState.Rendering,
        bool minimized = false,
        bool occluded = false) => new(blank, revitMain, bridge, session, minimized, occluded);

    [Fact]
    public void NotBlank_UsesPrintWindow_EvenWhenCovered()
    {
        var d = ScreenshotPlan.Decide(Facts(blank: false, occluded: true));
        Assert.Equal(ScreenshotMethod.PrintWindow, d.Method);
    }

    [Fact]
    public void BlankRevitMainWithBridge_UsesRevitApi_EvenWhenLockedMinimizedOrCovered()
    {
        var d = ScreenshotPlan.Decide(Facts(revitMain: true, bridge: true, session: SessionRenderState.RdpDisconnected, minimized: true, occluded: true));
        Assert.Equal(ScreenshotMethod.RevitApi, d.Method);
    }

    [Fact]
    public void BlankRevitMainWithoutBridge_FallsThroughToScreenChecks()
    {
        Assert.Equal(ScreenshotMethod.Screen, ScreenshotPlan.Decide(Facts(revitMain: true)).Method);
        Assert.Equal(ScreenshotPlan.Covered, ScreenshotPlan.Decide(Facts(revitMain: true, occluded: true)).FailureCode);
    }

    [Fact]
    public void BlankOtherWindow_VisibleAndRendering_UsesScreen()
    {
        Assert.Equal(ScreenshotMethod.Screen, ScreenshotPlan.Decide(Facts(bridge: true)).Method);
    }

    [Theory]
    [InlineData(SessionRenderState.Locked, false, false, ScreenshotPlan.SessionLocked)]
    [InlineData(SessionRenderState.RdpDisconnected, false, false, ScreenshotPlan.DesktopNotRendered)]
    [InlineData(SessionRenderState.Rendering, true, false, ScreenshotPlan.Minimized)]
    [InlineData(SessionRenderState.Rendering, false, true, ScreenshotPlan.Covered)]
    [InlineData(SessionRenderState.Locked, true, true, ScreenshotPlan.SessionLocked)]
    [InlineData(SessionRenderState.Rendering, true, true, ScreenshotPlan.Minimized)]
    public void BlankAndNotSafe_FailsClosedWithReason(SessionRenderState session, bool minimized, bool occluded, string reason)
    {
        var d = ScreenshotPlan.Decide(Facts(session: session, minimized: minimized, occluded: occluded));
        Assert.True(d.IsFailure);
        Assert.Null(d.Method);
        Assert.Equal(reason, d.FailureCode);
        Assert.Contains("view_export_image", d.FailureMessage);
    }
}

public sealed class ScreenGeometryTests
{
    [Fact]
    public void Intersect_ClipsMaximizedFrameToWorkArea()
    {
        var frame = new System.Drawing.Rectangle(-8, -8, 1936, 1096);
        var work = new System.Drawing.Rectangle(0, 0, 1920, 1040);
        Assert.Equal(work, ScreenGeometry.Intersect(frame, work));
    }

    [Fact]
    public void Intersect_Disjoint_IsEmpty() =>
        Assert.True(ScreenGeometry.Intersect(new(0, 0, 10, 10), new(20, 20, 5, 5)).IsEmpty);

    [Fact]
    public void GridPoints_StayInsideRect_AndCountIsStepsSquared()
    {
        var r = new System.Drawing.Rectangle(100, 50, 400, 300);
        var pts = ScreenGeometry.GridPoints(r, 12);
        Assert.Equal(144, pts.Count);
        Assert.All(pts, p => Assert.True(r.Contains(p)));
    }

    [Fact]
    public void GridPoints_TinyOrEmpty_DoesNotThrow()
    {
        Assert.Empty(ScreenGeometry.GridPoints(System.Drawing.Rectangle.Empty, 12));
        Assert.All(ScreenGeometry.GridPoints(new(0, 0, 3, 3), 4), p => Assert.True(p.X >= 0 && p.Y >= 0));
    }

    [Fact]
    public void Contains_DetectsFullyInside()
    {
        Assert.True(ScreenGeometry.Contains(new(0, 0, 100, 100), new(10, 10, 20, 20)));
        Assert.False(ScreenGeometry.Contains(new(0, 0, 100, 100), new(90, 90, 20, 20)));
    }
}
