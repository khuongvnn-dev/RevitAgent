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
    private const int GridSteps = 7;

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
    /// True unless every sample point of a grid over the window rectangle hits <paramref name="hwnd"/> itself or one of its child
    /// windows (WindowFromPoint, root ancestor, same process). Points off screen count as covered.
    /// </summary>
    public static bool IsOccluded(IntPtr hwnd)
    {
        return NativeMethods.WithPhysicalDpi(() =>
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0)
            {
                return true;
            }

            var pid = NativeMethods.GetWindowProcessId(hwnd);
            for (var iy = 0; iy < GridSteps; iy++)
            {
                for (var ix = 0; ix < GridSteps; ix++)
                {
                    var x = r.Left + 2 + (int)((r.Width - 5) * (long)ix / (GridSteps - 1));
                    var y = r.Top + 2 + (int)((r.Height - 5) * (long)iy / (GridSteps - 1));
                    var hit = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y });
                    if (hit == IntPtr.Zero
                        || NativeMethods.GetAncestor(hit, NativeMethods.GaRoot) != hwnd
                        || NativeMethods.GetWindowProcessId(hit) != pid)
                    {
                        return true;
                    }
                }
            }

            return false;
        });
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
