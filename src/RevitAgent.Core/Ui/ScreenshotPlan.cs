namespace RevitAgent.Core.Ui;

/// <summary>How a screenshot was (or is to be) taken.</summary>
public enum ScreenshotMethod
{
    /// <summary>PrintWindow of the target window (works when covered).</summary>
    PrintWindow,

    /// <summary>The Bridge rendered the active view through the Revit API.</summary>
    RevitApi,

    /// <summary>Screen pixels of the target window rectangle, taken only when verified to show that window.</summary>
    Screen,
}

/// <summary>State of the interactive session as far as screen pixels are concerned.</summary>
public enum SessionRenderState
{
    Rendering,
    Locked,
    RdpDisconnected,
}

/// <summary>Facts the capture decision depends on (all observed after PrintWindow).</summary>
public sealed record ScreenshotFacts(
    bool Blank,
    bool IsRevitMainWindow,
    bool BridgeLoaded,
    SessionRenderState Session,
    bool Minimized,
    bool Occluded);

/// <summary>Outcome of <see cref="ScreenshotPlan.Decide"/>: a method, or the reason no trustworthy capture is possible.</summary>
public sealed record ScreenshotDecision(ScreenshotMethod? Method, string? FailureCode, string? FailureMessage)
{
    public bool IsFailure => Method is null;
}

/// <summary>
/// Pure decision logic of ui_screenshot. Fail closed: screen pixels are used only when the session renders, the window
/// is not minimized and nothing covers it; otherwise a precise TARGET_NOT_READY reason is returned.
/// </summary>
public static class ScreenshotPlan
{
    public const string Covered = "covered";
    public const string Minimized = "minimized";
    public const string SessionLocked = "session_locked";
    public const string DesktopNotRendered = "desktop_not_rendered";

    public const string Hint = "view_export_image renders the model view without the screen (Revit with the Bridge).";

    public static ScreenshotDecision Decide(ScreenshotFacts f)
    {
        if (!f.Blank)
        {
            return new ScreenshotDecision(ScreenshotMethod.PrintWindow, null, null);
        }

        if (f.IsRevitMainWindow && f.BridgeLoaded)
        {
            return new ScreenshotDecision(ScreenshotMethod.RevitApi, null, null);
        }

        if (f.Session == SessionRenderState.Locked)
        {
            return Fail(SessionLocked, "The Windows session is locked, so screen pixels are not the window.");
        }

        if (f.Session == SessionRenderState.RdpDisconnected)
        {
            return Fail(DesktopNotRendered, "The desktop is not rendered (disconnected Remote Desktop session), so screen pixels are not the window.");
        }

        if (f.Minimized)
        {
            return Fail(Minimized, "The window is minimized and nothing can be captured from it.");
        }

        if (f.Occluded)
        {
            return Fail(Covered, "The window renders black through PrintWindow and is covered by another window, so its screen pixels would show something else.");
        }

        return new ScreenshotDecision(ScreenshotMethod.Screen, null, null);
    }

    private static ScreenshotDecision Fail(string code, string message) => new(null, code, message + " " + Hint);
}
