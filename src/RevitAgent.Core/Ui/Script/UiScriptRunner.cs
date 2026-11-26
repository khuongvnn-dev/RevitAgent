using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Interop.UIAutomationClient;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui.Script;

/// <summary>Result of one script step (report.json fields, plus the UiStep role).</summary>
public sealed record UiStepResult(int N, string Step, string Status, long Ms, string Output, string Error, string Screenshot, string Role, string Outcome);

/// <summary>Result of a script run.</summary>
public sealed class UiScriptReport
{
    public required string Name { get; init; }

    public string? File { get; init; }

    public required string Verdict { get; init; }

    public int Passed { get; init; }

    public int Failed { get; init; }

    public int Skipped { get; init; }

    public double Seconds { get; init; }

    public required string Time { get; init; }

    public required IReadOnlyList<UiStepResult> Steps { get; init; }

    /// <summary>SUCCEEDED | FAILED (add-in misbehaved) | BLOCKED (could not reach what to check).</summary>
    public required string Measured { get; init; }

    public required string RunDir { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Executes a UI script against one process:
/// starts at the main window of the pid (or the script's <c>window</c>), runs steps in order with auto-wait and dialog
/// rules, screenshots on failure (or each step), writes report.json / report.md and NN_fail_snapshot.txt.
/// Every window it touches must belong to the target pid.
/// </summary>
public sealed class UiScriptRunner
{
    private const string MainMarker = "<main>";
    private static readonly Regex LayoutError = new("(?i)no on-screen rectangle|offscreen|virtualiz", RegexOptions.Compiled);

    private readonly UiOp _op;
    private readonly UiAutomationService _ui;
    private readonly UiScript _script;
    private readonly int _pid;
    private readonly string _runDir;
    private readonly string _shots;
    private readonly UiScriptVars _vars = new();
    private readonly IReadOnlyList<DialogRule> _rules;
    private readonly List<string> _handled = new();
    private readonly HashSet<string> _ruleNotes = new(StringComparer.Ordinal);
    private readonly DateTime _deadline;
    private long _current;

    public UiScriptRunner(UiOp op, UiScript script, IReadOnlyList<DialogRule> rules, int pid, string runDir, string? screenshotsOverride, TimeSpan overall, IReadOnlyDictionary<string, string>? vars)
    {
        _op = op;
        _ui = op.Service;
        _script = script;
        _rules = rules;
        _pid = pid;
        _runDir = runDir;
        _shots = (screenshotsOverride ?? script.Screenshots).ToLowerInvariant();
        _deadline = DateTime.UtcNow + overall;
        _vars.Load(script.Vars);
        _vars.Set("dir", script.Directory ?? runDir);
        if (vars is not null)
        {
            foreach (var kv in vars)
            {
                _vars.Set(kv.Key, kv.Value);
            }
        }

        _vars.SetEnv("REVITAGENT_UI_HOST_PID", pid.ToString(CultureInfo.InvariantCulture));
        _vars.SetEnv("REVITAGENT_UI_OUT", runDir);
        op.RestrictPid = pid;
        op.BeforeAttempt = _ => RunDialogRules();
    }

    public UiScriptReport Run(IReadOnlyList<string> warnings)
    {
        Directory.CreateDirectory(_runDir);
        var startWindow = _vars.Expand(_script.Window);
        _current = (string.IsNullOrWhiteSpace(startWindow)
            ? new WindowSpec { Pid = _pid }.Resolve()
            : _ui.ResolveWindow(_op, new WindowSpec { Pid = _pid, Window = startWindow })).Hwnd;

        var results = new List<UiStepResult>();
        var roles = Roles();
        int failed = 0;
        bool stop = false;
        foreach (var step in _script.Steps)
        {
            var label = step.Label;
            var (role, optional) = roles[step.Number - 1];
            if (stop || DateTime.UtcNow > _deadline)
            {
                results.Add(new UiStepResult(step.Number, label, "SKIP", 0, string.Empty, stop ? string.Empty : "run time budget exhausted", string.Empty, role, "SKIP"));
                continue;
            }

            var sw = Stopwatch.StartNew();
            string status = "PASS", output = string.Empty, error = string.Empty, shot = string.Empty;
            int before = _handled.Count;
            try
            {
                output = Execute(step);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GatewayException ex)
            {
                status = "FAIL";
                error = ex.Message;
            }
            catch (Exception ex)
            {
                // Any other failure fails the step (never the whole run, so the report is always written).
                status = "FAIL";
                error = UiaCore.Translate(ex)?.Message ?? $"{ex.GetType().Name}: {ex.Message}";
            }

            sw.Stop();
            if (_handled.Count > before)
            {
                output = string.Join("\n", _handled.Skip(before).Append(output)).Trim();
            }

            if (_shots == "each" || (status == "FAIL" && _shots != "none"))
            {
                shot = SaveShot(Path.Combine(_runDir, $"{step.Number:D2}_{status.ToLowerInvariant()}.png")) ?? string.Empty;
            }

            if (status == "FAIL")
            {
                failed++;
                WriteFailSnapshot(Path.Combine(_runDir, $"{step.Number:D2}_fail_snapshot.txt"));
                if (!(_script.ContinueOnError || step.Flag("continueOnError")))
                {
                    stop = true;
                }
            }

            results.Add(new UiStepResult(step.Number, label, status, sw.ElapsedMilliseconds, output, error, shot, role, Outcome(role, optional, status, error)));
        }

        var report = new UiScriptReport
        {
            Name = _script.Name,
            File = _script.Path,
            Verdict = failed == 0 ? "PASSED" : "FAILED",
            Passed = results.Count(r => r.Status == "PASS"),
            Failed = failed,
            Skipped = results.Count(r => r.Status == "SKIP"),
            Seconds = Math.Round(results.Sum(r => r.Ms) / 1000.0, 1),
            Time = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            Steps = results,
            Measured = results.Any(r => r.Outcome == "FAIL") ? "FAILED" : results.Any(r => r.Outcome == "BLOCK") ? "BLOCKED" : "SUCCEEDED",
            RunDir = _runDir,
            Warnings = warnings,
        };
        WriteReports(report);
        return report;
    }

    // ---------------------------------------------------------------- dispatch

    private string Execute(UiScriptStep st)
    {
        var remaining = (int)Math.Max(0, (_deadline - DateTime.UtcNow).TotalSeconds);
        var s = new StepArgs(st, _vars, _script.TimeoutSeconds, remaining);
        switch (s.Do)
        {
            case "sleep":
                _op.Sleep(Math.Clamp(st.Int("ms") ?? 500, 0, 600_000));
                return "slept";
            case "run":
            case "launch":
                throw GatewayException.Denied($"'{s.Do}' is not allowed in a UI script");
            case "windows":
                return string.Join("\n", TopLevelWindows.List(_pid).Select(w =>
                    $"{(w.Hwnd == _current ? '*' : ' ')} {w.HwndHex,-11} pid={w.ProcessId,-7} {w.ProcessName,-20} \"{UiText.Clip(w.Title, 100)}\"{(w.Owner != 0 ? $"  (owned by {UiText.Hex(w.Owner)})" : string.Empty)}{(w.IsDialogClass ? "  [dialog]" : string.Empty)}"));
            case "use":
            {
                var w = _ui.ResolveWindow(_op, new WindowSpec { Pid = _pid, Window = s.Window ?? s.Target });
                _current = w.Hwnd;
                return $"current window: {w.HwndHex} \"{w.Title}\" ({w.ProcessName})";
            }

            case "snapshot":
                return Snapshot(s);
            case "find":
                return Find(s);
            case "inspect":
            {
                var t = Resolve(s, needEnabled: false, allowWindow: true);
                return JsonSerializer.Serialize(UiActions.Inspect(_op, t), GatewayJson.Options);
            }

            case "text":
            {
                var t = Resolve(s, needEnabled: false, allowWindow: true);
                return UiActions.AllText(_op, t.Element, TimeSpan.FromSeconds(Math.Max(1, s.Limit ?? 15)));
            }

            case "point":
                return Point(s);
            case "screenshot":
                return Screenshot(s);
            case "click":
                return Click(s, "left", dbl: false);
            case "dblclick":
                return Click(s, "left", dbl: true);
            case "rightclick":
                return Click(s, "right", dbl: false);
            case "hover":
            {
                var t = Resolve(s, needEnabled: false);
                UiSafety.EnsureActionAllowed(_op, t.Window);
                InputSender.EnsureForeground(t.Window.Handle, _pid, "hover", _op.Token);
                var p = UiActions.ClickPoint(_op, t);
                InputSender.MoveTo(p.X, p.Y, _pid, _op.Token);
                return $"hovering {t.Node.Describe()} at {p.X},{p.Y}";
            }

            case "type":
                return TypeStep(s);
            case "keys":
                return Keys(s);
            case "focus":
            {
                var t = Resolve(s, needEnabled: true);
                UiSafety.EnsureActionAllowed(_op, t.Window);
                UiActions.TryActivate(t.Window, _op.Token);
                UiActions.FocusElement(_op, t.Wrap(_op.Core));
                return $"focused {t.Node.Describe()}";
            }

            case "toggle":
            {
                var t = Resolve(s, needEnabled: true);
                string? state = s.Text is { Length: > 0 } x ? (x.ToLowerInvariant() is "on" or "true" or "1" or "checked" ? "on" : "off") : null;
                var r = UiActions.Toggle(_op, t, state);
                return $"{t.Node.Describe()} -> {r["state"]}";
            }

            case "expand":
            case "collapse":
            {
                var t = Resolve(s, needEnabled: true);
                var r = UiActions.Expand(_op, t, s.Do == "collapse");
                return $"{r["action"]} {t.Node.Describe()}";
            }

            case "select":
            {
                var t = Resolve(s, needEnabled: true);
                var r = UiActions.Select(_op, t, string.IsNullOrEmpty(s.Text) ? null : s.Text, null);
                return $"selected {r["item"]} in {t.Node.Describe()}{(r.TryGetValue("value", out var v) && v is not null ? $" -> value=\"{v}\"" : string.Empty)}";
            }

            case "scroll":
                return Scroll(s);
            case "drag":
                return Drag(s);
            case "close":
                return Close(s);
            case "wait":
                return Wait(s);
            case "expect":
                return Expect(s);
            case "table":
                return Table(s);
            case "dialogs":
                return Dialogs(s);
            default:
                throw GatewayException.Validation($"step has unknown 'do' value '{s.Do}'");
        }
    }

    private string Snapshot(StepArgs s)
    {
        var t = Resolve(s, needEnabled: false, allowWindow: true, wait: false);
        _current = t.Window.Hwnd;
        var snap = TreeSnapshot.Take(_op.Core, t.Element, new SnapshotOptions
        {
            InteractiveOnly = s.Flag("interactive"),
            MaxDepth = s.Depth ?? 25,
            MaxNodes = s.Max ?? 500,
            IncludeOffscreen = s.Flag("all"),
            Raw = s.Flag("raw"),
            Budget = TimeSpan.FromSeconds(30),
        }, i => "e" + i, _op.Token);
        var set = _ui.Refs.Replace(_op.Session, t.Window);
        foreach (var n in snap.RefNodes)
        {
            set.Add(n);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# window: {t.Window.HwndHex} \"{UiText.Clip(t.Window.Title, 100)}\" pid={t.Window.ProcessId} ({t.Window.ProcessName})");
        foreach (var l in snap.Lines)
        {
            sb.AppendLine(l);
        }

        if (snap.Truncated)
        {
            sb.AppendLine($"# TRUNCATED at {snap.RefNodes.Count} nodes");
        }

        return sb.ToString().TrimEnd();
    }

    private string Find(StepArgs s)
    {
        var selector = s.Selector ?? throw GatewayException.Validation("find needs a selector target, e.g. \"type=Button\"");
        var (window, found) = _ui.FindAll(_op, s.Spec(this), selector, int.MaxValue, TimeSpan.FromSeconds(Math.Max(1, s.Limit ?? 15)), s.Flag("all"));
        _current = window.Hwnd;
        if (found.Matches.Count == 0)
        {
            return $"no elements match '{selector}' in \"{window.Title}\"";
        }

        var set = _ui.Refs.GetOrCreate(_op.Session, window);
        var lines = found.Matches.Take(40).Select(e =>
        {
            var n = _op.Core.ReadLive(e);
            var r = _ui.AddRef(set, n);
            return TreeSnapshot.FormatNode(n, r, 0, null);
        }).ToList();
        if (found.Matches.Count > 40)
        {
            lines.Add($"# ... {found.Matches.Count - 40} more; refine the selector");
        }

        return string.Join("\n", lines);
    }

    private string Point(StepArgs s)
    {
        if (s.X is not { } x || s.Y is not { } y || x < 0 || y < 0)
        {
            throw GatewayException.Validation("point needs x and y (physical screen pixels)");
        }

        var h = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y });
        var root = h == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(h, NativeMethods.GaRoot);
        if (root == IntPtr.Zero || NativeMethods.GetWindowProcessId(root) != _pid)
        {
            throw GatewayException.NotFound($"point {x},{y} is not over a window of process {_pid}");
        }

        var e = _op.Core.Native.ElementFromPoint(new tagPOINT { x = x, y = y })
            ?? throw GatewayException.NotFound($"no UI Automation element at {x},{y}");
        var n = _op.Core.ReadLive(e);
        var sel = n.AutomationId.Length > 0 ? $"type={n.Type};id={n.AutomationId}" : n.Name.Length > 0 ? $"type={n.Type};name={n.Name}" : $"type={n.Type}";
        return TreeSnapshot.FormatNode(n, null, 0, null) + $"\nwindow: {UiText.Hex(root.ToInt64())}   selector: \"{sel}\"";
    }

    private string Screenshot(StepArgs s)
    {
        if (string.Equals(s.Target, "screen", StringComparison.OrdinalIgnoreCase))
        {
            throw GatewayException.Validation("screenshot of the whole screen is not supported (it would capture other applications); capture the window or an element");
        }

        var path = OutputPath(s.Out, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var t = Resolve(s, needEnabled: false, allowWindow: true);
        if (NativeMethods.IsIconic(t.Window.Handle))
        {
            throw GatewayException.NotReady($"window \"{UiText.Clip(t.Window.Title, 60)}\" is minimized; cannot capture it");
        }

        using var full = (s.Flag("fromscreen") ? WindowCapture.CaptureWindowFromScreen(t.Window.Handle) : WindowCapture.CaptureWindow(t.Window.Handle))
            ?? throw GatewayException.NotReady("window has no size (hidden or minimized?)");
        System.Drawing.Bitmap shot = full;
        System.Drawing.Bitmap? crop = null;
        if (!t.IsWindow && _op.Core.ReadLive(t.Element).Bounds is { } b)
        {
            crop = WindowCapture.Crop(full, new System.Drawing.Rectangle(b.X - t.Window.Left, b.Y - t.Window.Top, b.Width, b.Height));
            shot = crop ?? full;
        }

        try
        {
            File.WriteAllBytes(path, WindowCapture.EncodePng(shot));
            return $"saved {path} ({shot.Width}x{shot.Height})";
        }
        finally
        {
            crop?.Dispose();
        }
    }

    private string Click(StepArgs s, string button, bool dbl)
    {
        if (s.Target is null && s.X is { } x && s.Y is { } y && x >= 0 && y >= 0)
        {
            // click x/y in a script: screen pixels. Converted to window-relative and confined to the current window.
            var w = _ui.ResolveWindow(_op, s.Spec(this));
            UiActions.ClickAt(_op, w, x - w.Left, y - w.Top, button, dbl);
            return $"clicked at {x},{y}";
        }

        bool mouse = s.Flag("mouse") || dbl || button != "left";
        var t = Resolve(s, needEnabled: !mouse);
        var r = UiActions.Click(_op, t, button, dbl, mouse);
        var what = r["action"] switch
        {
            "mouse" => dbl ? "double-clicked" : button == "right" ? "right-clicked" : "clicked",
            var a => a?.ToString(),
        };
        return $"{what} {t.Node.Describe()}{(r.ContainsKey("blocked") ? " - the call is still blocked after 3s (a modal dialog probably opened)" : string.Empty)}";
    }

    private string TypeStep(StepArgs s)
    {
        bool clear = s.Flag("clear");
        var text = clear ? string.Empty : s.Text;
        if (!clear && !s.TextGiven)
        {
            throw GatewayException.Validation("type needs \"text\" (or \"clear\": true)");
        }

        text ??= string.Empty;
        if (s.Target is null)
        {
            var w = _ui.ResolveWindow(_op, s.Spec(this));
            UiActions.TypeIntoFocused(_op, w, text);
            return $"typed {text.Length} chars into the focused element of \"{UiText.Clip(w.Title, 60)}\" (keyboard)";
        }

        var t = Resolve(s, needEnabled: true);
        var r = UiActions.Type(_op, t, text, s.Flag("keyboard") || clear, s.Flag("append"), s.Flag("verify"));
        return r["action"] switch
        {
            "set_value" => $"set value of {t.Node.Describe()} -> \"{UiText.Clip(r["value"] as string, 120)}\"",
            "cleared" => $"cleared {t.Node.Describe()} (keyboard)",
            _ => $"typed {text.Length} chars into {t.Node.Describe()} ({r["action"]}){(r.ContainsKey("verified") ? $" - verified value \"{UiText.Clip(r["value"] as string, 80)}\"" : string.Empty)}",
        };
    }

    private string Keys(StepArgs s)
    {
        var keys = s.Target ?? s.Text;
        if (string.IsNullOrEmpty(keys))
        {
            throw GatewayException.Validation("keys needs a key string, e.g. \"^s\" or \"{ENTER}\"");
        }

        var strokes = InputSender.ParseSendKeys(keys);
        var w = _ui.ResolveWindow(_op, s.Spec(this));
        UiSafety.EnsureActionAllowed(_op, w);
        UiActions.EnsureAltF4Safe(_op, strokes, w);
        InputSender.EnsureForeground(w.Handle, _pid, "keys", _op.Token);
        UiActions.EnsureAltF4Safe(_op, strokes, w, afterActivation: true);
        InputSender.SendStrokes(strokes, _pid, _op.Token);
        return $"sent keys {keys} to \"{UiText.Clip(w.Title, 60)}\"";
    }

    private string Scroll(StepArgs s)
    {
        var t = Resolve(s, needEnabled: false);
        var spec = (s.Text ?? "down").Trim().ToLowerInvariant();
        var dir = spec;
        int count = 1;
        var m = Regex.Match(spec, "^(\\w+)\\s+(\\d+)$");
        if (m.Success)
        {
            dir = m.Groups[1].Value;
            count = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        var r = UiActions.Scroll(_op, t, dir, Math.Clamp(count, 1, 100));
        return r.TryGetValue("vertical_percent", out var v)
            ? $"scrolled {dir} x{count} -> h={r["horizontal_percent"]}% v={v}%"
            : $"{r["action"]} {dir}";
    }

    private string Drag(StepArgs s)
    {
        var a = Resolve(s, needEnabled: true);
        if (string.IsNullOrEmpty(s.Text))
        {
            throw GatewayException.Validation("drag needs \"text\" = the destination (ref or selector)");
        }

        var b = Selector.LooksLikeRef(s.Text)
            ? _ui.ResolveTarget(_op, s.Spec(this), s.Text, null, allowWindow: false)
            : _ui.ResolveTarget(_op, s.Spec(this), null, s.Text, allowWindow: false);
        UiSafety.EnsureActionAllowed(_op, a.Window);
        UiSafety.EnsureActionAllowed(_op, b.Window);
        InputSender.EnsureForeground(a.Window.Handle, _pid, "drag", _op.Token);
        var p1 = UiActions.ClickPoint(_op, a);
        var p2 = UiActions.ClickPoint(_op, b);
        InputSender.Drag(p1.X, p1.Y, p2.X + (s.OffsetX ?? 0), p2.Y + (s.OffsetY ?? 0), _pid, _op.Token);
        return $"dragged {a.Node.Describe()} -> {b.Node.Describe()} - re-read the list or value to confirm the drop took effect";
    }

    private string Close(StepArgs s)
    {
        var t = Resolve(s, needEnabled: false, allowWindow: true, wait: false);
        var w = t.Window;
        UiSafety.EnsureActionAllowed(_op, w);
        if (_ui.IsMainWindow(w))
        {
            throw GatewayException.Denied($"close would close the host application's main window \"{UiText.Clip(w.Title, 60)}\"");
        }

        var el = _op.Core.Wrap(_op.Core.FromHandle(w.Hwnd));
        if (!el.Patterns.Window.TryGetPattern(out var wp))
        {
            throw GatewayException.Validation($"\"{w.Title}\" is not a window");
        }

        // An open dropdown/popup owned by the window swallows the close request: dismiss it with Esc first.
        var popups = TopLevelWindows.List(_pid).Where(o => o.Owner == w.Hwnd && o.Title.Length == 0).ToList();
        if (popups.Count > 0 && InputSender.Activate(w.Handle, _pid, _op.Token))
        {
            InputSender.SendStrokes(InputSender.ParseSendKeys("{ESC}"), _pid, _op.Token);
            _op.Sleep(300);
        }

        return UiActions.Bounded(_op, () => wp.Close(), 3000)
            ? $"closed \"{w.Title}\" (if it asked to save, a dialog is now open - run 'windows')"
            : $"close requested for \"{w.Title}\" (still blocked - a save prompt may be showing)";
    }

    private string Wait(StepArgs s)
    {
        if (s.Target is null && s.Window is null)
        {
            throw GatewayException.Validation("wait needs a target and/or window");
        }

        bool gone = s.Flag("gone");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(s.Timeout);
        while (true)
        {
            _op.Token.ThrowIfCancellationRequested();
            UiTarget? t = null;
            TopLevelWindow? w = null;
            try
            {
                if (s.Target is not null)
                {
                    t = s.IsRef ? _ui.ResolveTarget(_op, s.Spec(this), s.Target, null, false) : _ui.ResolveTarget(_op, s.Spec(this), null, s.Selector, false, TimeSpan.FromSeconds(5));
                }
                else
                {
                    w = _ui.ResolveWindow(_op, new WindowSpec { Pid = _pid, Window = s.Window });
                }
            }
            catch (GatewayException ex) when (ex.Code is GatewayErrorCodes.NotFound or GatewayErrorCodes.TargetNotReady)
            {
            }
            catch (COMException)
            {
                // Vanished between search and read: not found.
            }

            bool found = t is not null || w is not null;
            if (found != gone)
            {
                if (gone)
                {
                    return "gone";
                }

                if (w is not null)
                {
                    _current = w.Hwnd;
                    return $"window ready (now current): {w.HwndHex} \"{w.Title}\"";
                }

                return $"found {t!.Node.Describe()}";
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new GatewayException(GatewayErrorCodes.Timeout, $"timed out after {s.Timeout} s");
            }

            _op.Sleep(300);
        }
    }

    private string Expect(StepArgs s)
    {
        if (s.Target is null && s.Window is null)
        {
            throw GatewayException.Validation("expect needs a target or window");
        }

        var e = new UiActions.Expectation(s.Is, s.TextGiven ? s.Text ?? string.Empty : null, s.Flag("exact"), s.Match, s.Count is { } c && c >= 0 ? c : null);
        var spec = s.Target is null ? new WindowSpec { Pid = _pid, Window = s.Window } : s.Spec(this);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(s.Timeout);
        while (true)
        {
            RunDialogRules();
            var o = UiActions.Evaluate(_op, spec, s.IsRef ? s.Target : null, s.IsRef ? null : s.Selector, e);
            if (o.Pass)
            {
                return "PASS: " + o.Message;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new GatewayException(GatewayErrorCodes.NotFound, $"FAIL: {o.Message} (waited {s.Timeout}s)");
            }

            _op.Sleep(250);
        }
    }

    private string Table(StepArgs s)
    {
        if (s.Target is null)
        {
            throw GatewayException.Validation("table needs a target (the grid/list/table element)");
        }

        var t = Resolve(s, needEnabled: false);
        var r = UiActions.ReadTable(_op, t, Math.Clamp(s.Max ?? 500, 1, 5000));
        var headers = (List<string>)r["headers"]!;
        var rows = (List<List<string>>)r["rows"]!;
        if (s.Out is not null)
        {
            var path = OutputPath(s.Out, "table.csv");
            static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            if (headers.Count > 0)
            {
                sb.AppendLine(string.Join(",", headers.Select(Q)));
            }

            foreach (var row in rows)
            {
                sb.AppendLine(string.Join(",", row.Select(Q)));
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return $"saved {rows.Count} rows x {headers.Count} cols to {path} (source: {r["source"]})";
        }

        var md = new StringBuilder();
        md.AppendLine($"# {rows.Count} rows (source: {r["source"]}){(r["more"] is true ? " - more rows not shown; raise max" : string.Empty)}");
        if (headers.Count > 0)
        {
            md.AppendLine("| " + string.Join(" | ", headers.Select(h => UiText.Clip(h, 40))) + " |");
            md.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", headers.Count)));
        }

        foreach (var row in rows)
        {
            md.AppendLine("| " + string.Join(" | ", row.Select(c => UiText.Clip(c, 40))) + " |");
        }

        return md.ToString().TrimEnd();
    }

    private string Dialogs(StepArgs s)
    {
        if (_rules.Count == 0)
        {
            throw GatewayException.Validation("dialogs needs dialog rules (\"dialogs\" in the script)");
        }

        int before = _handled.Count;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(s.Timeout);
        do
        {
            if (RunDialogRules() == 0)
            {
                _op.Sleep(400);
            }
        }
        while (DateTime.UtcNow < deadline);

        var lines = new List<string>();
        if (_handled.Count == before)
        {
            lines.Add("no matching dialogs appeared");
        }

        foreach (var o in TopLevelWindows.List(_pid).Where(w => w.IsDialogClass || w.Owner != 0))
        {
            lines.Add($"# still open: {o.HwndHex} \"{UiText.Clip(o.Title, 80)}\" ({o.ProcessName}) - no rule matched");
        }

        return string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- dialog rules

    /// <summary>Auto-handles windows of the target pid that match a rule; never answers forbidden dialogs.</summary>
    public int RunDialogRules()
    {
        if (_rules.Count == 0)
        {
            return 0;
        }

        int n = 0;
        foreach (var w in TopLevelWindows.List(_pid))
        {
            foreach (var r in _rules)
            {
                if (string.IsNullOrEmpty(r.Title) || !w.Title.Contains(r.Title, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(r.Proc) && !string.Equals(w.ProcessName, r.Proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? r.Proc[..^4] : r.Proc, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var label = r.Name ?? r.Title;
                try
                {
                    // Always read the dialog's complete text (static texts + button labels): a generic title such as
                    // "Autodesk Revit" can hide a delete / overwrite / license confirmation.
                    var text = UiActions.AllText(_op, _op.Core.FromHandle(w.Hwnd), TimeSpan.FromSeconds(3));
                    if (text.Contains("[... truncated", StringComparison.Ordinal))
                    {
                        Note($"refused to auto-answer dialog \"{w.Title}\": its text could not be read completely (a human must answer it)");
                        continue;
                    }

                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var verdict = RevitAgent.Core.Revit.DialogSafety.Classify(w.Title, lines);
                    if (verdict.IsForbidden || UiScriptValidator.ForbiddenWord(w.Title + " " + text) is not null)
                    {
                        Note($"refused to auto-answer dialog \"{w.Title}\" ({verdict.Reason ?? "contains a forbidden word"}): security / license / sign-in / recovery / delete / overwrite dialogs need a human");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(r.Text) && !text.Contains(r.Text, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string msg;
                    if (!string.IsNullOrEmpty(r.Click))
                    {
                        var root = _op.Core.FromHandle(w.Hwnd);
                        var hit = ElementSearch.Find(_op.Core, root, Selector.Parse(r.Click), 1, TimeSpan.FromSeconds(3), _op.Token).Matches.FirstOrDefault();
                        if (hit is null)
                        {
                            continue;
                        }

                        var node = _op.Core.ReadLive(hit);
                        var res = UiActions.Click(_op, new UiTarget { Window = w, Element = hit, Node = node }, "left", false, r.Mouse);
                        msg = $"{res["action"]} {node.Describe()}";
                    }
                    else if (!string.IsNullOrEmpty(r.Keys))
                    {
                        var strokes = InputSender.ParseSendKeys(r.Keys);
                        UiActions.EnsureAltF4Safe(_op, strokes, w);
                        InputSender.EnsureForeground(w.Handle, _pid, "a dialog rule", _op.Token);
                        UiActions.EnsureAltF4Safe(_op, strokes, w, afterActivation: true);
                        InputSender.SendStrokes(strokes, _pid, _op.Token);
                        msg = $"sent keys {r.Keys}";
                    }
                    else
                    {
                        continue;
                    }

                    _handled.Add($"auto-handled dialog '{label}' ({w.HwndHex} \"{w.Title}\"): {msg}");
                    n++;
                    _op.Sleep(400);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Note($"dialog rule '{label}' failed: {ex.Message}");
                }
            }
        }

        return n;
    }

    // ---------------------------------------------------------------- helpers

    internal long CurrentHwnd()
    {
        if (_current != 0 && NativeMethods.IsWindow(new IntPtr(_current)) && NativeMethods.GetWindowProcessId(new IntPtr(_current)) == _pid)
        {
            return _current;
        }

        // The current window closed (dialog dismissed): fall back to the main window of the process.
        _current = new WindowSpec { Pid = _pid }.Resolve().Hwnd;
        return _current;
    }

    private UiTarget Resolve(StepArgs s, bool needEnabled, bool allowWindow = false, bool wait = true)
    {
        var spec = s.Spec(this);
        string? reference = s.IsRef ? s.Target : null;
        string? selector = s.IsRef ? null : s.Selector;
        if (reference is null && selector is null && !allowWindow)
        {
            throw GatewayException.Validation("Missing target. Use a ref from snapshot (e.g. e12) or a selector (e.g. \"name=OK;type=Button\").");
        }

        return wait
            ? _ui.ResolveActionable(_op, spec, reference, selector, needEnabled, TimeSpan.FromSeconds(s.Timeout), allowWindow)
            : _ui.ResolveTarget(_op, spec, reference, selector, allowWindow);
    }

    private string OutputPath(string? requested, string defaultName)
    {
        var path = UiPaths.Evidence(_ui.Paths, string.IsNullOrWhiteSpace(requested) ? defaultName : requested, _runDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private string? SaveShot(string path)
    {
        try
        {
            var h = CurrentHwnd();
            using var bmp = WindowCapture.CaptureWindow(new IntPtr(h));
            if (bmp is null)
            {
                return null;
            }

            File.WriteAllBytes(path, WindowCapture.EncodePng(bmp));
            return path;
        }
        catch (Exception ex) when (ex is GatewayException or IOException or COMException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void WriteFailSnapshot(string path)
    {
        try
        {
            var h = CurrentHwnd();
            var w = TopLevelWindows.Describe(new IntPtr(h));
            var snap = TreeSnapshot.Take(_op.Core, _op.Core.FromHandle(h), new SnapshotOptions { InteractiveOnly = true, MaxNodes = 300, MaxDepth = 25, Budget = TimeSpan.FromSeconds(15) }, i => "e" + i, _op.Token);
            var sb = new StringBuilder();
            sb.AppendLine($"# window: {UiText.Hex(h)} \"{w?.Title}\" pid={_pid}");
            foreach (var o in TopLevelWindows.List(_pid).Where(o => o.Hwnd != h))
            {
                sb.AppendLine($"# also open: {o.HwndHex} \"{UiText.Clip(o.Title, 80)}\" class={o.ClassName}{(o.Owner == h ? " (owned dialog/popup - probably modal)" : string.Empty)}");
            }

            foreach (var l in snap.Lines)
            {
                sb.AppendLine(l);
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is GatewayException or IOException or COMException)
        {
        }
    }

    private void Note(string message)
    {
        if (_ruleNotes.Add(message))
        {
            _handled.Add("note: " + message);
        }
    }

    /// <summary>UiStep Get-UiStepRoles: nav = steps on the host's main window, check = steps on add-in windows.</summary>
    private List<(string Role, bool Optional)> Roles()
    {
        var list = new List<(string, bool)>();
        var cur = MainMarker;
        foreach (var st in _script.Steps)
        {
            var w = st.Raw("window");
            var eff = string.IsNullOrWhiteSpace(w) ? cur : w!;
            var role = st.Raw("role") is "nav" or "check" ? st.Raw("role")! : eff == MainMarker ? "nav" : "check";
            var gone = st.Flag("gone") || string.Equals(st.Raw("is"), "gone", StringComparison.OrdinalIgnoreCase);
            list.Add((role, st.Flag("continueOnError") || _script.ContinueOnError));
            if (gone)
            {
                if (eff == cur)
                {
                    cur = MainMarker;
                }
            }
            else if (!string.IsNullOrWhiteSpace(w) && st.Do is "use" or "wait" or "snapshot" or "find")
            {
                cur = w!;
            }
        }

        return list;
    }

    /// <summary>UiStep Get-UiStepOutcome: FAIL only when the add-in misbehaved; navigation/layout failures are BLOCK (or WARN when optional).</summary>
    private static string Outcome(string role, bool optional, string status, string error)
    {
        if (status != "FAIL")
        {
            return status;
        }

        bool notProduct = role == "nav" || LayoutError.IsMatch(error);
        return !notProduct ? "FAIL" : optional ? "WARN" : "BLOCK";
    }

    private void WriteReports(UiScriptReport report)
    {
        var json = new
        {
            name = report.Name,
            file = report.File,
            verdict = report.Verdict,
            passed = report.Passed,
            failed = report.Failed,
            skipped = report.Skipped,
            seconds = report.Seconds,
            time = report.Time,
            measured = report.Measured,
            steps = report.Steps.Select(r => new { n = r.N, step = r.Step, status = r.Status, ms = r.Ms, output = r.Output, error = r.Error, screenshot = r.Screenshot, role = r.Role, outcome = r.Outcome }),
        };
        File.WriteAllText(Path.Combine(_runDir, "report.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));

        var md = new StringBuilder();
        md.AppendLine($"# {report.Name} - {report.Verdict}").AppendLine();
        md.AppendLine($"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped in {report.Seconds.ToString(CultureInfo.InvariantCulture)}s - {report.Time} (measured: {report.Measured})").AppendLine();
        md.AppendLine("| # | Status | Step | Time | Details |").AppendLine("|---|---|---|---|---|");
        foreach (var r in report.Steps)
        {
            var det = UiText.Clip(r.Error.Length > 0 ? r.Error : r.Output, 160).Replace("|", "\\|");
            if (r.Screenshot.Length > 0)
            {
                det += $" ![]({Path.GetFileName(r.Screenshot)})";
            }

            md.AppendLine($"| {r.N} | {r.Status} | {r.Step.Replace("|", "\\|")} | {Math.Round(r.Ms / 1000.0, 1).ToString(CultureInfo.InvariantCulture)}s | {det} |");
        }

        File.WriteAllText(Path.Combine(_runDir, "report.md"), md.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Expanded options of one step .</summary>
    private sealed class StepArgs
    {
        private readonly UiScriptStep _st;

        public StepArgs(UiScriptStep st, UiScriptVars vars, int defaultTimeout, int maxTimeout)
        {
            _st = st;
            Do = st.Do;
            Target = Empty(vars.Expand(st.Raw("target")));
            Window = Empty(vars.Expand(st.Raw("window")));
            TextGiven = st.Has("text");
            Text = vars.Expand(st.Raw("text"));
            Is = Empty(vars.Expand(st.Raw("is")));
            Match = Empty(vars.Expand(st.Raw("match")));
            Out = Empty(vars.Expand(st.Raw("out")));
            Count = st.Has("count") ? st.Int("count") : null;
            X = st.Int("x");
            Y = st.Int("y");
            Depth = st.Int("depth");
            Max = st.Int("max");
            Limit = st.Int("limit");
            OffsetX = st.Int("offsetx");
            OffsetY = st.Int("offsety");
            Timeout = Math.Clamp(st.Int("timeout") ?? defaultTimeout, 0, Math.Clamp(maxTimeout, 0, 600));
            var within = Empty(vars.Expand(st.Raw("within")));
            var from = Empty(vars.Expand(st.Raw("from")));
            if (Target is not null && !IsRef)
            {
                var sel = Target;
                if (within is not null && !sel.Contains("in=", StringComparison.OrdinalIgnoreCase))
                {
                    sel += ";in=\"" + within.Replace("\"", "\\\"") + "\"";
                }

                if (string.Equals(from, "end", StringComparison.OrdinalIgnoreCase))
                {
                    sel += ";from=end";
                }

                Selector = sel;
            }
        }

        public string Do { get; }

        public string? Target { get; }

        public string? Selector { get; }

        public bool IsRef => Target is not null && Ui.Selector.LooksLikeRef(Target);

        public string? Window { get; }

        public bool TextGiven { get; }

        public string? Text { get; }

        public string? Is { get; }

        public string? Match { get; }

        public string? Out { get; }

        public int? Count { get; }

        public int? X { get; }

        public int? Y { get; }

        public int? Depth { get; }

        public int? Max { get; }

        public int? Limit { get; }

        public int? OffsetX { get; }

        public int? OffsetY { get; }

        public int Timeout { get; }

        public bool Flag(string name) => _st.Flag(name);

        /// <summary>The step's window: its own 'window', else the run's current window.</summary>
        public WindowSpec Spec(UiScriptRunner runner) => Window is not null
            ? new WindowSpec { Pid = runner._pid, Window = Window }
            : new WindowSpec { Pid = runner._pid, Hwnd = runner.CurrentHwnd() };

        private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
