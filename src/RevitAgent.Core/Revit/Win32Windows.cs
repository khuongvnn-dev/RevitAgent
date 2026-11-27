using System.Diagnostics;
using System.Globalization;
using System.Text;
using RevitAgent.Core.Revit.Native;

namespace RevitAgent.Core.Revit;

/// <summary>A top-level window of a process, read with message-free Win32 calls.</summary>
public sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string ClassName,
    bool Visible,
    bool Enabled,
    bool Hung,
    bool Minimized,
    IntPtr Owner,
    int Width,
    int Height,
    long Style,
    long ExStyle)
{
    public string HwndHex => Win32Windows.FormatHwnd(Hwnd);

    public bool IsOwned => Owner != IntPtr.Zero;

    public bool IsDialogClass => string.Equals(ClassName, Win32Windows.DialogClass, StringComparison.Ordinal);

    public bool IsToolWindow => (ExStyle & NativeMethods.WsExToolWindow) != 0;

    /// <summary>WS_CAPTION (title bar) — main frames have it, splash screens do not.</summary>
    public bool HasCaption => (Style & NativeMethods.WsCaption) == NativeMethods.WsCaption;

    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>A child control of a dialog (Win32 only; DirectUI/WPF content is invisible here).</summary>
public sealed record ChildControl(IntPtr Hwnd, IntPtr Parent, string ClassName, int ControlId, bool Visible, bool Enabled, long Style);

/// <summary>
/// Message-free Win32 window observation (EnumWindows, GetClassName, GetWindowText on foreign top-level windows,
/// IsWindowEnabled, IsHungAppWindow). Safe to call on any process, including a busy or hung Revit:
/// none of these calls sends a message to the target.
/// </summary>
public static class Win32Windows
{
    public const string DialogClass = "#32770";

    /// <summary>Classes that are never blockers (tooltips, menus, shadows, IME).</summary>
    private static readonly HashSet<string> NoiseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tooltips_class32", "#32768", "SysShadow", "IME", "MSCTFIME UI", "Shell_TrayWnd", "ForegroundStaging",
    };

    public static string FormatHwnd(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Parses "0x1A2B", "1A2B" (hex with letters) or a decimal handle. Returns false when invalid.</summary>
    public static bool TryParseHwnd(string? text, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return false;
        }

        long value;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
        }
        else if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                 !long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (value <= 0)
        {
            return false;
        }

        hwnd = new IntPtr(value);
        return true;
    }

    /// <summary>Top-level windows of <paramref name="pid"/> in Z order (visible ones only unless <paramref name="includeHidden"/>).</summary>
    public static IReadOnlyList<WindowInfo> TopLevelWindows(int pid, bool includeHidden = false)
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows(
            (h, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(h, out var owner);
                if (owner == (uint)pid)
                {
                    handles.Add(h);
                }

                return true;
            },
            IntPtr.Zero);

        var result = new List<WindowInfo>(handles.Count);
        foreach (var h in handles)
        {
            var visible = NativeMethods.IsWindowVisible(h);
            if (!visible && !includeHidden)
            {
                continue;
            }

            var info = Describe(h);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>Snapshot of one window, or null when it no longer exists.</summary>
    public static WindowInfo? Describe(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        var minimized = NativeMethods.IsIconic(hwnd);
        int width = 0, height = 0;
        if (minimized)
        {
            var placement = new NativeMethods.WindowPlacement { Length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WindowPlacement>() };
            if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
            {
                width = placement.NormalPosition.Right - placement.NormalPosition.Left;
                height = placement.NormalPosition.Bottom - placement.NormalPosition.Top;
            }
        }
        else if (NativeMethods.GetWindowRect(hwnd, out var r))
        {
            width = r.Right - r.Left;
            height = r.Bottom - r.Top;
        }

        return new WindowInfo(
            hwnd,
            WindowTitle(hwnd),
            ClassName(hwnd),
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsWindowEnabled(hwnd),
            NativeMethods.IsHungAppWindow(hwnd),
            minimized,
            NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner),
            width,
            height,
            NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlStyle),
            NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle));
    }

    /// <summary>
    /// The application's main window: visible, not owned, titled, with a caption bar (so a borderless splash screen
    /// never counts), not a dialog/tool window; a title containing "Revit" wins, then the largest area
    /// (Revit 2024 creates hidden "&lt;guid&gt;Monitor" and tooltip windows first).
    /// </summary>
    public static WindowInfo? FindMainWindow(IReadOnlyList<WindowInfo> windows) =>
        windows
            .Where(w => w.Visible && !w.IsOwned && w.Title.Length > 0 && w.HasCaption && !w.IsDialogClass && !w.IsToolWindow && !IsNoise(w))
            .OrderByDescending(w => w.Title.Contains("Revit", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.Area)
            .FirstOrDefault();

    public static bool IsNoise(WindowInfo w) => NoiseClasses.Contains(w.ClassName) || w.Width <= 0 || w.Height <= 0;

    /// <summary>Process id that owns <paramref name="hwnd"/> (0 when the window is gone).</summary>
    public static int ProcessIdOf(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public static bool Exists(IntPtr hwnd) => NativeMethods.IsWindow(hwnd);

    public static bool IsVisible(IntPtr hwnd) => NativeMethods.IsWindowVisible(hwnd);

    public static IntPtr LastActivePopup(IntPtr owner) => NativeMethods.GetLastActivePopup(owner);

    /// <summary>
    /// Wakes the message loop of the thread that owns <paramref name="hwnd"/>: posts WM_NULL (no effect on any window)
    /// <paramref name="count"/> times, <paramref name="spacingMs"/> apart. Revit runs a command queued by PostCommand from its
    /// idle processing, and an idle loop only turns over when a message arrives: a Revit in the background with no mouse or
    /// paint traffic can hold a posted command indefinitely (live finding 2026-11-25 / 2026-11-27). PostMessage never blocks,
    /// even on a hung window. Returns true when at least one post was queued.
    /// </summary>
    public static bool WakeMessageLoop(IntPtr hwnd, int count = 3, int spacingMs = 120)
    {
        var any = false;
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(spacingMs);
            }

            any |= NativeMethods.PostMessage(hwnd, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero);
        }

        return any;
    }

    /// <summary>Caption via GetWindowText: for windows of other processes this reads the cached caption without a message.</summary>
    public static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>All descendants of <paramref name="parent"/> (message-free).</summary>
    public static IReadOnlyList<ChildControl> Children(IntPtr parent, int max = 400)
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumChildWindows(
            parent,
            (h, _) =>
            {
                handles.Add(h);
                return handles.Count < max;
            },
            IntPtr.Zero);

        return handles.Select(h => new ChildControl(
            h,
            NativeMethods.GetParent(h),
            ClassName(h),
            NativeMethods.GetDlgCtrlID(h),
            NativeMethods.IsWindowVisible(h),
            NativeMethods.IsWindowEnabled(h),
            NativeMethods.GetWindowLong(h, NativeMethods.GwlStyle))).ToArray();
    }

    /// <summary>
    /// Text of a control via WM_GETTEXT with SendMessageTimeout (SMTO_ABORTIFHUNG), so a hung target
    /// costs at most <paramref name="timeoutMs"/>. Returns null on timeout/failure.
    /// </summary>
    public static string? ReadText(IntPtr hwnd, int maxChars = 4000, uint timeoutMs = 400)
    {
        var buffer = new char[maxChars + 1];
        var ok = NativeMethods.SendMessageTimeoutText(
            hwnd, NativeMethods.WmGetText, (IntPtr)buffer.Length, buffer, NativeMethods.SmtoAbortIfHung | NativeMethods.SmtoBlock, timeoutMs, out var copied);
        if (ok == IntPtr.Zero)
        {
            return null;
        }

        var n = (int)Math.Clamp(copied.ToInt64(), 0, maxChars);
        return new string(buffer, 0, n);
    }

    /// <summary>Full path of a process image via QueryFullProcessImageName (works for elevated processes too).</summary>
    public static string? ProcessImagePath(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>True when the process exists and has not exited (no exception for access-denied processes).</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied on HasExited (elevated process): it exists.
            return true;
        }
    }

    /// <summary>Process start time in UTC, or null when it cannot be read.</summary>
    public static DateTime? ProcessStartUtc(Process p)
    {
        try
        {
            return p.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Strips accelerator markers ("&amp;Yes" → "Yes", "&amp;&amp;" → "&amp;") and surrounding whitespace.</summary>
    public static string StripMnemonic(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&')
                {
                    sb.Append('&');
                    i++;
                }

                continue;
            }

            sb.Append(text[i]);
        }

        return sb.ToString().Trim();
    }
}
