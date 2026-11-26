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

/// <summary>Result of one script step (report.json fields). Status: passed | failed | skipped | blocked.</summary>
public sealed record UiStepResult(int N, string Step, string Status, long Ms, string Observed, string Expected, string Error, string Screenshot, bool Optional);

/// <summary>Result of a script run. Verdict: passed | failed | blocked.</summary>
public sealed class UiScriptReport
{
    public required string Name { get; init; }

    public string? File { get; init; }

    public required string Verdict { get; init; }

    public int Passed { get; init; }

    /// <summary>Required steps that failed.</summary>
    public int Failed { get; init; }

    /// <summary>Optional steps that failed or were refused (recorded, but they do not affect the verdict).</summary>
    public int OptionalFailed { get; init; }

    /// <summary>Required steps refused by a safety rule or stopped by a forbidden dialog.</summary>
    public int Blocked { get; init; }

    public int Skipped { get; init; }

    public double Seconds { get; init; }

    public required string Time { get; init; }

    public required IReadOnlyList<UiStepResult> Steps { get; init; }

    public required string RunDir { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Executes a UI script against one process:
/// starts at the main window of the pid, runs steps in order with auto-wait and dialog
/// rules, screenshots on failure (or each step), writes report.json / report.md and NN_fail_snapshot.txt.
/// Every window it touches must belong to the target pid.
/// </summary>
public sealed class UiScriptRunner
{
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
    private int _refusals;

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
        _current = new WindowSpec { Pid = _pid }.Resolve().Hwnd;

        var results = new List<UiStepResult>();
        bool stop = false;
        foreach (var step in _script.Steps)
        {
            var label = step.Label;
            var expected = ExpectedOf(step);
            if (stop || DateTime.UtcNow > _deadline)
            {
                results.Add(new UiStepResult(step.Number, label, "skipped", 0, string.Empty, expected, stop ? string.Empty : "run time budget exhausted", string.Empty, step.Optional));
                continue;
            }

            var sw = Stopwatch.StartNew();
            string status = "passed", observed = string.Empty, error = string.Empty, shot = string.Empty;
            int before = _handled.Count;
            int refusals = _refusals;
            try
            {
                observed = Execute(step);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ExpectationNotMet ex)
            {
                status = "failed";
                observed = ex.Observed;
                error = ex.Message;
            }
            catch (GatewayException ex)
            {
                status = ex.Code == GatewayErrorCodes.PermissionDenied ? "blocked" : "failed";
                error = ex.Message;
            }
            catch (Exception ex)
            {
                // Any other failure fails the step (never the whole run, so the report is always written).
                status = "failed";
                error = UiaCore.Translate(ex)?.Message ?? $"{ex.GetType().Name}: {ex.Message}";
            }

            sw.Stop();
            if (status == "failed" && _refusals > refusals)
            {
                status = "blocked"; // a forbidden dialog is in the way and was left for a human
            }

            if (_handled.Count > before)
            {
                observed = string.Join("\n", _handled.Skip(before).Append(observed)).Trim();
            }

            if (_shots == "each" || (status is "failed" or "blocked" && _shots != "none"))
            {
                shot = SaveShot(Path.Combine(_runDir, $"{step.Number:D2}_{status}.png")) ?? string.Empty;
            }

            if (status is "failed" or "blocked")
            {
                WriteFailSnapshot(Path.Combine(_runDir, $"{step.Number:D2}_{status}_snapshot.txt"));
                if (!step.Optional)
                {
                    stop = true;
                }
            }

            results.Add(new UiStepResult(step.Number, label, status, sw.ElapsedMilliseconds, observed, expected, error, shot, step.Optional));
        }

        int Count(string status, bool optional) => results.Count(r => r.Status == status && r.Optional == optional);
        var blocked = Count("blocked", false);
        var failed = Count("failed", false);
        var report = new UiScriptReport
        {
            Name = _script.Name,
            File = _script.Path,
            Verdict = blocked > 0 ? "blocked" : failed > 0 ? "failed" : "passed",
            Passed = results.Count(r => r.Status == "passed"),
            Failed = failed,
            Blocked = blocked,
            OptionalFailed = Count("failed", true) + Count("blocked", true),
            Skipped = results.Count(r => r.Status == "skipped"),
            Seconds = Math.Round(results.Sum(r => r.Ms) / 1000.0, 1),
            Time = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            Steps = results,
            RunDir = _runDir,
            Warnings = warnings,
        };
        WriteReports(report);
        return report;
    }

    /// <summary>Human-readable expectation of an expect / wait step (empty for other steps).</summary>
    private static string ExpectedOf(UiScriptStep st)
    {
        if (st.Action == "wait")
        {
            return "state " + (st.Raw("state") ?? "appears");
        }

        if (st.Action != "expect")
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (st.Raw("is") is { Length: > 0 } i)
        {
            parts.Add("is " + i);
        }

        if (st.Has("text"))
        {
            parts.Add($"text {(st.Flag("exact") ? "==" : "contains")} '{st.Raw("text")}'");
        }

        if (st.Raw("match") is { Length: > 0 } m)
        {
            parts.Add($"text matches /{m}/");
        }

        if (st.Has("count"))
        {
            parts.Add("count == " + st.Raw("count"));
        }

        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- dispatch

    private string Execute(UiScriptStep st)
    {
        var remaining = (int)Math.Max(0, (_deadline - DateTime.UtcNow).TotalSeconds);
        var s = new StepArgs(st, _vars, _script.TimeoutSeconds, remaining);
        switch (s.Action)
        {
            case "pause":
                _op.Sleep(Math.Clamp(st.Int("ms") ?? 500, 0, 600_000));
                return "paused";
            case "list_windows":
                return string.Join("\n", TopLevelWindows.List(_pid)
                    .Where(w => s.Title is null || w.Title.Contains(s.Title, StringComparison.OrdinalIgnoreCase))
                    .Select(w =>
                        $"{(w.Hwnd == _current ? '*' : ' ')} {w.HwndHex,-11} pid={w.ProcessId,-7} {w.ProcessName,-20} \"{UiText.Clip(w.Title, 100)}\"{(w.Owner != 0 ? $"  (owned by {UiText.Hex(w.Owner)})" : string.Empty)}{(w.IsDialogClass ? "  [dialog]" : string.Empty)}"));
            case "attach":
            {
                if (s.Window is null)
                {
                    throw GatewayException.Validation("attach needs 'window' (title substring, /regex/ or hwnd:0x..)");
                }

                var w = _ui.ResolveWindow(_op, new WindowSpec { Pid = _pid, Window = s.Window });
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

            case "screenshot":
                return Screenshot(s);
            case "click":
                return Click(s);
            case "type":
                return TypeStep(s);
            case "keys":
                return Keys(s);
            case "toggle":
            {
                var t = Resolve(s, needEnabled: true);
                var r = UiActions.Toggle(_op, t, s.State);
                return $"{t.Node.Describe()} -> {r["state"]}";
            }

            case "expand":
            {
                var t = Resolve(s, needEnabled: true);
                var r = UiActions.Expand(_op, t, s.Flag("collapse"));
                return $"{r["action"]} {t.Node.Describe()}";
            }

            case "select":
            {
                if (s.Item is not null && s.Index is not null)
                {
                    throw GatewayException.Validation("select: pass either item or index, not both");
                }

                var t = Resolve(s, needEnabled: true);
                var r = UiActions.Select(_op, t, s.Item, s.Index);
                return $"selected {r["item"]} in {t.Node.Describe()}{(r.TryGetValue("value", out var v) && v is not null ? $" -> value=\"{v}\"" : string.Empty)}";
            }

            case "scroll":
                return Scroll(s);
            case "wait":
                return Wait(s);
            case "expect":
                return Expect(s);
            case "read_table":
                return Table(s);
            default:
                throw GatewayException.Validation($"step has unknown action '{s.Action}'");
        }
    }

    private string Snapshot(StepArgs s)
    {
        var t = Resolve(s, needEnabled: false, allowWindow: true, wait: false);
        _current = t.Window.Hwnd;
        var snap = TreeSnapshot.Take(_op.Core, t.Element, new SnapshotOptions
        {
            InteractiveOnly = !s.Has("interactive_only") || s.Flag("interactive_only"),
            MaxDepth = s.MaxDepth ?? 25,
            MaxNodes = s.MaxNodes ?? 400,
            IncludeOffscreen = s.Flag("include_offscreen"),
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
        var selector = s.Selector ?? throw GatewayException.Validation("find needs a 'selector', e.g. \"type=Button\"");
        var (window, found) = _ui.FindAll(_op, s.Spec(this), selector, int.MaxValue, TimeSpan.FromSeconds(Math.Max(1, s.Timeout)), s.Flag("include_offscreen"));
        _current = window.Hwnd;
        if (found.Matches.Count == 0)
        {
            return $"no elements match '{selector}' in \"{window.Title}\"";
        }

        var set = _ui.Refs.GetOrCreate(_op.Session, window);
        var max = Math.Clamp(s.Limit ?? 20, 1, 100);
        var lines = found.Matches.Take(max).Select(e =>
        {
            var n = _op.Core.ReadLive(e);
            var r = _ui.AddRef(set, n);
            return TreeSnapshot.FormatNode(n, r, 0, null);
        }).ToList();
        if (found.Matches.Count > max)
        {
            lines.Add($"# ... {found.Matches.Count - max} more; refine the selector or raise limit");
        }

        return string.Join("\n", lines);
    }

    private string Screenshot(StepArgs s)
    {
        var path = OutputPath(s.SavePath, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var t = Resolve(s, needEnabled: false, allowWindow: true);
        if (NativeMethods.IsIconic(t.Window.Handle))
        {
            throw GatewayException.NotReady($"window \"{UiText.Clip(t.Window.Title, 60)}\" is minimized; cannot capture it");
        }

        using var full = WindowCapture.CaptureWindow(t.Window.Handle)
            ?? throw GatewayException.NotReady("window printed blank or has no size (hidden, minimized, or a canvas PrintWindow cannot render); use the ui_screenshot tool or view_export_image");
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

    private string Click(StepArgs s)
    {
        var button = (s.Button ?? "left").ToLowerInvariant();
        if (button is not ("left" or "right" or "middle"))
        {
            throw GatewayException.Validation("click: button must be left, right or middle");
        }

        var dbl = s.Flag("double");
        if (s.Ref is null && s.Selector is null && (s.X is not null || s.Y is not null))
        {
            if (s.X is not { } x || s.Y is not { } y || x < 0 || y < 0)
            {
                throw GatewayException.Validation("click: pass both x and y (window pixels)");
            }

            var w = _ui.ResolveWindow(_op, s.Spec(this));
            UiActions.ClickAt(_op, w, x, y, button, dbl);
            return $"clicked at {x},{y}";
        }

        bool mouse = s.Flag("mouse");
        var t = Resolve(s, needEnabled: !mouse && !dbl && button == "left");
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
            throw GatewayException.Validation("type needs 'text' (or clear: true)");
        }

        text ??= string.Empty;
        if (s.Ref is null && s.Selector is null)
        {
            var w = _ui.ResolveWindow(_op, s.Spec(this));
            UiActions.TypeIntoFocused(_op, w, text);
            return $"typed {text.Length} chars into the focused element of \"{UiText.Clip(w.Title, 60)}\" (keyboard)";
        }

        var t = Resolve(s, needEnabled: true);
        var r = UiActions.Type(_op, t, text, s.Flag("keyboard"), !clear && s.Flag("append"), s.Flag("verify"));
        return r["action"] switch
        {
            "set_value" => $"set value of {t.Node.Describe()} -> \"{UiText.Clip(r["value"] as string, 120)}\"",
            "cleared" => $"cleared {t.Node.Describe()} (keyboard)",
            _ => $"typed {text.Length} chars into {t.Node.Describe()} ({r["action"]}){(r.ContainsKey("verified") ? $" - verified value \"{UiText.Clip(r["value"] as string, 80)}\"" : string.Empty)}",
        };
    }

    private string Keys(StepArgs s)
    {
        var keys = s.Keys;
        if (string.IsNullOrEmpty(keys))
        {
            throw GatewayException.Validation("keys needs 'keys', e.g. \"^s\" or \"{ENTER}\"");
        }

        var strokes = InputSender.ParseSendKeys(keys);
        UiTarget? t = null;
        TopLevelWindow w;
        if (s.Ref is not null || s.Selector is not null)
        {
            t = Resolve(s, needEnabled: true);
            w = t.Window;
        }
        else
        {
            w = _ui.ResolveWindow(_op, s.Spec(this));
        }

        UiSafety.EnsureActionAllowed(_op, w);
        UiActions.EnsureAltF4Safe(_op, strokes, w);
        InputSender.EnsureForeground(w.Handle, _pid, "keys", _op.Token);
        if (t is not null)
        {
            UiActions.FocusElement(_op, t.Wrap(_op.Core));
            UiActions.EnsureFocusWithin(_op, t);
        }

        UiActions.EnsureAltF4Safe(_op, strokes, w, afterActivation: true);
        InputSender.SendStrokes(strokes, _pid, _op.Token);
        return $"sent keys {keys} to \"{UiText.Clip(w.Title, 60)}\"";
    }

    private string Scroll(StepArgs s)
    {
        var t = Resolve(s, needEnabled: false);
        var dir = (s.Direction ?? "down").Trim().ToLowerInvariant();
        var count = Math.Clamp(s.Amount ?? 1, 1, 100);
        var r = UiActions.Scroll(_op, t, dir, count);
        return r.TryGetValue("vertical_percent", out var v)
            ? $"scrolled {dir} x{count} -> h={r["horizontal_percent"]}% v={v}%"
            : $"{r["action"]} {dir}";
    }

    private string Wait(StepArgs s)
    {
        var state = (s.State ?? "appears").ToLowerInvariant();
        if (state is not ("appears" or "gone" or "enabled"))
        {
            throw GatewayException.Validation("wait: state must be appears, gone or enabled");
        }

        bool element = s.Ref is not null || s.Selector is not null;
        if (!element && s.Window is null)
        {
            throw GatewayException.Validation("wait needs a ref/selector (element) or a window");
        }

        if (!element && state == "enabled")
        {
            throw GatewayException.Validation("wait: state=enabled needs a ref or selector");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(s.Timeout);
        while (true)
        {
            _op.Token.ThrowIfCancellationRequested();
            RunDialogRules();
            UiTarget? t = null;
            TopLevelWindow? w = null;
            try
            {
                if (element)
                {
                    t = s.Ref is not null
                        ? _ui.ResolveTarget(_op, s.Spec(this), s.Ref, null, false)
                        : _ui.ResolveTarget(_op, s.Spec(this), null, s.Selector, false, TimeSpan.FromSeconds(5));
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
            bool ok = state switch { "gone" => !found, "enabled" => t is not null && t.Node.Enabled, _ => found };
            if (ok)
            {
                if (state == "gone")
                {
                    return "gone";
                }

                if (w is not null)
                {
                    _current = w.Hwnd;
                    return $"window ready (now current): {w.HwndHex} \"{w.Title}\"";
                }

                return $"{state}: {t!.Node.Describe()}";
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new GatewayException(GatewayErrorCodes.Timeout, $"timed out after {s.Timeout} s waiting for state '{state}' (last seen: {(found ? "present" : "not found")})");
            }

            _op.Sleep(300);
        }
    }

    private string Expect(StepArgs s)
    {
        if (s.Ref is null && s.Selector is null && s.Window is null)
        {
            throw GatewayException.Validation("expect needs a ref/selector or a window");
        }

        var e = new UiActions.Expectation(s.Is, s.TextGiven ? s.Text ?? string.Empty : null, s.Flag("exact"), s.Match, s.Count is { } c && c >= 0 ? c : null);
        var spec = s.Ref is null && s.Selector is null ? new WindowSpec { Pid = _pid, Window = s.Window } : s.Spec(this);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(s.Timeout);
        while (true)
        {
            RunDialogRules();
            var o = UiActions.Evaluate(_op, spec, s.Ref, s.Selector, e);
            if (o.Pass)
            {
                return o.Message;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new ExpectationNotMet(o.Message, $"expectation not met after {s.Timeout}s");
            }

            _op.Sleep(250);
        }
    }

    /// <summary>An expect step whose condition never held: the last observation is the step's observed value.</summary>
    private sealed class ExpectationNotMet : Exception
    {
        public ExpectationNotMet(string observed, string message)
            : base(message)
        {
            Observed = observed;
        }

        public string Observed { get; }
    }

    private string Table(StepArgs s)
    {
        if (s.Ref is null && s.Selector is null)
        {
            throw GatewayException.Validation("read_table needs a ref or selector (the grid/list/table element)");
        }

        var t = Resolve(s, needEnabled: false);
        var r = UiActions.ReadTable(_op, t, Math.Clamp(s.Limit ?? 100, 1, 2000));
        var headers = (List<string>)r["headers"]!;
        var rows = (List<List<string>>)r["rows"]!;
        if (s.SavePath is not null)
        {
            var path = OutputPath(s.SavePath, "table.csv");
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
        md.AppendLine($"# {rows.Count} rows (source: {r["source"]}){(r["more"] is true ? " - more rows not shown; raise limit" : string.Empty)}");
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
                        _refusals++;
                        Note($"refused to auto-answer dialog \"{w.Title}\": its text could not be read completely (a human must answer it)");
                        continue;
                    }

                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var verdict = RevitAgent.Core.Revit.DialogSafety.Classify(w.Title, lines);
                    if (verdict.IsForbidden || UiScriptValidator.ForbiddenWord(w.Title + " " + text) is not null)
                    {
                        _refusals++;
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
        string? reference = s.Ref;
        string? selector = s.Selector;
        if (reference is not null && selector is not null)
        {
            throw GatewayException.Validation("Pass either 'ref' or 'selector', not both.");
        }

        if (reference is null && selector is null && !allowWindow)
        {
            throw GatewayException.Validation("Missing target. Use a 'ref' from snapshot (e.g. e12) or a 'selector' (e.g. \"name=OK;type=Button\").");
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

    private void WriteReports(UiScriptReport report)
    {
        var json = new
        {
            name = report.Name,
            file = report.File,
            verdict = report.Verdict,
            passed = report.Passed,
            failed = report.Failed,
            blocked = report.Blocked,
            optional_failed = report.OptionalFailed,
            skipped = report.Skipped,
            seconds = report.Seconds,
            time = report.Time,
            steps = report.Steps.Select(r => new { n = r.N, step = r.Step, status = r.Status, optional = r.Optional, ms = r.Ms, observed = r.Observed, expected = r.Expected, error = r.Error, screenshot = r.Screenshot }),
        };
        File.WriteAllText(Path.Combine(_runDir, "report.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));

        var md = new StringBuilder();
        md.AppendLine($"# {report.Name} - {report.Verdict}").AppendLine();
        md.AppendLine($"{report.Passed} passed, {report.Failed} failed, {report.Blocked} blocked, {report.OptionalFailed} optional failed, {report.Skipped} skipped in {report.Seconds.ToString(CultureInfo.InvariantCulture)}s - {report.Time}").AppendLine();
        md.AppendLine("| # | Status | Step | Time | Details |").AppendLine("|---|---|---|---|---|");
        foreach (var r in report.Steps)
        {
            var det = UiText.Clip(r.Error.Length > 0 ? r.Error : r.Observed, 160).Replace("|", "\\|");
            if (r.Screenshot.Length > 0)
            {
                det += $" ![]({Path.GetFileName(r.Screenshot)})";
            }

            md.AppendLine($"| {r.N} | {r.Status} | {r.Step.Replace("|", "\\|")} | {Math.Round(r.Ms / 1000.0, 1).ToString(CultureInfo.InvariantCulture)}s | {det} |");
        }

        File.WriteAllText(Path.Combine(_runDir, "report.md"), md.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Expanded options of one step (the argument names of the matching ui_* tool).</summary>
    private sealed class StepArgs
    {
        private readonly UiScriptStep _st;
        private readonly UiScriptVars _vars;

        public StepArgs(UiScriptStep st, UiScriptVars vars, int defaultTimeout, int maxTimeout)
        {
            _st = st;
            _vars = vars;
            Action = st.Action;
            Ref = Str("ref");
            Selector = Str("selector");
            Window = Str("window");
            TextGiven = st.Has("text");
            Text = vars.Expand(st.Raw("text"));
            Is = Str("is");
            Match = Str("match");
            State = Str("state");
            Item = Str("item");
            Button = Str("button");
            Keys = vars.Expand(st.Raw("keys"));
            Direction = Str("direction");
            Title = Str("title");
            SavePath = Str("save_path");
            Count = st.Has("count") ? st.Int("count") : null;
            Index = st.Int("index");
            X = st.Int("x");
            Y = st.Int("y");
            Amount = st.Int("amount");
            MaxDepth = st.Int("max_depth");
            MaxNodes = st.Int("max_nodes");
            Limit = st.Int("limit");
            Timeout = Math.Clamp(st.Int("timeout_s") ?? defaultTimeout, 0, Math.Clamp(maxTimeout, 0, 600));
        }

        public string Action { get; }

        public string? Ref { get; }

        public string? Selector { get; }

        public string? Window { get; }

        public bool TextGiven { get; }

        public string? Text { get; }

        public string? Is { get; }

        public string? Match { get; }

        public string? State { get; }

        public string? Item { get; }

        public string? Button { get; }

        public string? Keys { get; }

        public string? Direction { get; }

        public string? Title { get; }

        public string? SavePath { get; }

        public int? Count { get; }

        public int? Index { get; }

        public int? X { get; }

        public int? Y { get; }

        public int? Amount { get; }

        public int? MaxDepth { get; }

        public int? MaxNodes { get; }

        public int? Limit { get; }

        public int Timeout { get; }

        public bool Has(string name) => _st.Has(name);

        public bool Flag(string name) => _st.Flag(name);

        /// <summary>The step's window: its own 'window', else the run's current window.</summary>
        public WindowSpec Spec(UiScriptRunner runner) => Window is not null
            ? new WindowSpec { Pid = runner._pid, Window = Window }
            : new WindowSpec { Pid = runner._pid, Hwnd = runner.CurrentHwnd() };

        private string? Str(string name) => _vars.Expand(_st.Raw(name)) is { Length: > 0 } v ? v : null;
    }
}
