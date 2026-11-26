using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui.Tools;

/// <summary>ui_list_windows — top-level windows (EnumWindows; includes owned dialogs such as TaskDialogs).</summary>
public sealed class UiListWindowsTool : UiToolBase
{
    public UiListWindowsTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_list_windows";

    public override string Description =>
        "List visible top-level windows (z-order, topmost first): hwnd, title, class, pid, owner, is_modal_dialog (#32770 or an owned window whose owner is disabled), enabled, bounds, is_main. " +
        "Pass pid to see one process (Revit main window + its add-in dialogs, TaskDialogs and popups — each is a separate top-level window). Read-only, ~50 ms, works even when the session is locked " +
        "(the result says whether the desktop is usable for UI automation). Use the hwnd or title as 'window' in the other ui_* tools.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .Integer("pid", "Only windows of this process.")
        .String("title", "Only windows whose title contains this text (case-insensitive).")
        .Boolean("include_untitled", "Also list untitled, unowned windows (helper/hidden frames). Default false.", defaultValue: false)
        .Integer("limit", "Maximum windows to return (default 100).", defaultValue: 100, minimum: 1, maximum: 500);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = call.GetInt("pid");
        var title = call.GetString("title");
        var untitled = call.GetBool("include_untitled");
        var limit = call.GetLimit(defaultValue: 100, max: 500);
        if (pid is { } p)
        {
            WindowSpec.EnsureProcessAlive(p);
        }

        var all = TopLevelWindows.List(pid);
        var mains = all.GroupBy(w => w.ProcessId).ToDictionary(g => g.Key, g => TopLevelWindows.PickMain(g)?.Hwnd);
        var shown = all
            .Where(w => untitled || w.Title.Length > 0 || w.Owner != 0 || w.ClassName.StartsWith("HwndWrapper", StringComparison.Ordinal))
            .Where(w => title is null || w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var reason = UiEnvironment.DesktopUnavailableReason();
        return Task.FromResult(ToolResult.Json(new
        {
            count = Math.Min(shown.Count, limit),
            total = shown.Count,
            truncated = shown.Count > limit,
            desktop = new { available = reason is null, reason },
            windows = shown.Take(limit).Select(w => w.ToDto(mains.TryGetValue(w.ProcessId, out var m) && m == w.Hwnd)).ToArray(),
        }));
    }
}

/// <summary>ui_snapshot — accessibility tree with refs.</summary>
public sealed class UiSnapshotTool : UiToolBase
{
    public UiSnapshotTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_snapshot";

    public override string Description =>
        "UI Automation tree of a window as text, one element per line with a ref (e12) for actions. " +
        "Big window: snapshot a subtree (ref/selector), e.g. the Revit ribbon. The header lists other windows of the process (dialogs). " +
        "The Revit canvas has no tree: use ui_screenshot and ui_click x/y. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()), "the subtree root")
        .Boolean("interactive_only", "Only actionable or labelled elements.", defaultValue: true)
        .Integer("max_depth", "Max depth.", defaultValue: 25, minimum: 1, maximum: 100)
        .Integer("max_nodes", "Max elements.", defaultValue: 400, minimum: 10, maximum: 3000)
        .Boolean("bounds", "Add box=x,y,w,h (window pixels).", defaultValue: false)
        .Boolean("include_offscreen", "Include offscreen elements.", defaultValue: false)
        .Boolean("raw", "Raw view (text inside buttons).", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var interactive = call.GetBool("interactive_only", true);
        var maxDepth = call.GetInt("max_depth", 25) ?? 25;
        var maxNodes = Math.Clamp(call.GetInt("max_nodes", 400) ?? 400, 10, 3000);
        var bounds = call.GetBool("bounds");
        var offscreen = call.GetBool("include_offscreen");
        var raw = call.GetBool("raw");

        CheckDesktop();
        var (header, text) = await Ui.RunAsync(call.SessionId, TimeSpan.FromSeconds(45), input: false, cancellationToken, op =>
        {
            var t = Ui.ResolveTarget(op, spec, reference, selector, allowWindow: true);
            CheckTarget(t.Window);
            var options = new SnapshotOptions
            {
                InteractiveOnly = interactive,
                MaxDepth = maxDepth,
                MaxNodes = maxNodes,
                Bounds = bounds,
                IncludeOffscreen = offscreen,
                Raw = raw,
                OriginX = t.Window.Left,
                OriginY = t.Window.Top,
                Budget = TimeSpan.FromSeconds(30),
            };
            var snap = TreeSnapshot.Take(op.Core, t.Element, options, i => "e" + i, op.Token);
            var set = Ui.Refs.Replace(op.Session, t.Window); // only after a successful walk
            foreach (var n in snap.RefNodes)
            {
                set.Add(n);
            }

            var others = TopLevelWindows.List(t.Window.ProcessId)
                .Where(o => o.Hwnd != t.Window.Hwnd && (o.Title.Length > 0 || o.Owner != 0))
                .Select(o => new
                {
                    hwnd = o.HwndHex,
                    title = o.Title,
                    @class = o.ClassName,
                    kind = o.Owner == t.Window.Hwnd ? "owned dialog/popup (probably modal)" : o.IsModalDialog ? "modal dialog" : "same app",
                })
                .Take(20)
                .ToArray();

            var sb = new StringBuilder();
            foreach (var line in snap.Lines)
            {
                sb.AppendLine(line);
            }

            if (snap.Truncated)
            {
                sb.AppendLine(snap.TimedOut
                    ? $"# TRUNCATED after {options.Budget.TotalSeconds:0}s — snapshot a subtree (ref/selector) or a smaller window"
                    : $"# TRUNCATED at {snap.RefNodes.Count} nodes — use interactive_only, snapshot a subtree ref, or raise max_nodes");
            }

            if (snap.LabelsSkipped > 0)
            {
                sb.AppendLine($"# {snap.LabelsSkipped} unnamed buttons/items left without label= (label lookups used their share of the time budget)");
            }

            var h = new Dictionary<string, object?>
            {
                ["window"] = WindowInfo(op, t.Window),
                ["root"] = t.IsWindow ? null : t.Node.Describe(),
                ["refs"] = snap.RefNodes.Count,
                ["visited"] = snap.Visited,
                ["truncated"] = snap.Truncated,
                ["depth_limited"] = snap.DepthLimited ? true : null,
                ["elapsed_ms"] = snap.ElapsedMs,
                ["perf"] = new
                {
                    round_trips = snap.RoundTrips,
                    ms_per_round_trip = snap.RoundTrips > 0 ? Math.Round((double)snap.ElapsedMs / snap.RoundTrips, 1) : 0,
                    label_lookups = snap.LabelLookups,
                    label_ms = snap.LabelMs,
                    labels_skipped = snap.LabelsSkipped,
                },
                ["also_open"] = others.Length > 0 ? others : null,
                ["notes"] = op.Notes.Count > 0 ? op.Notes.ToArray() : null,
            };
            return (h, sb.ToString());
        }).ConfigureAwait(false);

        return ToolResult.Json(header).AddText(text);
    }
}

/// <summary>ui_find — selector search with refs.</summary>
public sealed class UiFindTool : UiToolBase
{
    public UiFindTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_find";

    public override string Description =>
        "Find elements matching a selector in a window and give them refs (added to the window's current refs). Onscreen matches first, document order. " +
        "Example: selector 'type=Button;name~=Save', 'id=ID_OBJECTS_WALL' (Revit ribbon), 'id=CommandButton_1' (TaskDialog OK). " +
        "Bounded search (timeout_s, default 10 s; partial results are flagged 'stopped'). Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Targeting(Schema.Object())
        .String("selector", SelectorHelp, required: true)
        .Integer("limit", "Maximum matches to return (default 20).", defaultValue: 20, minimum: 1, maximum: 100)
        .Boolean("include_offscreen", "Also search inside offscreen containers.", defaultValue: false)
        .Number("timeout_s", "Search time budget in seconds (default 10, max 60).", defaultValue: 10);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var selector = call.RequireString("selector");
        var limit = call.GetLimit(defaultValue: 20, max: 100);
        var offscreen = call.GetBool("include_offscreen");
        var budget = Seconds(call, "timeout_s", 10, 60);
        CheckDesktop();
        var result = await Ui.RunAsync(call.SessionId, budget + TimeSpan.FromSeconds(15), input: false, cancellationToken, op =>
        {
            var (window, found) = Ui.FindAll(op, spec, selector, int.MaxValue, budget, offscreen);
            CheckTarget(window);
            var set = Ui.Refs.GetOrCreate(op.Session, window);
            var matches = new List<object>();
            foreach (var e in found.Matches.Take(limit))
            {
                var n = op.Core.ReadLive(e);
                if (TreeSnapshot.NeedsLabel(n))
                {
                    var label = TreeSnapshot.RawLabel(op.Core, e, n.Type, out _);
                    if (label.Length > 0)
                    {
                        n.Label = label;
                    }
                }

                var r = Ui.AddRef(set, n);
                matches.Add(new
                {
                    @ref = r,
                    line = TreeSnapshot.FormatNode(n, r, 0, null),
                    type = n.Type,
                    name = n.Name,
                    id = n.AutomationId.Length > 0 ? n.AutomationId : null,
                    offscreen = n.Offscreen ? true : (bool?)null,
                    enabled = n.Enabled,
                });
            }

            return new
            {
                window = WindowInfo(op, window),
                selector,
                count = found.Matches.Count,
                returned = matches.Count,
                matches,
                stopped = found.Stopped,
                visited = found.Visited,
                elapsed_ms = found.ElapsedMs,
            };
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_inspect — all properties and patterns of one element.</summary>
public sealed class UiInspectTool : UiToolBase
{
    public UiInspectTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_inspect";

    public override string Description =>
        "All UI Automation properties of one element (or of the window when no ref/selector): type, name, automation_id, class, framework, bounds (screen and window-relative), " +
        "enabled/offscreen/focused, supported patterns, value, toggle/expand/selected state, range, scroll, window state, default action, and the actions the gateway can perform. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()));

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        CheckDesktop();
        var result = await Ui.RunAsync(call.SessionId, TimeSpan.FromSeconds(30), input: false, cancellationToken, op =>
        {
            var t = Ui.ResolveTarget(op, spec, reference, selector, allowWindow: true);
            CheckTarget(t.Window);
            var d = UiActions.Inspect(op, t);
            d["target"] = Ui.TargetInfo(op, t);
            if (op.Notes.Count > 0)
            {
                d["notes"] = op.Notes.ToArray();
            }

            return d;
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_read_table — DataGrid / list / table as header + rows.</summary>
public sealed class UiReadTableTool : UiToolBase
{
    public UiReadTableTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_read_table";

    public override string Description =>
        "Read a grid/table/list element as headers + rows of cell text (GridPattern/TablePattern, else row-like children: DataItem/ListItem rows, WinForms DataGridView). " +
        "Use it to verify data shown by an add-in (schedules, result lists). Virtualized grids return only realized rows: scroll and read again. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()), "the grid / list / table")
        .Integer("limit", "Maximum rows (default 100).", defaultValue: 100, minimum: 1, maximum: 2000);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var limit = call.GetLimit(defaultValue: 100, max: 2000);
        CheckDesktop();
        var result = await Ui.RunAsync(call.SessionId, TimeSpan.FromSeconds(50), input: false, cancellationToken, op =>
        {
            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: false, UiAutomationService.DefaultActionWait);
            CheckTarget(t.Window);
            var d = UiActions.ReadTable(op, t, limit);
            d["target"] = Ui.TargetInfo(op, t);
            return d;
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_screenshot — window or element PNG for the agent's eyes. Fails closed: never returns pixels of another window.</summary>
public sealed class UiScreenshotTool : UiToolBase
{
    private readonly BridgeClient? _bridge;

    public UiScreenshotTool(UiAutomationService ui, BridgeClient? bridge = null)
        : base(ui)
    {
        _bridge = bridge;
    }

    public override string Name => "ui_screenshot";

    public override string Description =>
        "PNG of a window or element (ref/selector), scaled to max_width: {path, scale, method, captured_window}. " +
        "method: print_window (works covered); revit_api (black Revit canvas: Bridge-rendered active model view, no UI); " +
        "screen (that window's rectangle only, if uncovered and the session renders). Else TARGET_NOT_READY. ui_click x/y = pixel / scale.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()), "an element to crop to")
        .String("save_path", "PNG path in captures or runs.")
        .Integer("max_width", "Max image width.", defaultValue: 1600, minimum: 100, maximum: 4000)
        .Boolean("include_image", "Return the image (false: only save).", defaultValue: true);

    private sealed record Outcome(Dictionary<string, object?>? Info, byte[]? Png, int PendingPid, string? PendingTitle);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var maxWidth = Math.Clamp(call.GetInt("max_width", 1600) ?? 1600, 100, 4000);
        var includeImage = call.GetBool("include_image", true);
        var path = ResolveSavePath(call.GetString("save_path"));

        var outcome = await CaptureAsync(call.SessionId, spec, reference, selector, maxWidth, includeImage, path, _bridge is not null, null, cancellationToken).ConfigureAwait(false);
        if (outcome.Info is null)
        {
            string? bridgeError = null;
            try
            {
                var args = new JsonObject { ["save_path"] = path, ["pixel_width"] = maxWidth };
                var res = await _bridge!.CallAsync(outcome.PendingPid, BridgeToolNames.ViewExportImage, JsonSerializer.SerializeToElement(args), TimeSpan.FromSeconds(75), cancellationToken).ConfigureAwait(false);
                outcome = FromBridge(res, path, includeImage, outcome.PendingPid, outcome.PendingTitle ?? string.Empty);
            }
            catch (GatewayException ex)
            {
                bridgeError = $"{ex.Code}: {ex.Message}";
            }

            if (outcome.Info is null)
            {
                outcome = await CaptureAsync(call.SessionId, spec, reference, selector, maxWidth, includeImage, path, false, bridgeError, cancellationToken).ConfigureAwait(false);
            }
        }

        var result = ToolResult.Json(outcome.Info!);
        if (outcome.Png is not null)
        {
            result.AddImage(outcome.Png);
        }

        return result;
    }

    private static Outcome FromBridge(JsonElement res, string path, bool includeImage, int pid, string title)
    {
        int Int(string n) => res.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        if (!File.Exists(path))
        {
            throw GatewayException.NotReady("The Bridge reported an exported view image but the file is missing: " + path);
        }

        var width = Int("width");
        var height = Int("height");
        var info = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["method"] = "revit_api",
            ["captured_window"] = new { pid, title },
            ["width"] = width,
            ["height"] = height,
            ["image_width"] = includeImage ? width : null,
            ["image_height"] = includeImage ? height : null,
            ["scale"] = includeImage ? 1.0 : null,
            ["view"] = new
            {
                view_id = res.TryGetProperty("view_id", out var vid) ? vid.Clone() : (JsonElement?)null,
                view_name = res.TryGetProperty("view_name", out var vn) ? vn.GetString() : null,
                document = res.TryGetProperty("document", out var d) ? d.GetString() : null,
            },
            ["note"] = "PrintWindow returned black for the Revit canvas; this is the active model view rendered by Revit, not the UI (ribbon, palettes, dialogs). Pixels do not map to ui_click x/y.",
        };
        return new Outcome(info, includeImage ? File.ReadAllBytes(path) : null, 0, null);
    }

    private Task<Outcome> CaptureAsync(string session, WindowSpec spec, string? reference, string? selector, int maxWidth, bool includeImage, string path, bool allowBridge, string? bridgeError, CancellationToken cancellationToken)
    {
        return Ui.RunAsync(session, TimeSpan.FromSeconds(30), input: false, cancellationToken, op =>
        {
            var t = Ui.ResolveTarget(op, spec, reference, selector, allowWindow: true);
            CheckTarget(t.Window);
            var hwnd = t.Window.Handle;
            var captured = new { pid = t.Window.ProcessId, title = t.Window.Title };
            var minimized = Native.NativeMethods.IsIconic(hwnd);

            // 1. PrintWindow (never copies the screen). A minimized window is not printed: its content would be stale or empty.
            Bitmap? full = minimized ? null : WindowCapture.CaptureWindow(hwnd);
            var method = ScreenshotMethod.PrintWindow;
            if (full is null)
            {
                if (!minimized && !Native.NativeMethods.IsWindow(hwnd))
                {
                    throw GatewayException.NotFound("The window has no size or is gone.");
                }

                var revitMain = t.IsWindow
                    && string.Equals(t.Window.ProcessName, "Revit", StringComparison.OrdinalIgnoreCase)
                    && op.Service.IsMainWindow(t.Window);
                var bridgeLoaded = allowBridge && revitMain && _bridge!.IsPipePresent(t.Window.ProcessId);
                var occluded = !minimized && !bridgeLoaded && ScreenGuard.IsOccluded(hwnd);
                var decision = ScreenshotPlan.Decide(new ScreenshotFacts(true, revitMain, bridgeLoaded, ScreenGuard.Session(), minimized, occluded));
                if (decision.IsFailure)
                {
                    throw new GatewayException(
                        GatewayErrorCodes.TargetNotReady,
                        decision.FailureMessage!,
                        new { reason = decision.FailureCode, window = captured, bridge_error = bridgeError, hint = ScreenshotPlan.Hint });
                }

                if (decision.Method == ScreenshotMethod.RevitApi)
                {
                    return new Outcome(null, null, t.Window.ProcessId, t.Window.Title);
                }

                // 3. Screen pixels of exactly this window rectangle, verified again right after the copy.
                method = ScreenshotMethod.Screen;
                full = ScreenGuard.CopyRect(hwnd);
                if (full is not null && ScreenGuard.IsOccluded(hwnd))
                {
                    full.Dispose();
                    throw new GatewayException(
                        GatewayErrorCodes.TargetNotReady,
                        "Another window covered the target while it was captured, so the copy was discarded. " + ScreenshotPlan.Hint,
                        new { reason = ScreenshotPlan.Covered, window = captured, bridge_error = bridgeError, hint = ScreenshotPlan.Hint });
                }

                if (full is null)
                {
                    throw GatewayException.NotFound("The window has no size or is gone.");
                }
            }

            using var fullImage = full;
            Bitmap? cropped = null;
            object? crop = null;
            if (!t.IsWindow)
            {
                var b = op.Core.ReadLive(t.Element).Bounds
                    ?? throw GatewayException.NotReady($"{t.Node.Describe()} has no on-screen rectangle (offscreen, hidden or virtualized).");
                var area = new Rectangle(b.X - t.Window.Left, b.Y - t.Window.Top, b.Width, b.Height);
                cropped = WindowCapture.Crop(fullImage, area) ?? throw GatewayException.NotReady($"{t.Node.Describe()} is outside the window area.");
                crop = new { x = area.X, y = area.Y, width = area.Width, height = area.Height };
            }

            try
            {
                var shot = cropped ?? fullImage;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, WindowCapture.EncodePng(shot));
                byte[]? png = null;
                double scale = 1;
                int iw = shot.Width, ih = shot.Height;
                if (includeImage)
                {
                    using var scaled = WindowCapture.ScaleToMaxWidth(shot, maxWidth, out scale);
                    png = WindowCapture.EncodePng(scaled);
                    iw = scaled.Width;
                    ih = scaled.Height;
                }

                var d = new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["method"] = method == ScreenshotMethod.Screen ? "screen" : "print_window",
                    ["captured_window"] = captured,
                    ["width"] = shot.Width,
                    ["height"] = shot.Height,
                    ["image_width"] = includeImage ? iw : null,
                    ["image_height"] = includeImage ? ih : null,
                    ["scale"] = includeImage ? Math.Round(scale, 4) : null,
                    ["crop"] = crop,
                    ["window"] = WindowInfo(op, t.Window),
                    ["target"] = t.IsWindow ? null : Ui.TargetInfo(op, t),
                    ["bridge_error"] = bridgeError,
                    ["note"] = t.IsWindow ? "x/y for ui_click = image pixel / scale (window-relative)" : null,
                };
                return new Outcome(d, png, 0, null);
            }
            finally
            {
                cropped?.Dispose();
            }
        });
    }

    private string ResolveSavePath(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Path.Combine(Ui.Paths.CapturesDir, $"ui_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        }

        var full = UiPaths.Evidence(Ui.Paths, requested, Ui.Paths.CapturesDir);
        if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            full += ".png";
        }

        return full;
    }
}

/// <summary>ui_wait — wait for a window or element state.</summary>
public sealed class UiWaitTool : UiToolBase
{
    public UiWaitTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_wait";

    public override TimeSpan Timeout => TimeSpan.FromSeconds(330);

    public override string Description =>
        "Wait until a window appears / is gone (no ref/selector: e.g. pid + window='Sheet Configure'), or until an element exists / is gone / is enabled (ref or selector). " +
        "Polls every 250 ms up to timeout_s (default 10, max 300). Returns how long it waited; TIMEOUT error (with the last observed state) when the condition never became true. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()), "the element to wait for (omit to wait for the window)")
        .String("state", "appears (default) | gone | enabled.", enumValues: new[] { "appears", "gone", "enabled" }, defaultValue: "appears")
        .Number("timeout_s", "Maximum wait in seconds (default 10, max 300).", defaultValue: 10);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var state = (call.GetString("state") ?? "appears").ToLowerInvariant();
        if (state is "exists" or "visible" or "appear")
        {
            state = "appears";
        }

        if (state is not ("appears" or "gone" or "enabled"))
        {
            throw GatewayException.Validation("state must be appears, gone or enabled.");
        }

        bool element = !string.IsNullOrWhiteSpace(reference) || !string.IsNullOrWhiteSpace(selector);
        if (!element && state == "enabled")
        {
            throw GatewayException.Validation("state=enabled needs a ref or selector.");
        }

        if (!element && spec.IsEmpty)
        {
            throw GatewayException.Validation("Pass pid/window/hwnd (window wait) or a ref/selector (element wait).");
        }

        var timeout = Seconds(call, "timeout_s", 10, 300);
        CheckDesktop();
        var result = await Ui.RunAsync(call.SessionId, timeout + TimeSpan.FromSeconds(20), input: false, cancellationToken, op =>
        {
            var sw = Stopwatch.StartNew();
            string last = "not checked";
            while (true)
            {
                op.Token.ThrowIfCancellationRequested();
                bool ok;
                object? target = null;
                object? window = null;
                try
                {
                    if (element)
                    {
                        var t = Ui.ResolveTarget(op, spec, reference, selector, allowWindow: false, TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 2, 10)));
                        last = t.Node.Enabled ? "present" : "present (disabled)";
                        ok = state switch { "gone" => false, "enabled" => t.Node.Enabled, _ => true };
                        if (ok)
                        {
                            target = Ui.TargetInfo(op, t);
                        }
                    }
                    else
                    {
                        var w = Ui.ResolveWindow(op, spec);
                        last = $"window {w.HwndHex} \"{UiText.Clip(w.Title, 60)}\" present";
                        ok = state != "gone";
                        if (ok)
                        {
                            target = WindowTarget(op, w);
                            window = WindowInfo(op, w);
                        }
                    }
                }
                catch (GatewayException ex) when (ex.Code is GatewayErrorCodes.NotFound or GatewayErrorCodes.TargetNotReady)
                {
                    if (ex.Code == GatewayErrorCodes.NotFound && !string.IsNullOrWhiteSpace(reference) && state != "gone")
                    {
                        throw;
                    }

                    last = ex.Message;
                    ok = state == "gone";
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    // The element vanished between search and read: same as not found.
                    last = UiaCore.Translate(ex)?.Message ?? ex.Message;
                    ok = state == "gone";
                }

                if (ok)
                {
                    return new Dictionary<string, object?>
                    {
                        ["state"] = state,
                        ["satisfied"] = true,
                        ["waited_ms"] = sw.ElapsedMilliseconds,
                        ["target"] = target ?? FallbackTarget(op, spec, selector, reference),
                        ["window"] = window,
                    };
                }

                if (sw.Elapsed >= timeout)
                {
                    throw new GatewayException(
                        GatewayErrorCodes.Timeout,
                        $"Waited {timeout.TotalSeconds:0.#}s but the {(element ? "element" : "window")} did not become '{state}'. Last state: {last}",
                        new { state, waited_ms = sw.ElapsedMilliseconds, last });
                }

                op.Sleep(250);
            }
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_expect — retrying assertion that returns pass/fail as data.</summary>
public sealed class UiExpectTool : UiToolBase
{
    public UiExpectTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_expect";

    public override TimeSpan Timeout => TimeSpan.FromSeconds(150);

    public override string Description =>
        "Retrying UI assertion: checks an element (ref/selector) or the window (none) until it passes or timeout_s (default 5) elapses, and returns {pass, message, actual} — a failed check is data, not an error. " +
        "Kinds: is = exists | gone | enabled | disabled | visible | hidden | focused | checked | unchecked | mixed | selected | expanded | collapsed; text (contains, or exact=true equals); match (regex on the text); " +
        "count (number of selector matches). Text = Value, else TextPattern, else all text of a container, else Name. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()), "the element to check (omit to check the window)")
        .String("is", "State to expect.", enumValues: new[] { "exists", "gone", "enabled", "disabled", "visible", "hidden", "focused", "checked", "unchecked", "mixed", "selected", "expanded", "collapsed" })
        .String("text", "Expected text (contains, case-insensitive, whitespace-normalized; exact=true for equality).")
        .Boolean("exact", "With text: require equality instead of contains.", defaultValue: false)
        .String("match", "Regular expression the element text must match.")
        .Integer("count", "Expected number of elements matching 'selector'.", minimum: 0)
        .Number("timeout_s", "Retry for up to this many seconds (default 5, max 120).", defaultValue: 5);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var e = new UiActions.Expectation(call.GetString("is"), call.GetString("text"), call.GetBool("exact"), call.GetString("match"), call.GetInt("count"));
        if (e.Match is not null)
        {
            UiText.CompileRegex(e.Match, "match");
        }

        var timeout = Seconds(call, "timeout_s", 5, 120);
        CheckDesktop();
        var result = await Ui.RunAsync(call.SessionId, timeout + TimeSpan.FromSeconds(25), input: false, cancellationToken, op =>
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var o = UiActions.Evaluate(op, spec, reference, selector, e);
                if (o.Pass || sw.Elapsed >= timeout)
                {
                    return new Dictionary<string, object?>
                    {
                        ["pass"] = o.Pass,
                        ["message"] = (o.Pass ? "PASS: " : "FAIL: ") + o.Message,
                        ["actual"] = o.Actual,
                        ["expected"] = new { @is = e.Is, text = e.Text, exact = e.Exact ? true : (bool?)null, match = e.Match, count = e.Count },
                        ["waited_ms"] = sw.ElapsedMilliseconds,
                        ["target"] = o.Target is { } t
                            ? (t.IsWindow ? WindowTarget(op, t.Window) : Ui.TargetInfo(op, t))
                            : FallbackTarget(op, spec, selector, reference, o.Window),
                    };
                }

                op.Sleep(250);
            }
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}
