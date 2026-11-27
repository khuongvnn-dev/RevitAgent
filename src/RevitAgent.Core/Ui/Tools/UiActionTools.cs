using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui.Tools;

/// <summary>Base of tools that change UI state: auto-wait argument and the global input lock.</summary>
public abstract class UiActionToolBase : UiToolBase
{
    protected UiActionToolBase(UiAutomationService ui)
        : base(ui)
    {
    }

    public override ToolPermission Permission => ToolPermission.Interact;

    protected static Schema WithWait(Schema s) =>
        s.Number("timeout_s", "Seconds to wait for the target (max 60).", defaultValue: 5);

    protected static TimeSpan Wait(ToolCall call) => Seconds(call, "timeout_s", 5, 60);

    protected Task<T> RunActionAsync<T>(ToolCall call, CancellationToken ct, Func<UiOp, T> work)
    {
        CheckDesktop();
        var budget = Wait(call) + TimeSpan.FromSeconds(30);
        return Ui.RunAsync(call.SessionId, budget, input: true, ct, work);
    }
}

/// <summary>ui_click.</summary>
public sealed class UiClickTool : UiActionToolBase
{
    public UiClickTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_click";

    public override string Description =>
        "Click an element (ref or selector): UI Automation patterns first (no focus needed), else a real mouse click. " +
        "right/middle, double or mouse=true force the mouse. x/y without ref/selector: click at window pixels (Revit canvas, unscaled ui_screenshot). " +
        "blocked:true = a modal dialog probably opened.";

    protected override Schema BuildSchema() => WithWait(ElementArgs(Targeting(Schema.Object())))
        .String("button", "Mouse button.", enumValues: new[] { "left", "right", "middle" }, defaultValue: "left")
        .Boolean("double", "Double-click.", defaultValue: false)
        .Boolean("mouse", "Real mouse click.", defaultValue: false)
        .Integer("x", "Window pixel x (no ref/selector).", minimum: 0)
        .Integer("y", "Window pixel y.", minimum: 0);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var button = (call.GetString("button") ?? "left").ToLowerInvariant();
        if (button is not ("left" or "right" or "middle"))
        {
            throw GatewayException.Validation("button must be left, right or middle.");
        }

        var dbl = call.GetBool("double");
        var mouse = call.GetBool("mouse");
        var x = call.GetInt("x");
        var y = call.GetInt("y");
        bool point = x is not null || y is not null;
        if (point && (x is null || y is null))
        {
            throw GatewayException.Validation("Pass both x and y.");
        }

        if (point && (reference is not null || selector is not null))
        {
            throw GatewayException.Validation("x/y click the window at a point; do not combine them with ref/selector.");
        }

        var wait = Wait(call);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            if (point)
            {
                var w = Ui.ResolveWindow(op, spec);
                CheckTarget(w);
                var o = UiActions.ClickAt(op, w, x!.Value, y!.Value, button, dbl);
                o["target"] = new { selector = (string?)null, window = w.Title, hwnd = w.HwndHex, pid = w.ProcessId, is_main_window = Ui.IsMainWindow(w), x, y };
                return o;
            }

            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: !mouse && !dbl && button == "left", wait);
            CheckTarget(t.Window);
            return ActionResult(op, t, UiActions.Click(op, t, button, dbl, mouse));
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_type.</summary>
public sealed class UiTypeTool : UiActionToolBase
{
    public UiTypeTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_type";

    public override string Description =>
        "Type into an edit field (ref or selector; none = the focused element). Default sets the value (no focus; replaces, append adds). " +
        "keyboard=true sends real keystrokes, for apps that need key events. Password fields are refused. Returns the value read back.";

    protected override Schema BuildSchema() => WithWait(ElementArgs(Targeting(Schema.Object()), "the edit field"))
        .String("text", "Text. With keyboard: \\n = Enter, \\t = Tab.")
        .Boolean("keyboard", "Real keystrokes.", defaultValue: false)
        .Boolean("clear", "Empty the field.", defaultValue: false)
        .Boolean("append", "Append.", defaultValue: false)
        .Boolean("verify", "Read back; fail after 3 tries.", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var clear = call.GetBool("clear");
        var text = clear ? string.Empty : call.GetString("text");
        if (text is null)
        {
            throw GatewayException.Validation("Pass 'text' (or clear=true).");
        }

        var keyboard = call.GetBool("keyboard");
        var append = !clear && call.GetBool("append");
        var verify = call.GetBool("verify");
        var wait = Wait(call);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            if (reference is null && selector is null)
            {
                var w = Ui.ResolveWindow(op, spec);
                CheckTarget(w);
                var o = UiActions.TypeIntoFocused(op, w, text);
                o["target"] = new { selector = (string?)null, window = w.Title, hwnd = w.HwndHex, pid = w.ProcessId, is_main_window = Ui.IsMainWindow(w) };
                return o;
            }

            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: true, wait);
            CheckTarget(t.Window);
            return ActionResult(op, t, UiActions.Type(op, t, text, keyboard, append, verify));
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_select.</summary>
public sealed class UiSelectTool : UiActionToolBase
{
    public UiSelectTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_select";

    public override string Description =>
        "Select an item: with 'item' (name, exact first then contains) or 'index' (0-based), picks it inside a combo box / list / tab control / tree (a combo is expanded and collapsed again); " +
        "without them, selects the target itself (a TabItem or ListItem; note: Revit 2024 ribbon tabs are Buttons with id=<tab name>, e.g. ui_click id=View). Uses SelectionItem/Invoke patterns, mouse only as fallback. " +
        "Returns the selected item, the combo value and the resolved selector.";

    protected override Schema BuildSchema() => WithWait(ElementArgs(Targeting(Schema.Object()), "the combo/list/tab control (or the item itself)"))
        .String("item", "Name of the item to select inside the target.")
        .Integer("index", "0-based index of the item inside the target.", minimum: 0);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var item = call.GetString("item");
        var index = call.GetInt("index");
        if (item is not null && index is not null)
        {
            throw GatewayException.Validation("Pass either item or index, not both.");
        }

        var wait = Wait(call);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: true, wait);
            CheckTarget(t.Window);
            return ActionResult(op, t, UiActions.Select(op, t, item, index));
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_toggle.</summary>
public sealed class UiToggleTool : UiActionToolBase
{
    public UiToggleTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_toggle";

    public override string Description =>
        "Toggle a check box / toggle button (Toggle pattern). state=on|off sets it idempotently (no change when already there); omitted = flip once. Returns before/after state and the resolved selector.";

    protected override Schema BuildSchema() => WithWait(ElementArgs(Targeting(Schema.Object())))
        .String("state", "on | off | toggle (default).", enumValues: new[] { "on", "off", "toggle" });

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var state = call.GetString("state");
        var wait = Wait(call);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: true, wait);
            CheckTarget(t.Window);
            return ActionResult(op, t, UiActions.Toggle(op, t, state));
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_expand.</summary>
public sealed class UiExpandTool : UiActionToolBase
{
    public UiExpandTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_expand";

    public override string Description =>
        "Expand (or collapse=true) a tree item, combo box, expander, menu or collapsed Revit ribbon panel / split button (ExpandCollapse pattern). Returns the new state and the resolved selector.";

    protected override Schema BuildSchema() => WithWait(ElementArgs(Targeting(Schema.Object())))
        .Boolean("collapse", "Collapse instead of expand.", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var collapse = call.GetBool("collapse");
        var wait = Wait(call);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: true, wait);
            CheckTarget(t.Window);
            return ActionResult(op, t, UiActions.Expand(op, t, collapse));
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_keys.</summary>
public sealed class UiKeysTool : UiActionToolBase
{
    public UiKeysTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_keys";

    public override string Description =>
        "Send a key sequence (SendKeys syntax) to a window after bringing it to the foreground: {TAB} {ENTER} {ESC} {F2} {DOWN 3}, ^a = Ctrl+A, +{TAB} = Shift+Tab, %f = Alt+F, ~ = Enter. " +
        "Optional ref/selector focuses that element first. Keys are only sent while the target window is in the foreground; if focus is stolen the rest is not sent (TARGET_NOT_READY). " +
        "Alt+F4 on the application's main window is refused (use revit_close). Revit keyboard shortcuts work on the main window (e.g. {ESC}{ESC} to cancel a command).";

    protected override Schema BuildSchema() => ElementArgs(Targeting(Schema.Object()), "an element to focus first (optional)")
        .String("keys", "Key sequence, e.g. \"^a{DELETE}\", \"{TAB}{ENTER}\", \"%{F4}\".", required: true);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var keys = call.RequireString("keys");
        var strokes = InputSender.ParseSendKeys(keys);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            UiTarget? t = null;
            TopLevelWindow w;
            if (reference is not null || selector is not null)
            {
                t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: true, UiAutomationService.DefaultActionWait);
                w = t.Window;
            }
            else
            {
                w = Ui.ResolveWindow(op, spec);
            }

            CheckTarget(w);
            UiSafety.EnsureActionAllowed(op, w);
            UiActions.EnsureAltF4Safe(op, strokes, w);
            InputSender.EnsureForeground(w.Handle, w.ProcessId, "keys", op.Token);
            UiActions.EnsureForegroundIsTarget(op, w, "keys");
            if (t is not null)
            {
                UiActions.FocusElement(op, t.Wrap(op.Core));
                UiActions.EnsureFocusWithin(op, t);
            }

            UiActions.EnsureAltF4Safe(op, strokes, w, afterActivation: true);
            InputSender.SendStrokes(strokes, w.ProcessId, op.Token, requireRoot: w.Handle);
            var r = new Dictionary<string, object?>
            {
                ["action"] = "keys",
                ["keys"] = keys,
                ["strokes"] = strokes.Count,
                ["foreground"] = TopLevelWindows.Describe(Native.NativeMethods.GetForegroundWindow())?.Title,
            };
            if (t is not null)
            {
                return ActionResult(op, t, r);
            }

            r["target"] = new { selector = (string?)null, window = w.Title, hwnd = w.HwndHex, pid = w.ProcessId, is_main_window = Ui.IsMainWindow(w) };
            return r;
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}

/// <summary>ui_scroll.</summary>
public sealed class UiScrollTool : UiActionToolBase
{
    public UiScrollTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_scroll";

    public override string Description =>
        "Scroll a list / grid / pane (the target or its nearest scrollable ancestor; Scroll pattern, mouse wheel as fallback): direction down|up|pagedown|pageup|left|right with amount steps, " +
        "or direction=into to scroll the target item into view (ScrollItem pattern). Returns the new scroll percentages and the resolved selector.";

    protected override Schema BuildSchema() => WithWait(ElementArgs(Targeting(Schema.Object())))
        .String("direction", "down (default) | up | pagedown | pageup | left | right | into.", enumValues: new[] { "down", "up", "pagedown", "pageup", "left", "right", "into" }, defaultValue: "down")
        .Integer("amount", "Number of steps (default 1).", defaultValue: 1, minimum: 1, maximum: 100);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var spec = Spec(call);
        var reference = Ref(call);
        var selector = SelectorArg(call);
        var direction = call.GetString("direction") ?? "down";
        var amount = Math.Clamp(call.GetInt("amount", 1) ?? 1, 1, 100);
        var wait = Wait(call);
        var result = await RunActionAsync(call, cancellationToken, op =>
        {
            var t = Ui.ResolveActionable(op, spec, reference, selector, needEnabled: false, wait);
            CheckTarget(t.Window);
            return ActionResult(op, t, UiActions.Scroll(op, t, direction, amount));
        }).ConfigureAwait(false);
        return ToolResult.Json(result);
    }
}
