using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>
/// Captures a top-level window to PNG from outside the target process.
/// PrintWindow(PW_RENDERFULLCONTENT) works for occluded windows and most
/// WPF content; DirectX canvases may print black (see <see cref="ScreenshotPlan"/> for what happens then). All coordinates are physical pixels (per-monitor DPI aware);
/// DPI-unaware windows (rendered by PrintWindow at their 96-DPI size) are scaled up to their on-screen size.
/// Never throws for a vanished window; returns null instead.
/// </summary>
public static class WindowCapture
{
    private const uint PwRenderFullContent = 2;

    /// <summary>PNG bytes of <paramref name="hwnd"/> through PrintWindow, or null (gone, no size, or printed blank).</summary>
    public static byte[]? CapturePng(IntPtr hwnd)
    {
        using var bmp = hwnd == IntPtr.Zero ? null : CaptureWindow(hwnd);
        return bmp is null ? null : EncodePng(bmp);
    }

    /// <summary>
    /// The window at its physical on-screen size through PrintWindow only, or null when it is gone, has no size or
    /// printed blank/black. Never falls back to screen pixels.
    /// </summary>
    public static Bitmap? CaptureWindow(IntPtr hwnd) => NativeMethods.WithPhysicalDpi(() => CaptureWindowCore(hwnd, fromScreen: false));

    /// <summary>
    /// Copies the pixels of the window rectangle (and nothing else) from the screen. They show whatever is on top, so call
    /// this only after <see cref="ScreenGuard"/> verified the window is rendering, not minimized and not covered.
    /// </summary>
    internal static Bitmap? CopyWindowRectFromScreen(IntPtr hwnd) => NativeMethods.WithPhysicalDpi(() => CaptureWindowCore(hwnd, fromScreen: true));

    /// <summary>Saves a PNG to <paramref name="path"/> and returns the bytes (null when the window is gone).</summary>
    public static byte[]? SavePng(IntPtr hwnd, string path)
    {
        var png = CapturePng(hwnd);
        if (png is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, png);
        }

        return png;
    }

    public static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>Crops <paramref name="bmp"/> to <paramref name="area"/> (clipped to the image); null when nothing is left.</summary>
    public static Bitmap? Crop(Bitmap bmp, Rectangle area)
    {
        area.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
        return area.Width <= 0 || area.Height <= 0 ? null : bmp.Clone(area, bmp.PixelFormat);
    }

    /// <summary>Returns a copy scaled down to at most <paramref name="maxWidth"/> pixels wide (or the same size), and the scale factor.</summary>
    public static Bitmap ScaleToMaxWidth(Bitmap bmp, int maxWidth, out double scale)
    {
        if (maxWidth <= 0 || bmp.Width <= maxWidth)
        {
            scale = 1.0;
            return new Bitmap(bmp);
        }

        scale = (double)maxWidth / bmp.Width;
        int h = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        var small = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(bmp, 0, 0, maxWidth, h);
        return small;
    }

    private static Bitmap? CaptureWindowCore(IntPtr hwnd, bool fromScreen)
    {
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return null;
        }

        int w = r.Width, h = r.Height;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        if (fromScreen)
        {
            return FromScreen(r.Left, r.Top, w, h);
        }

        // DPI-unaware windows: PrintWindow renders them at their logical (96-DPI) size; capture that, then scale up.
        int awareness = -1;
        try
        {
            awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(NativeMethods.GetWindowDpiAwarenessContext(hwnd));
        }
        catch (EntryPointNotFoundException)
        {
        }

        if (awareness == 0)
        {
            var lr = default(NativeMethods.Rect);
            try
            {
                var old = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextUnaware);
                NativeMethods.GetWindowRect(hwnd, out lr);
                NativeMethods.SetThreadDpiAwarenessContext(old);
            }
            catch (EntryPointNotFoundException)
            {
            }

            if (lr.Width > 0 && lr.Height > 0 && (lr.Width != w || lr.Height != h))
            {
                using var small = Print(hwnd, lr.Width, lr.Height);
                if (small is not null)
                {
                    var big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(big);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(small, 0, 0, w, h);
                    return big;
                }
            }
        }

        return Print(hwnd, w, h);
    }

    private static Bitmap? Print(IntPtr hwnd, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                ok = NativeMethods.PrintWindow(hwnd, hdc, PwRenderFullContent);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        if (!ok || IsBlank(bmp))
        {
            bmp.Dispose();
            return null;
        }

        return bmp;
    }

    private static Bitmap FromScreen(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        return bmp;
    }

    private static bool IsBlank(Bitmap bmp)
    {
        // Sample a few pixels: PrintWindow on some GPU surfaces returns all-black (or fully transparent).
        int[] xs = { bmp.Width / 5, 2 * bmp.Width / 5, bmp.Width / 2, 3 * bmp.Width / 5, 4 * bmp.Width / 5 };
        int[] ys = { bmp.Height / 5, 2 * bmp.Height / 5, bmp.Height / 2, 3 * bmp.Height / 5, 4 * bmp.Height / 5 };
        foreach (var x in xs)
        {
            foreach (var y in ys)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R != 0 || c.G != 0 || c.B != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
