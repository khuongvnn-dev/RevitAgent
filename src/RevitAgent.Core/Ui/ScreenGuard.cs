using System.Drawing;
using System.Runtime.InteropServices;
using RevitAgent.Core.Machine;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>
/// Facts that decide whether screen pixels of a window rectangle really show that window: the session renders,
/// and a grid of points across the rectangle all hit the window (or its own child windows). Feeds <see cref="ScreenshotPlan"/>.
/// </summary>
internal static class ScreenGuard
{
    private const int GridSteps = 12;

    /// <summary>Locked screen, or a disconnected Remote Desktop session: the screen is not what the user sees.</summary>
    public static SessionRenderState Session()
    {
        if (UiEnvironment.DesktopUnavailableReason() is not null)
        {
            return SessionRenderState.Locked;
        }

        return IsRdpDisconnected() ? SessionRenderState.RdpDisconnected : SessionRenderState.Rendering;
    }

    /// <summary>
    /// The visible rectangle of <paramref name="hwnd"/> in physical pixels: DWM extended frame bounds (no invisible resize
    /// border) clipped to the work area of its monitor. False when it is empty. Call inside <c>WithPhysicalDpi</c>.
    /// </summary>
    internal static bool TryGetVisibleRect(IntPtr hwnd, out Rectangle visible)
    {
        visible = Rectangle.Empty;
        if (!NativeMethods.GetVisibleBounds(hwnd, out var r))
        {
            return false;
        }

        var rect = ToRectangle(r);
        if (NativeMethods.GetWorkArea(hwnd, out var work))
        {
            rect = ScreenGeometry.Intersect(rect, ToRectangle(work));
        }

        visible = rect;
        return rect.Width > 0 && rect.Height > 0;
    }

    private static Rectangle ToRectangle(NativeMethods.Rect r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>
    /// True unless every sample point of a grid over the visible window rectangle hits <paramref name="hwnd"/> itself or one of its
    /// child windows (WindowFromPoint, root ancestor, same process) and no visible window above it in the z-order overlaps it.
    /// Fails closed: any doubt means occluded.
    /// </summary>
    public static bool IsOccluded(IntPtr hwnd)
    {
        return NativeMethods.WithPhysicalDpi(() =>
        {
            if (!TryGetVisibleRect(hwnd, out var r))
            {
                return true;
            }

            var pid = NativeMethods.GetWindowProcessId(hwnd);
            foreach (var pt in ScreenGeometry.GridPoints(r, GridSteps))
            {
                var hit = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = pt.X, Y = pt.Y });
                if (hit == IntPtr.Zero
                    || NativeMethods.GetAncestor(hit, NativeMethods.GaRoot) != hwnd
                    || NativeMethods.GetWindowProcessId(hit) != pid)
                {
                    return true;
                }
            }

            return HasOverlappingWindowAbove(hwnd, r, pid);
        });
    }

    private static bool HasOverlappingWindowAbove(IntPtr hwnd, Rectangle target, int pid)
    {
        var cur = NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndPrev);
        for (var guard = 0; cur != IntPtr.Zero; guard++)
        {
            if (guard > 2000)
            {
                return true;
            }

            if (NativeMethods.IsWindowVisible(cur) && !NativeMethods.IsIconic(cur) && !NativeMethods.IsCloaked(cur))
            {
                if (!NativeMethods.GetVisibleBounds(cur, out var other))
                {
                    return true;
                }

                var o = ToRectangle(other);
                if (!ScreenGeometry.Intersect(target, o).IsEmpty)
                {
                    var ownedInside = NativeMethods.GetWindowProcessId(cur) == pid
                        && NativeMethods.GetWindow(cur, NativeMethods.GwOwner) == hwnd
                        && ScreenGeometry.Contains(target, o);
                    if (!ownedInside)
                    {
                        return true;
                    }
                }
            }

            cur = NativeMethods.GetWindow(cur, NativeMethods.GwHwndPrev);
        }

        return false;
    }

    /// <summary>Screen pixels of exactly the window rectangle; the caller has checked <see cref="Session"/>, minimized and <see cref="IsOccluded"/>.</summary>
    public static Bitmap? CopyRect(IntPtr hwnd) => WindowCapture.CopyWindowRectFromScreen(hwnd);

    private static bool IsRdpDisconnected()
    {
        try
        {
            if (MachineNative.WTSQuerySessionInformation(MachineNative.WTS_CURRENT_SERVER_HANDLE, MachineNative.WTS_CURRENT_SESSION, MachineNative.WTSConnectState, out var p, out _))
            {
                try
                {
                    return Marshal.ReadInt32(p) == MachineNative.WTSDisconnected;
                }
                finally
                {
                    MachineNative.WTSFreeMemory(p);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return false;
    }
}

/// <summary>Pure rectangle math behind <see cref="ScreenGuard"/>.</summary>
public static class ScreenGeometry
{
    /// <summary>Intersection of two rectangles; <see cref="Rectangle.Empty"/> when they do not overlap.</summary>
    public static Rectangle Intersect(Rectangle a, Rectangle b)
    {
        var r = Rectangle.Intersect(a, b);
        return r.Width <= 0 || r.Height <= 0 ? Rectangle.Empty : r;
    }

    /// <summary>True when <paramref name="inner"/> lies completely inside <paramref name="outer"/>.</summary>
    public static bool Contains(Rectangle outer, Rectangle inner) => outer.Contains(inner);

    /// <summary>
    /// A steps x steps grid of points spread over the rectangle, inset by 2 px so edges and shadows are not sampled.
    /// Empty for an empty rectangle or fewer than 2 steps.
    /// </summary>
    public static IReadOnlyList<Point> GridPoints(Rectangle r, int steps)
    {
        var list = new List<Point>();
        if (r.Width <= 0 || r.Height <= 0 || steps < 2)
        {
            return list;
        }

        var w = Math.Max(0, r.Width - 5);
        var h = Math.Max(0, r.Height - 5);
        for (var iy = 0; iy < steps; iy++)
        {
            for (var ix = 0; ix < steps; ix++)
            {
                list.Add(new Point(r.Left + 2 + (int)(w * (long)ix / (steps - 1)), r.Top + 2 + (int)(h * (long)iy / (steps - 1))));
            }
        }

        return list;
    }
}
