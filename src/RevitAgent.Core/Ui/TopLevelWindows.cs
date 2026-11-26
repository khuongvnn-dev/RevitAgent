using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>One visible top-level window (from EnumWindows, so owned dialogs such as TaskDialogs are included).</summary>
public sealed record TopLevelWindow(
    long Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    string ClassName,
    long Owner,
    bool Enabled,
    int Left,
    int Top,
    int Width,
    int Height)
{
    public string HwndHex => UiText.Hex(Hwnd);

    public double Area => (double)Width * Height;

    /// <summary>Win32 dialog class (#32770): MessageBox, TaskDialog, common dialogs, MFC dialogs.</summary>
    public bool IsDialogClass => ClassName == "#32770";

    /// <summary>True for a #32770 dialog or an owned window whose owner is disabled (= it is modal to its owner).</summary>
    public bool IsModalDialog => IsDialogClass || (Owner != 0 && !NativeMethods.IsWindowEnabled(new IntPtr(Owner)));

    public IntPtr Handle => new(Hwnd);

    public object ToDto(bool isMain) => new
    {
        hwnd = HwndHex,
        title = Title,
        @class = ClassName,
        pid = ProcessId,
        process = ProcessName,
        owner = Owner == 0 ? null : UiText.Hex(Owner),
        is_modal_dialog = IsModalDialog,
        enabled = Enabled,
        bounds = new { x = Left, y = Top, width = Width, height = Height },
        is_main = isMain,
    };
}

/// <summary>Enumerates top-level windows and resolves the <c>pid</c> / <c>window</c> / <c>hwnd</c> targeting arguments.</summary>
public static class TopLevelWindows
{
    private static readonly Dictionary<int, string> ProcessNames = new();
    private static readonly object ProcessNamesGate = new();

    /// <summary>Visible top-level windows in z-order (topmost first), optionally of one process.</summary>
    public static IReadOnlyList<TopLevelWindow> List(int? pid = null)
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows(
            (h, _) =>
            {
                if (NativeMethods.IsWindowVisible(h))
                {
                    handles.Add(h);
                }

                return true;
            },
            IntPtr.Zero);

        return NativeMethods.WithPhysicalDpi(() =>
        {
            var result = new List<TopLevelWindow>(handles.Count);
            foreach (var h in handles)
            {
                var wpid = NativeMethods.GetWindowProcessId(h);
                if (pid is { } p && wpid != p)
                {
                    continue;
                }

                var w = Describe(h, wpid);
                if (w is not null && w.Width >= 2 && w.Height >= 2)
                {
                    result.Add(w);
                }
            }

            return (IReadOnlyList<TopLevelWindow>)result;
        });
    }

    /// <summary>Describes one window handle (null when it no longer exists).</summary>
    public static TopLevelWindow? Describe(IntPtr hwnd, int? knownPid = null)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        return NativeMethods.WithPhysicalDpi(() =>
        {
            var pid = knownPid ?? NativeMethods.GetWindowProcessId(hwnd);
            NativeMethods.GetWindowRect(hwnd, out var r);
            return new TopLevelWindow(
                hwnd.ToInt64(),
                pid,
                ProcessName(pid),
                NativeMethods.GetWindowText(hwnd),
                NativeMethods.GetClassName(hwnd),
                NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner).ToInt64(),
                NativeMethods.IsWindowEnabled(hwnd),
                r.Left,
                r.Top,
                r.Width,
                r.Height);
        });
    }

    /// <summary>
    /// The main window of a process = not owned, has a title, largest area (z-order alone
    /// picks hidden helper windows or ribbon tooltips in Revit 2024).
    /// </summary>
    public static TopLevelWindow? PickMain(IEnumerable<TopLevelWindow> windows) =>
        windows
            .OrderBy(w => w.Owner != 0 ? 1 : 0)
            .ThenBy(w => string.IsNullOrWhiteSpace(w.Title) ? 1 : 0)
            .ThenByDescending(w => w.Area)
            .FirstOrDefault();

    public static string ProcessName(int pid)
    {
        lock (ProcessNamesGate)
        {
            if (ProcessNames.TryGetValue(pid, out var n))
            {
                return n;
            }
        }

        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            name = "?";
        }

        lock (ProcessNamesGate)
        {
            if (ProcessNames.Count > 500)
            {
                ProcessNames.Clear();
            }

            ProcessNames[pid] = name;
        }

        return name;
    }
}

/// <summary>
/// Parsed window targeting: <c>pid</c>, <c>window</c> spec and/or <c>hwnd</c>.
/// Window spec grammar: title substring (exact title wins), <c>/regex/</c>, <c>hwnd:0x1234</c>,
/// <c>0x1234</c>, <c>pid:1234</c>, <c>proc:Revit</c>, <c>class:#32770</c>.
/// </summary>
public sealed class WindowSpec
{
    private static readonly Regex HexOrDec = new("^(0x[0-9a-fA-F]+|\\d+)$", RegexOptions.Compiled);

    public int? Pid { get; init; }

    public string? Window { get; init; }

    public long? Hwnd { get; init; }

    public bool IsEmpty => Pid is null && string.IsNullOrWhiteSpace(Window) && Hwnd is null;

    /// <summary>True when the caller named a specific window (title/regex/hwnd), not just a process.</summary>
    public bool NamesWindow => !string.IsNullOrWhiteSpace(Window) || Hwnd is not null;

    public static WindowSpec FromCall(ToolCall call) => new()
    {
        Pid = call.GetInt("pid"),
        Window = call.GetString("window"),
        Hwnd = call.GetLong("hwnd"),
    };

    public override string ToString()
    {
        var parts = new List<string>();
        if (Pid is { } p)
        {
            parts.Add($"pid {p}");
        }

        if (!string.IsNullOrWhiteSpace(Window))
        {
            parts.Add($"window '{Window}'");
        }

        if (Hwnd is { } h)
        {
            parts.Add($"hwnd {UiText.Hex(h)}");
        }

        return parts.Count == 0 ? "(no window)" : string.Join(", ", parts);
    }

    /// <summary>
    /// Resolves to one top-level window. Throws NOT_FOUND when nothing matches, VALIDATION_FAILED for a bad spec.
    /// When <see cref="Pid"/> is set every match must belong to that process.
    /// </summary>
    public TopLevelWindow Resolve()
    {
        if (Hwnd is { } h)
        {
            return ResolveHandle(h);
        }

        var spec = Window?.Trim();
        if (string.IsNullOrEmpty(spec))
        {
            if (Pid is not { } pid)
            {
                throw GatewayException.Validation("Pass 'pid' (target process), 'window' (title / hwnd:0x...) or 'hwnd' to choose the window.");
            }

            EnsureProcessAlive(pid);
            var main = TopLevelWindows.PickMain(TopLevelWindows.List(pid));
            return main ?? throw GatewayException.NotFound(
                $"Process {pid} has no visible top-level window (still starting, minimized to tray, or a different desktop). Retry with ui_wait or check ui_list_windows.");
        }

        if (spec.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveHandle(ParseHandle(spec[5..]));
        }

        if (HexOrDec.IsMatch(spec))
        {
            return ResolveHandle(ParseHandle(spec));
        }

        var windows = TopLevelWindows.List(Pid);
        List<TopLevelWindow> matches;
        bool processSpec = false;
        var m = Regex.Match(spec, "^(pid|proc|process|class):(.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            var val = m.Groups[2].Value.Trim();
            switch (key)
            {
                case "pid":
                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var specPid))
                    {
                        throw GatewayException.Validation($"Window spec '{spec}': pid must be an integer.");
                    }

                    if (Pid is { } p && p != specPid)
                    {
                        throw GatewayException.Validation($"Window spec '{spec}' names a different process than pid {p}.");
                    }

                    EnsureProcessAlive(specPid);
                    matches = (Pid is null ? TopLevelWindows.List(specPid) : windows).ToList();
                    processSpec = true;
                    break;
                case "class":
                    matches = windows.Where(w => string.Equals(w.ClassName, val, StringComparison.OrdinalIgnoreCase)).ToList();
                    break;
                default:
                    var name = val.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? val[..^4] : val;
                    matches = windows.Where(w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                    processSpec = true;
                    break;
            }
        }
        else if (spec.Length >= 2 && spec.StartsWith('/') && spec.EndsWith('/'))
        {
            var rx = UiText.CompileRegex(spec[1..^1], "window");
            matches = windows.Where(w => rx.IsMatch(w.Title)).ToList();
        }
        else
        {
            var want = UiText.Norm(spec);
            matches = windows.Where(w => string.Equals(UiText.Norm(w.Title), want, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = windows.Where(w => w.Title.Length > 0 && UiText.Norm(w.Title).Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        if (matches.Count == 0)
        {
            if (Pid is { } pid2)
            {
                EnsureProcessAlive(pid2);
            }

            throw GatewayException.NotFound(
                $"No top-level window matches '{spec}'{(Pid is { } p2 ? $" in process {p2}" : string.Empty)}. Call ui_list_windows to see the open windows.");
        }

        if (processSpec && matches.Count > 1)
        {
            return TopLevelWindows.PickMain(matches)!;
        }

        return matches[0];
    }

    private TopLevelWindow ResolveHandle(long hwnd)
    {
        var w = TopLevelWindows.Describe(new IntPtr(hwnd));
        if (w is null)
        {
            throw GatewayException.NotFound($"No window with handle {UiText.Hex(hwnd)} (it was closed). Call ui_list_windows.");
        }

        // A child-control handle is accepted: use its top-level window.
        var root = NativeMethods.GetAncestor(new IntPtr(hwnd), NativeMethods.GaRoot);
        if (root != IntPtr.Zero && root.ToInt64() != hwnd)
        {
            w = TopLevelWindows.Describe(root) ?? w;
        }

        if (Pid is { } p && w.ProcessId != p)
        {
            throw GatewayException.Validation($"Window {w.HwndHex} belongs to process {w.ProcessId}, not pid {p}.");
        }

        return w;
    }

    private static long ParseHandle(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
        {
            return dec;
        }

        throw GatewayException.Validation($"'{s}' is not a window handle (use 0x1A2B or decimal).");
    }

    public static void EnsureProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
            {
                throw GatewayException.NotFound($"Process {pid} has exited.");
            }
        }
        catch (ArgumentException)
        {
            throw GatewayException.NotFound($"No process with pid {pid} is running (it exited or the pid is wrong). Use revit_list_instances.");
        }
        catch (InvalidOperationException)
        {
            throw GatewayException.NotFound($"Process {pid} has exited.");
        }
    }
}
