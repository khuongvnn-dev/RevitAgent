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
