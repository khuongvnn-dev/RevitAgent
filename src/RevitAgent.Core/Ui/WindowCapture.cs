using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>
/// Captures a top-level window to PNG from outside the target process.
/// Order: PrintWindow(PW_RENDERFULLCONTENT) — works for occluded windows and most WPF/DirectX content —
/// then a screen copy of the window rectangle. All coordinates are physical pixels (per-monitor DPI aware);
/// DPI-unaware windows (rendered by PrintWindow at their 96-DPI size) are scaled up to their on-screen size.
/// Never throws for a vanished window; returns null instead.
/// </summary>
public static class WindowCapture
{
    private const uint PwRenderFullContent = 2;

    /// <summary>PNG bytes of <paramref name="hwnd"/> (or the whole primary screen when hwnd is zero), or null.</summary>
    public static byte[]? CapturePng(IntPtr hwnd)
    {
        using var bmp = hwnd == IntPtr.Zero ? CaptureScreen() : CaptureWindow(hwnd);
        return bmp is null ? null : EncodePng(bmp);
    }

    /// <summary>The window at its physical on-screen size, or null when it is gone or has no size.</summary>
    public static Bitmap? CaptureWindow(IntPtr hwnd) => NativeMethods.WithPhysicalDpi(() => CaptureWindowCore(hwnd, fromScreen: false));

    /// <summary>
    /// Copies the window rectangle from the screen (what the user sees, incl. overlapping windows). Use when
    /// PrintWindow returns stale/black content (some DirectX canvases). The window should be in the foreground.
    /// </summary>
    public static Bitmap? CaptureWindowFromScreen(IntPtr hwnd) => NativeMethods.WithPhysicalDpi(() => CaptureWindowCore(hwnd, fromScreen: true));

    public static Bitmap CaptureScreen()
    {
        return NativeMethods.WithPhysicalDpi(() =>
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            return bmp;
        });
    }

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

        var bmp = Print(hwnd, w, h);
        if (bmp is null)
        {
            return FromScreen(r.Left, r.Top, w, h);
        }

        return bmp;
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
        int[] xs = { bmp.Width / 4, bmp.Width / 2, 3 * bmp.Width / 4 };
        int[] ys = { bmp.Height / 4, bmp.Height / 2, 3 * bmp.Height / 4 };
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
