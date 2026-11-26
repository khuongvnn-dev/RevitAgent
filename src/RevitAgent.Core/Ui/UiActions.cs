using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Interop.UIAutomationClient;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Native;
using ExpandCollapseState = FlaUI.Core.Definitions.ExpandCollapseState;
using ScrollAmount = FlaUI.Core.Definitions.ScrollAmount;
using ToggleState = FlaUI.Core.Definitions.ToggleState;

namespace RevitAgent.Core.Ui;

/// <summary>
/// UI actions on resolved targets. Pattern actions (Invoke, Toggle, SelectionItem, ExpandCollapse, Value) come first:
/// they need no focus and cannot hit another window. Real mouse/keyboard input is the fallback and is only sent after
/// the target window is in the foreground (see <see cref="InputSender"/>).
/// </summary>
public static class UiActions
{
    private static readonly string[] SafeDefaultActions = { "press", "click", "execute", "jump", "push", "open", "check", "uncheck", "select", "switch" };

    // ---------------------------------------------------------------- click

    public static Dictionary<string, object?> Click(UiOp op, UiTarget t, string button, bool doubleClick, bool forceMouse)
    {
        UiSafety.EnsureActionAllowed(op, t.Window);
        var r = new Dictionary<string, object?>();
        var d = t.Node.Describe();
        bool mouse = forceMouse || doubleClick || !string.Equals(button, "left", StringComparison.OrdinalIgnoreCase);
        if (!mouse)
        {
            if (!t.Node.Enabled)
            {
                throw GatewayException.NotReady($"{d} is disabled.");
            }

            TryActivate(t.Window, op.Token);
            var el = t.Wrap(op.Core);
            if (t.Node.CanInvoke && el.Patterns.Invoke.TryGetPattern(out var inv))
            {
                r["action"] = "invoked";
                if (!Bounded(op, () => inv.Invoke(), 3000))
                {
                    r["blocked"] = true;
                    op.Notes.Add("the Invoke call is still blocked after 3 s — a modal dialog probably opened; call revit_list_dialogs to find it");
                }

                return r;
            }

            if (t.Node.CanToggle && el.Patterns.Toggle.TryGetPattern(out var tg))
            {
                if (!Bounded(op, () => tg.Toggle(), 3000))
                {
                    r["blocked"] = true;
                    op.Notes.Add("the Toggle call is still blocked after 3 s — a dialog may have opened");
                }

                r["action"] = "toggled";
                r["state"] = ToggleName(SafeRead(() => tg.ToggleState.Value, ToggleState.Indeterminate));
                return r;
            }

            if (t.Node.CanSelect && el.Patterns.SelectionItem.TryGetPattern(out var si))
            {
                Bounded(op, () => si.Select(), 3000);
                r["action"] = "selected";
                return r;
            }

            if (t.Node.CanExpand && el.Patterns.ExpandCollapse.TryGetPattern(out var ec))
            {
                var state = SafeRead(() => ec.ExpandCollapseState.Value, ExpandCollapseState.LeafNode);
                if (state == ExpandCollapseState.Collapsed)
                {
                    Bounded(op, () => ec.Expand(), 3000);
                    r["action"] = "expanded";
                }
                else
                {
                    Bounded(op, () => ec.Collapse(), 3000);
                    r["action"] = "collapsed";
                }

                return r;
            }

            if (el.Patterns.LegacyIAccessible.TryGetPattern(out var la))
            {
                var def = SafeRead(() => la.DefaultAction.Value, string.Empty) ?? string.Empty;
                if (def.Length > 0 && SafeDefaultActions.Any(a => def.Contains(a, StringComparison.OrdinalIgnoreCase)) &&
                    !def.Contains("double", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Bounded(op, () => la.DoDefaultAction(), 3000))
                    {
                        r["blocked"] = true;
                        op.Notes.Add("the default action is still blocked after 3 s — a modal dialog probably opened");
                    }

                    r["action"] = "default_action";
                    r["default_action"] = def;
                    return r;
                }
            }

            op.Notes.Add("no invoke/toggle/select/expand pattern; used a real mouse click");
        }

        var count = doubleClick ? 2 : 1;
        var p = MouseOnElement(op, t, button, count);
        r["action"] = "mouse";
        r["button"] = button.ToLowerInvariant();
        r["clicks"] = count;
        r["point"] = new { x = p.X - t.Window.Left, y = p.Y - t.Window.Top, screen_x = p.X, screen_y = p.Y };
        return r;
    }

    /// <summary>Mouse click at window-relative pixels (drawing canvas, owner-drawn controls without a UIA tree).</summary>
    public static Dictionary<string, object?> ClickAt(UiOp op, TopLevelWindow w, int x, int y, string button, bool doubleClick)
    {
        UiSafety.EnsureActionAllowed(op, w);
        if (x < 0 || y < 0 || x >= w.Width || y >= w.Height)
        {
            throw GatewayException.Validation($"Point {x},{y} is outside the window ({w.Width}x{w.Height}). x/y are window-relative pixels (same as an unscaled ui_screenshot of the window).");
        }

        int sx = w.Left + x, sy = w.Top + y;
        InputSender.EnsureForeground(w.Handle, w.ProcessId, "a mouse click", op.Token);
        InputSender.Click(sx, sy, button, doubleClick ? 2 : 1, w.ProcessId, op.Token);
        string? hit = null;
        try
        {
            var e = op.Core.Native.ElementFromPoint(new tagPOINT { x = sx, y = sy });
            if (e is not null)
            {
                hit = op.Core.ReadLive(e).Describe();
            }
        }
        catch (COMException)
        {
        }

        return new Dictionary<string, object?>
        {
            ["action"] = "mouse",
            ["button"] = button.ToLowerInvariant(),
            ["clicks"] = doubleClick ? 2 : 1,
            ["point"] = new { x, y, screen_x = sx, screen_y = sy },
            ["hit"] = hit,
        };
    }

    public static (int X, int Y) MouseOnElement(UiOp op, UiTarget t, string button, int count)
    {
        InputSender.EnsureForeground(t.Window.Handle, t.Window.ProcessId, "a mouse click", op.Token);
        var p = ClickPoint(op, t);
        InputSender.Click(p.X, p.Y, button, count, t.Window.ProcessId, op.Token);
        return p;
    }

    public static (int X, int Y) ClickPoint(UiOp op, UiTarget t)
    {
        var el = t.Wrap(op.Core);
        if (t.Node.Offscreen && el.Patterns.ScrollItem.TryGetPattern(out var sip))
        {
            try
            {
                sip.ScrollIntoView();
                op.Sleep(150);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }

        try
        {
            if (el.TryGetClickablePoint(out var pt) && (pt.X != 0 || pt.Y != 0))
            {
                return (pt.X, pt.Y);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        var live = op.Core.ReadLive(t.Element);
        if (live.Bounds is not { } b || b.Width <= 0 || b.Height <= 0)
        {
            throw GatewayException.NotReady($"{t.Node.Describe()} has no on-screen rectangle (offscreen, hidden or virtualized). Scroll it into view or expand its container first.");
        }

        return (b.CenterX, b.CenterY);
    }

    // ---------------------------------------------------------------- type

    public static Dictionary<string, object?> Type(UiOp op, UiTarget t, string text, bool keyboard, bool append, bool verify)
    {
        UiSafety.EnsureActionAllowed(op, t.Window);
        EnsureNotPassword(t.Node);
        var r = new Dictionary<string, object?>();
        var el = t.Wrap(op.Core);
        var d = t.Node.Describe();
        if (!t.Node.Enabled)
        {
            throw GatewayException.NotReady($"{d} is disabled.");
        }

        if (!keyboard && t.Node.HasValue && !t.Node.ReadOnly && el.Patterns.Value.TryGetPattern(out var vp))
        {
            var want = append ? (SafeRead(() => vp.Value.Value, string.Empty) ?? string.Empty) + text : text;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                vp.SetValue(want);
                op.Sleep(50);
                var got = SafeRead(() => vp.Value.Value, null);
                r["action"] = "set_value";
                r["value"] = got;
                if (!verify || ValuesEqual(got, want))
                {
                    if (verify)
                    {
                        r["verified"] = true;
                    }

                    return r;
                }
            }

            throw GatewayException.NotReady($"Set the value of {d} but it reads \"{UiText.Clip(r["value"] as string, 80)}\" (wanted \"{UiText.Clip(want, 80)}\"). The field may reformat or reject input; try keyboard=true.");
        }

        if (!keyboard && t.Node.HasRange && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            el.Patterns.RangeValue.TryGetPattern(out var rv) && !SafeRead(() => rv.IsReadOnly.Value, true))
        {
            rv.SetValue(num);
            r["action"] = "set_range";
            r["value"] = SafeRead(() => rv.Value.Value, double.NaN);
            return r;
        }

        // Real keystrokes. Empty text clears (Ctrl+A, Delete). verify reads back and retries: keystrokes land wherever
        // focus is, and a focus steal silently drops them.
        int tries = verify ? 3 : 1;
        string? value = null;
        bool ok = false;
        for (int attempt = 1; attempt <= tries; attempt++)
        {
            InputSender.EnsureForeground(t.Window.Handle, t.Window.ProcessId, "typing", op.Token);
            FocusElement(op, el);
            EnsureFocusWithin(op, t);
            InputSender.SendStrokes(InputSender.ParseSendKeys(append ? "^{END}" : "^a"), t.Window.ProcessId, op.Token);
            if (text.Length == 0 && !append)
            {
                InputSender.SendStrokes(InputSender.ParseSendKeys("{DELETE}"), t.Window.ProcessId, op.Token);
            }
            else
            {
                InputSender.TypeText(text, t.Window.ProcessId, op.Token);
            }

            op.Sleep(120);
            value = el.Patterns.Value.TryGetPattern(out var vp2) ? SafeRead(() => vp2.Value.Value, null) : null;
            if (!verify)
            {
                ok = true;
                break;
            }

            ok = append ? value is not null && value.EndsWith(text, StringComparison.Ordinal) : ValuesEqual(value, text);
            if (ok)
            {
                break;
            }

            op.Sleep(300);
        }

        if (verify && !ok)
        {
            throw GatewayException.NotReady($"Typed into {d} but the value reads \"{UiText.Clip(value, 80)}\" after {tries} tries — focus may be elsewhere; take a snapshot and retry.");
        }

        r["action"] = text.Length == 0 && !append ? "cleared" : "keyboard";
        r["chars"] = text.Length;
        r["value"] = value;
        if (verify)
        {
            r["verified"] = true;
        }

        return r;
    }

    /// <summary>Keyboard typing into whatever has focus in the window (no target).</summary>
    public static Dictionary<string, object?> TypeIntoFocused(UiOp op, TopLevelWindow w, string text)
    {
        UiSafety.EnsureActionAllowed(op, w);
        InputSender.EnsureForeground(w.Handle, w.ProcessId, "typing", op.Token);
        try
        {
            var focused = op.Core.Native.GetFocusedElement();
            if (focused is not null)
            {
                EnsureNotPassword(op.Core.ReadLive(focused));
            }
        }
        catch (COMException)
        {
            throw GatewayException.NotReady("Could not read the focused element (to make sure it is not a password field); nothing was typed. Pass a ref/selector of the field.");
        }

        InputSender.TypeText(text, w.ProcessId, op.Token);
        return new Dictionary<string, object?> { ["action"] = "keyboard", ["chars"] = text.Length, ["into"] = "focused element" };
    }

    public static void EnsureNotPassword(UiNode n)
    {
        if (n.IsPassword)
        {
            throw GatewayException.Denied($"Refusing to type into password field {n.Describe()}: secrets must never be typed by the agent. Ask the user to enter it.");
        }
    }

    public static void FocusElement(UiOp op, AutomationElement el)
    {
        try
        {
            el.Focus();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException || ex.GetType().Name.Contains("NotSupported", StringComparison.Ordinal))
        {
        }

        op.Sleep(60);
    }

    // ---------------------------------------------------------------- select / toggle / expand / scroll

    public static Dictionary<string, object?> Select(UiOp op, UiTarget t, string? item, int? index)
    {
        UiSafety.EnsureActionAllowed(op, t.Window);
        var el = t.Wrap(op.Core);
        var d = t.Node.Describe();
        var r = new Dictionary<string, object?>();
        TryActivate(t.Window, op.Token);
        if (item is null && index is null)
        {
            if (!el.Patterns.SelectionItem.TryGetPattern(out var own))
            {
                throw GatewayException.Validation($"{d} is not selectable; pass 'item' (name) or 'index' to choose an item inside it.");
            }

            Bounded(op, () => own.Select(), 3000);
            r["action"] = "selected";
            r["item"] = d;
            return r;
        }

        bool expandedByUs = false;
        if (el.Patterns.ExpandCollapse.TryGetPattern(out var ec) &&
            SafeRead(() => ec.ExpandCollapseState.Value, ExpandCollapseState.LeafNode) == ExpandCollapseState.Collapsed)
        {
            try
            {
                ec.Expand();
                expandedByUs = true;
                op.Sleep(300);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }

        IUIAutomationElement? hit;
        if (item is not null)
        {
            hit = ElementSearch.Find(op.Core, t.Element, Selector.ForName(item, false), 1, TimeSpan.FromSeconds(5), op.Token, includeOffscreen: true).Matches.FirstOrDefault()
                ?? ElementSearch.Find(op.Core, t.Element, Selector.ForName(item, true), 1, TimeSpan.FromSeconds(5), op.Token, includeOffscreen: true).Matches.FirstOrDefault();
            if (hit is null)
            {
                CollapseIf(ec, expandedByUs);
                var names = ItemsOf(op, t.Element).Take(30).Select(n => n.Name).Where(n => n.Length > 0).ToArray();
                throw GatewayException.NotFound($"No item named '{item}' inside {d}.", new { available = names });
            }
        }
        else
        {
            var items = ItemsOf(op, t.Element);
            if (index!.Value < 0 || index.Value >= items.Count)
            {
                CollapseIf(ec, expandedByUs);
                throw GatewayException.NotFound($"{d} has {items.Count} items; index {index} is out of range (0-based).");
            }

            hit = items[index.Value].Element;
        }

        var itemNode = op.Core.ReadLive(hit);
        var itemEl = op.Core.Wrap(hit);
        if (itemEl.Patterns.ScrollItem.TryGetPattern(out var sci))
        {
            try
            {
                sci.ScrollIntoView();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }

        if (itemEl.Patterns.SelectionItem.TryGetPattern(out var sel))
        {
            Bounded(op, () => sel.Select(), 3000);
            r["action"] = "selected";
        }
        else if (itemEl.Patterns.Invoke.TryGetPattern(out var inv))
        {
            Bounded(op, () => inv.Invoke(), 3000);
            r["action"] = "invoked";
        }
        else
        {
            MouseOnElement(op, new UiTarget { Window = t.Window, Element = hit, Node = itemNode }, "left", 1);
            r["action"] = "mouse";
        }

        op.Sleep(100);
        if (ec is not null)
        {
            try
            {
                if (ec.ExpandCollapseState.Value == ExpandCollapseState.Expanded)
                {
                    ec.Collapse();
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }

        r["item"] = itemNode.Describe();
        r["value"] = CurrentValue(op, el);
        return r;
    }

    /// <summary>Value of a combo/list: Value pattern, else the names of the current selection (non-editable WPF combo).</summary>
    public static string? CurrentValue(UiOp op, AutomationElement el)
    {
        if (SafeRead(() => el.Properties.IsPassword.ValueOrDefault, false))
        {
            return null;
        }

        if (el.Patterns.Value.TryGetPattern(out var vp))
        {
            var v = SafeRead(() => vp.Value.Value, null);
            if (!string.IsNullOrEmpty(v))
            {
                return v;
            }
        }

        if (el.Patterns.Selection.TryGetPattern(out var sp))
        {
            var names = SafeRead(() => sp.Selection.Value.Select(s => s.Properties.Name.ValueOrDefault ?? string.Empty).ToArray(), Array.Empty<string>());
            if (names.Length > 0)
            {
                return string.Join(", ", names);
            }
        }

        return null;
    }

    public static Dictionary<string, object?> Toggle(UiOp op, UiTarget t, string? state)
    {
        UiSafety.EnsureActionAllowed(op, t.Window);
        var el = t.Wrap(op.Core);
        if (!el.Patterns.Toggle.TryGetPattern(out var tg))
        {
            throw GatewayException.Validation($"{t.Node.Describe()} does not support toggle (use ui_click).");
        }

        if (!t.Node.Enabled)
        {
            throw GatewayException.NotReady($"{t.Node.Describe()} is disabled.");
        }

        TryActivate(t.Window, op.Token);
        var before = SafeRead(() => tg.ToggleState.Value, ToggleState.Indeterminate);
        if (state is null || state.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            Bounded(op, () => tg.Toggle(), 3000);
        }
        else
        {
            bool wantOn = state.ToLowerInvariant() switch
            {
                "on" or "true" or "1" or "checked" => true,
                "off" or "false" or "0" or "unchecked" => false,
                _ => throw GatewayException.Validation($"state must be on, off or toggle (got '{state}')."),
            };
            for (int n = 0; n < 3 && (SafeRead(() => tg.ToggleState.Value, ToggleState.Indeterminate) == ToggleState.On) != wantOn; n++)
            {
                Bounded(op, () => tg.Toggle(), 3000);
                op.Sleep(50);
            }
        }

        var after = SafeRead(() => tg.ToggleState.Value, ToggleState.Indeterminate);
        return new Dictionary<string, object?> { ["action"] = "toggled", ["before"] = ToggleName(before), ["state"] = ToggleName(after) };
    }

    public static Dictionary<string, object?> Expand(UiOp op, UiTarget t, bool collapse)
    {
        UiSafety.EnsureActionAllowed(op, t.Window);
        var el = t.Wrap(op.Core);
        if (!el.Patterns.ExpandCollapse.TryGetPattern(out var ec))
        {
            throw GatewayException.Validation($"{t.Node.Describe()} cannot expand/collapse.");
        }

        TryActivate(t.Window, op.Token);
        if (collapse)
        {
            Bounded(op, () => ec.Collapse(), 3000);
        }
        else
        {
            Bounded(op, () => ec.Expand(), 3000);
        }

        op.Sleep(150);
        var st = SafeRead(() => ec.ExpandCollapseState.Value, ExpandCollapseState.LeafNode);
        return new Dictionary<string, object?> { ["action"] = collapse ? "collapsed" : "expanded", ["state"] = st.ToString().ToLowerInvariant() };
    }

    public static Dictionary<string, object?> Scroll(UiOp op, UiTarget t, string direction, int amount)
    {
        UiSafety.EnsureActionAllowed(op, t.Window);
        var dir = direction.ToLowerInvariant();
        var el = t.Wrap(op.Core);
        if (dir == "into")
        {
            if (!el.Patterns.ScrollItem.TryGetPattern(out var si))
            {
                throw GatewayException.Validation($"{t.Node.Describe()} does not support ScrollIntoView.");
            }

            si.ScrollIntoView();
            return new Dictionary<string, object?> { ["action"] = "scrolled_into_view" };
        }

        ScrollAmount h = ScrollAmount.NoAmount, v = ScrollAmount.NoAmount;
        switch (dir)
        {
            case "down": v = ScrollAmount.SmallIncrement; break;
            case "up": v = ScrollAmount.SmallDecrement; break;
            case "pagedown": v = ScrollAmount.LargeIncrement; break;
            case "pageup": v = ScrollAmount.LargeDecrement; break;
            case "right": h = ScrollAmount.SmallIncrement; break;
            case "left": h = ScrollAmount.SmallDecrement; break;
            default: throw GatewayException.Validation($"Unknown scroll direction '{direction}' (down, up, pagedown, pageup, left, right, into).");
        }

        var container = ScrollContainer(op, t.Element);
        if (container is not null && container.Patterns.Scroll.TryGetPattern(out var sp))
        {
            int done = 0;
            for (; done < amount; done++)
            {
                try
                {
                    sp.Scroll(h, v);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    break;
                }
            }

            return new Dictionary<string, object?>
            {
                ["action"] = "scrolled",
                ["direction"] = dir,
                ["steps"] = done,
                ["horizontal_percent"] = Math.Round(SafeRead(() => sp.HorizontalScrollPercent.Value, -1d), 1),
                ["vertical_percent"] = Math.Round(SafeRead(() => sp.VerticalScrollPercent.Value, -1d), 1),
            };
        }

        InputSender.EnsureForeground(t.Window.Handle, t.Window.ProcessId, "mouse-wheel scrolling", op.Token);
        var p = ClickPoint(op, t);
        // Vertical wheel: positive = up. Horizontal wheel: positive = right.
        bool horizontal = dir is "left" or "right";
        int delta = dir is "up" or "pageup" or "right" ? 120 : -120;
        int notches = dir.StartsWith("page", StringComparison.Ordinal) ? amount * 5 : amount;
        for (int n = 0; n < notches; n++)
        {
            InputSender.Wheel(p.X, p.Y, delta, horizontal, t.Window.ProcessId, op.Token);
        }

        return new Dictionary<string, object?> { ["action"] = "mouse_wheel", ["direction"] = dir, ["steps"] = notches };
    }

    // ---------------------------------------------------------------- read

    /// <summary>Text for expectations: Value, TextPattern, all text of containers, else Name.</summary>
    public static string ElementText(UiOp op, IUIAutomationElement e, UiNode n)
    {
        var el = op.Core.Wrap(e);
        if (n.IsPassword)
        {
            return string.Empty;
        }

        if (n.HasValue && !string.IsNullOrEmpty(n.Value))
        {
            return n.Value!;
        }

        if (n.HasText && el.Patterns.Text.TryGetPattern(out var tp))
        {
            var s = SafeRead(() => tp.DocumentRange.GetText(100_000), string.Empty);
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }

        if (n.Type is "Window" or "Pane" or "Group" or "Custom" or "List" or "Tree" or "Table" or "DataGrid" or "StatusBar" or "ToolBar" or "Tab")
        {
            return AllText(op, e, TimeSpan.FromSeconds(10));
        }

        return n.Name.Length > 0 ? n.Name : n.Label ?? string.Empty;
    }

    /// <summary>All visible text under an element (document-order walk with a time budget; password values skipped).</summary>
    public static string AllText(UiOp op, IUIAutomationElement root, TimeSpan budget)
    {
        var sb = new StringBuilder();
        var walker = op.Core.Walker(false);
        var cr = op.Core.NewCacheRequest(new[]
        {
            UIA_PropertyIds.UIA_NamePropertyId, UIA_PropertyIds.UIA_IsOffscreenPropertyId, UIA_PropertyIds.UIA_ValueValuePropertyId,
            UIA_PropertyIds.UIA_IsPasswordPropertyId,
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stack = new Stack<IUIAutomationElement>();
        stack.Push(root.BuildUpdatedCache(cr));
        string? last = null;
        int visited = 0;
        while (stack.Count > 0)
        {
            op.Token.ThrowIfCancellationRequested();
            if (sw.Elapsed > budget)
            {
                sb.AppendLine($"[... truncated after {budget.TotalSeconds:0}s ...]");
                break;
            }

            var e = stack.Pop();
            visited++;
            try
            {
                if (visited > 1 && e.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsOffscreenPropertyId) is true)
                {
                    continue;
                }

                var t = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_NamePropertyId) as string ?? string.Empty;
                var v = e.GetCachedPropertyValue(UIA_PropertyIds.UIA_IsPasswordPropertyId) is true
                    ? string.Empty
                    : e.GetCachedPropertyValue(UIA_PropertyIds.UIA_ValueValuePropertyId) as string ?? string.Empty;
                if (v.Length > 0 && v != t)
                {
                    t = t.Length > 0 ? $"{t}: {v}" : v;
                }

                if (t.Length > 0 && t != last)
                {
                    sb.AppendLine(t);
                    last = t;
                }

                var kids = ElementSearch.Children(walker, e, cr);
                for (int j = kids.Count - 1; j >= 0; j--)
                {
                    stack.Push(kids[j]);
                }
            }
            catch (COMException)
            {
            }
        }

        return sb.ToString();
    }

    public static Dictionary<string, object?> ReadTable(UiOp op, UiTarget t, int limit)
    {
        var el = t.Wrap(op.Core);
        var headers = new List<string>();
        var rows = new List<List<string>>();
        string source;
        bool more = false;
        headers.AddRange(ColumnHeaders(op, t.Element));

        if (el.Patterns.Grid.TryGetPattern(out var gp))
        {
            int rowCount = SafeRead(() => gp.RowCount.Value, 0), colCount = SafeRead(() => gp.ColumnCount.Value, 0);
            source = $"GridPattern {rowCount}x{colCount}";
            int lim = Math.Min(rowCount, limit);
            more = rowCount > lim;
            for (int r = 0; r < lim; r++)
            {
                op.Token.ThrowIfCancellationRequested();
                var row = new List<string>(colCount);
                for (int c = 0; c < colCount; c++)
                {
                    string cell;
                    try
                    {
                        var item = gp.GetItem(r, c);
                        cell = item is null ? string.Empty : CellText(op, CoreOf(item), 0);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException || ex.GetType().Name.EndsWith("Exception", StringComparison.Ordinal))
                    {
                        cell = string.Empty;
                    }

                    row.Add(cell);
                }

                rows.Add(row);
            }

            if (rows.Count > 0 || rowCount > 0)
            {
                return TableResult(source, headers, rows, more, rowCount);
            }
        }

        // Fallback: row-like children (DataItem/ListItem/TreeItem/Custom) whose children are cells.
        source = "child rows";
        var walker = op.Core.Walker(false);
        var cr = op.Core.NewCacheRequest(UiaCore.NodeProperties);
        foreach (var ch in ElementSearch.Children(walker, t.Element, cr))
        {
            op.Token.ThrowIfCancellationRequested();
            var n = UiaCore.ReadCached(ch);
            if (n.Type == "Header" && headers.Count == 0)
            {
                headers.AddRange(ElementSearch.Children(walker, ch, cr).Select(h => UiaCore.ReadCached(h).Name));
            }
            else if (n.Type is "DataItem" or "ListItem" or "TreeItem" or "Custom" or "Unknown")
            {
                if (rows.Count >= limit)
                {
                    more = true;
                    break;
                }

                var cells = ElementSearch.Children(walker, ch, cr).Select(c => CellText(op, c, 0)).ToList();
                if (cells.Count == 0)
                {
                    cells.Add(CellText(op, ch, 0));
                }

                // WinForms DataGridView exposes its header as a "Top Row" Custom element.
                if (headers.Count == 0 && n.Name == "Top Row")
                {
                    headers.AddRange(cells);
                }
                else
                {
                    rows.Add(cells);
                }
            }
        }

        return TableResult(source, headers, rows, more, null);
    }

    public static Dictionary<string, object?> Inspect(UiOp op, UiTarget t)
    {
        var n = op.Core.ReadLive(t.Element);
        var el = t.Wrap(op.Core);
        var props = el.Properties;
        var d = new Dictionary<string, object?>
        {
            ["type"] = n.Type,
            ["name"] = n.Name,
            ["automation_id"] = n.AutomationId,
            ["class"] = n.ClassName,
            ["framework"] = SafeRead(() => props.FrameworkId.ValueOrDefault, null),
            ["pid"] = SafeRead(() => props.ProcessId.ValueOrDefault, 0),
            ["hwnd"] = n.NativeWindowHandle == 0 ? null : UiText.Hex(n.NativeWindowHandle),
            ["bounds"] = n.Bounds is { } b ? new { x = b.X, y = b.Y, width = b.Width, height = b.Height } : null,
            ["window_relative_bounds"] = n.Bounds is { } b2 ? new { x = b2.X - t.Window.Left, y = b2.Y - t.Window.Top, width = b2.Width, height = b2.Height } : null,
            ["enabled"] = n.Enabled,
            ["offscreen"] = n.Offscreen,
            ["focused"] = n.Focused,
            ["keyboard_focusable"] = n.Focusable,
            ["password"] = n.IsPassword ? true : null,
            ["help_text"] = NullIfEmpty(SafeRead(() => props.HelpText.ValueOrDefault, null)),
            ["access_key"] = NullIfEmpty(SafeRead(() => props.AccessKey.ValueOrDefault, null)),
            ["accelerator_key"] = NullIfEmpty(SafeRead(() => props.AcceleratorKey.ValueOrDefault, null)),
            ["item_status"] = NullIfEmpty(SafeRead(() => props.ItemStatus.ValueOrDefault, null)),
            ["runtime_id"] = n.RuntimeId is null ? null : string.Join(".", n.RuntimeId),
        };

        try
        {
            d["patterns"] = el.GetSupportedPatterns().Select(p => p.Name.Replace("Pattern", string.Empty, StringComparison.Ordinal)).OrderBy(s => s).ToArray();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        if (n.HasValue)
        {
            d["value"] = n.IsPassword ? "(hidden)" : n.Value;
            d["read_only"] = n.ReadOnly;
        }

        d["toggle"] = n.ToggleText;
        d["expand"] = n.ExpandText;
        if (n.CanSelect)
        {
            d["selected"] = n.Selected;
        }

        if (n.HasRange && el.Patterns.RangeValue.TryGetPattern(out var rv))
        {
            d["range"] = new { value = SafeRead(() => rv.Value.Value, 0d), min = SafeRead(() => rv.Minimum.Value, 0d), max = SafeRead(() => rv.Maximum.Value, 0d) };
        }

        if (n.CanScroll && el.Patterns.Scroll.TryGetPattern(out var sp))
        {
            d["scroll"] = new { horizontal_percent = SafeRead(() => sp.HorizontalScrollPercent.Value, -1d), vertical_percent = SafeRead(() => sp.VerticalScrollPercent.Value, -1d) };
        }

        if (el.Patterns.Window.TryGetPattern(out var wp))
        {
            d["window_state"] = new { visual_state = SafeRead(() => wp.WindowVisualState.Value.ToString().ToLowerInvariant(), "?"), modal = SafeRead(() => wp.IsModal.Value, false) };
        }

        if (n.HasText && !n.IsPassword && el.Patterns.Text.TryGetPattern(out var tx))
        {
            d["text"] = UiText.Clip(SafeRead(() => tx.DocumentRange.GetText(600), string.Empty), 500);
        }

        if (el.Patterns.LegacyIAccessible.TryGetPattern(out var la))
        {
            d["default_action"] = NullIfEmpty(SafeRead(() => la.DefaultAction.Value, null));
        }

        d["actions"] = TreeSnapshot.Actions(n);
        return d;
    }

    // ---------------------------------------------------------------- expect

    public sealed record Expectation(string? Is, string? Text, bool Exact, string? Match, int? Count);

    public sealed record ExpectOutcome(bool Pass, string Message, object? Actual, UiTarget? Target = null, TopLevelWindow? Window = null);

    /// <summary>One evaluation of an expectation . Never throws for a failed check.</summary>
    public static ExpectOutcome Evaluate(UiOp op, WindowSpec spec, string? reference, string? selector, Expectation e)
    {
        string desc = reference ?? selector ?? $"window {spec}";
        if (e.Count is { } want)
        {
            if (string.IsNullOrWhiteSpace(selector) || Selector.LooksLikeRef(selector))
            {
                throw GatewayException.Validation("count needs a selector (not a ref).");
            }

            TopLevelWindow w;
            SearchResult found;
            try
            {
                (w, found) = op.Service.FindAll(op, spec, selector, int.MaxValue, TimeSpan.FromSeconds(10), includeOffscreen: false);
            }
            catch (GatewayException ex) when (ex.Code is GatewayErrorCodes.NotFound or GatewayErrorCodes.TargetNotReady)
            {
                return new ExpectOutcome(false, $"window or container not found: {ex.Message}", null);
            }
            catch (COMException ex)
            {
                return new ExpectOutcome(false, $"the UI changed while counting ({ex.Message})", null);
            }

            if (found.Stopped is not null)
            {
                // A partial count proves nothing either way.
                return new ExpectOutcome(false, $"{desc} count == {want}: search stopped early ({found.Stopped}) after {found.Matches.Count} matches — narrow it with in=", found.Matches.Count, null, w);
            }

            return new ExpectOutcome(found.Matches.Count == want, $"{desc} count == {want} (actual {found.Matches.Count})", found.Matches.Count, null, w);
        }

        var state = (e.Is ?? (e.Text is not null || e.Match is not null ? string.Empty : "exists")).ToLowerInvariant();
        UiTarget? t = null;
        string? err = null;
        try
        {
            t = op.Service.ResolveTarget(op, spec, reference, selector, allowWindow: true, TimeSpan.FromSeconds(5));
        }
        catch (GatewayException ex) when (ex.Code is GatewayErrorCodes.NotFound or GatewayErrorCodes.TargetNotReady)
        {
            err = ex.Message;
        }
        catch (COMException ex)
        {
            err = ex.Message;
        }

        if (state == "gone")
        {
            return new ExpectOutcome(t is null, t is null ? $"{desc} is gone" : $"{desc} is gone (still present: {t.Node.Describe()})", t is null ? "gone" : "present", t, t?.Window);
        }

        if (t is null)
        {
            return new ExpectOutcome(false, $"{desc} not found ({err})", "not found");
        }

        var n = t.Node;
        var parts = new List<string>();
        bool ok = true;
        object? actual = null;
        if (state.Length > 0 && state != "exists")
        {
            switch (state)
            {
                case "enabled": ok = n.Enabled; actual = n.Enabled ? "enabled" : "disabled"; break;
                case "disabled": ok = !n.Enabled; actual = n.Enabled ? "enabled" : "disabled"; break;
                case "visible": ok = !n.Offscreen; actual = n.Offscreen ? "hidden" : "visible"; break;
                case "hidden": ok = n.Offscreen; actual = n.Offscreen ? "hidden" : "visible"; break;
                case "focused": ok = n.Focused; actual = n.Focused ? "focused" : "not focused"; break;
                case "checked" or "unchecked" or "mixed":
                    actual = n.CanToggle ? n.Toggle switch { 1 => "checked", 0 => "unchecked", _ => "mixed" } : "no toggle pattern";
                    ok = Equals(actual, state);
                    break;
                case "selected": ok = n.CanSelect && n.Selected; actual = n.CanSelect ? (n.Selected ? "selected" : "not selected") : "not selectable"; break;
                case "expanded" or "collapsed":
                    actual = n.CanExpand ? n.Expand switch { 0 => "collapsed", 1 or 2 => "expanded", _ => "leaf" } : "not expandable";
                    ok = Equals(actual, state);
                    break;
                default:
                    throw GatewayException.Validation($"Unknown state '{e.Is}' (exists, gone, enabled, disabled, visible, hidden, focused, checked, unchecked, mixed, selected, expanded, collapsed).");
            }

            parts.Add($"{desc} is {state}{(ok ? string.Empty : $" (actual: {actual})")}");
        }

        if (e.Text is not null || e.Match is not null)
        {
            string txt;
            try
            {
                txt = ElementText(op, t.Element, n);
            }
            catch (COMException ex)
            {
                return new ExpectOutcome(false, $"{desc} vanished while reading its text ({ex.Message})", "not found", t, t.Window);
            }
            var norm = UiText.Norm(txt);
            bool tok;
            string how;
            if (e.Match is not null)
            {
                tok = UiText.CompileRegex(e.Match, "match").IsMatch(txt);
                how = $"matches /{e.Match}/";
            }
            else if (e.Exact)
            {
                tok = string.Equals(norm, UiText.Norm(e.Text), StringComparison.Ordinal);
                how = $"equals '{e.Text}'";
            }
            else
            {
                tok = norm.Contains(UiText.Norm(e.Text), StringComparison.OrdinalIgnoreCase);
                how = $"contains '{e.Text}'";
            }

            parts.Add($"{desc} text {how}{(tok ? string.Empty : $" (actual: '{UiText.Clip(txt, 200)}')")}");
            ok = ok && tok;
            actual = UiText.Clip(txt, 500);
        }

        if (parts.Count == 0)
        {
            parts.Add($"{desc} exists ({n.Describe()})");
            actual = "exists";
        }

        return new ExpectOutcome(ok, string.Join("; ", parts), actual, t, t.Window);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Best-effort activation for pattern actions (some apps only react when active). Never sends input.</summary>
    public static void TryActivate(TopLevelWindow w, CancellationToken ct = default)
    {
        try
        {
            InputSender.Activate(w.Handle, w.ProcessId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pattern actions work without focus.
        }
    }

    /// <summary>
    /// Alt+F4 closes whatever is in front. Refused for the application's main window, and — once the target was
    /// activated — when the foreground window is not exactly the window the caller named (e.g. the main window came
    /// to the front instead of the dialog).
    /// </summary>
    public static void EnsureAltF4Safe(UiOp op, IReadOnlyList<KeyStroke> strokes, TopLevelWindow w, bool afterActivation = false)
    {
        if (!InputSender.ContainsAltF4(strokes))
        {
            return;
        }

        if (op.Service.IsMainWindow(w))
        {
            throw GatewayException.Denied($"Alt+F4 would close the application's main window \"{UiText.Clip(w.Title, 60)}\". Close dialogs by naming them in 'window'; close Revit with revit_close.");
        }

        if (afterActivation)
        {
            var fg = NativeMethods.GetForegroundWindow();
            var root = fg == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(fg, NativeMethods.GaRoot);
            if (root.ToInt64() != w.Hwnd)
            {
                var d = TopLevelWindows.Describe(root);
                throw GatewayException.Denied($"Alt+F4 not sent: the foreground window is \"{UiText.Clip(d?.Title, 60)}\", not \"{UiText.Clip(w.Title, 60)}\".");
            }
        }
    }

    /// <summary>
    /// Keyboard input only goes where it was meant to: the focused element must be <paramref name="target"/> or inside it
    /// (an editable combo focuses its inner edit). Otherwise keys would land in whatever else has focus (e.g. the Revit
    /// canvas, where letters start commands).
    /// </summary>
    public static void EnsureFocusWithin(UiOp op, UiTarget target)
    {
        IUIAutomationElement? cur;
        try
        {
            cur = op.Core.Native.GetFocusedElement();
            var walker = op.Core.Walker(false);
            for (int depth = 0; cur is not null && depth < 15; depth++)
            {
                if (op.Core.SameElement(cur, target.Element))
                {
                    return;
                }

                cur = walker.GetParentElement(cur);
            }
        }
        catch (COMException)
        {
        }

        throw GatewayException.NotReady($"Could not put keyboard focus on {target.Node.Describe()}; keystrokes were NOT sent. The element may not accept focus — try without keyboard=true.");
    }

    /// <summary>
    /// Runs a pattern call with a short deadline: a Win32 button that opens a modal dialog does not return from
    /// Invoke until the dialog closes. Returns false when still blocked (the call keeps running in the background).
    /// </summary>
    public static bool Bounded(UiOp op, Action action, int ms)
    {
        var task = Task.Factory.StartNew(
            () => NativeMethods.WithPhysicalDpi(() =>
            {
                action();
                return true;
            }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            if (!task.Wait(ms, op.Token))
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                return false;
            }
        }
        catch (AggregateException ae) when (ae.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ae.InnerException).Throw();
        }

        return true;
    }

    public static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string ToggleName(ToggleState s) => s switch
    {
        ToggleState.On => "on",
        ToggleState.Off => "off",
        _ => "indeterminate",
    };

    private static bool ValuesEqual(string? got, string want)
    {
        if (string.Equals(got ?? string.Empty, want, StringComparison.Ordinal))
        {
            return true;
        }

        return double.TryParse(got, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
               double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;
    }

    private static void CollapseIf(FlaUI.Core.Patterns.IExpandCollapsePattern? ec, bool expandedByUs)
    {
        if (ec is not null && expandedByUs)
        {
            try
            {
                ec.Collapse();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>Selectable items of a container (ListItem/TabItem/TreeItem/DataItem/MenuItem/RadioButton), not descending into items.</summary>
    private static List<UiNode> ItemsOf(UiOp op, IUIAutomationElement container)
    {
        var walker = op.Core.Walker(false);
        var cr = op.Core.NewCacheRequest(UiaCore.NodeProperties);
        var items = new List<UiNode>();
        var queue = new Queue<(IUIAutomationElement El, int Depth)>();
        queue.Enqueue((container, 0));
        while (queue.Count > 0 && items.Count < 2000)
        {
            var (el, depth) = queue.Dequeue();
            foreach (var k in ElementSearch.Children(walker, el, cr))
            {
                var n = UiaCore.ReadCached(k);
                if (n.Type is "ListItem" or "TabItem" or "TreeItem" or "DataItem" or "MenuItem" or "RadioButton")
                {
                    items.Add(n);
                }
                else if (depth < 3)
                {
                    queue.Enqueue((k, depth + 1));
                }
            }
        }

        return items;
    }

    private static AutomationElement? ScrollContainer(UiOp op, IUIAutomationElement start)
    {
        var walker = op.Core.Walker(false);
        var cur = start;
        for (int g = 0; g < 30 && cur is not null; g++)
        {
            var el = op.Core.Wrap(cur);
            if (el.Patterns.Scroll.IsSupported)
            {
                return el;
            }

            try
            {
                cur = walker.GetParentElement(cur);
            }
            catch (COMException)
            {
                return null;
            }
        }

        return null;
    }

    private static IUIAutomationElement CoreOf(AutomationElement e) => UiaCore.Unwrap(e);

    /// <summary>Column header names: native TablePattern.GetCurrentColumnHeaders, else the HeaderItems of a Header child (WPF DataGrid).</summary>
    private static List<string> ColumnHeaders(UiOp op, IUIAutomationElement grid)
    {
        var names = new List<string>();
        try
        {
            if (grid.GetCurrentPattern(UIA_PatternIds.UIA_TablePatternId) is IUIAutomationTablePattern tp)
            {
                var arr = tp.GetCurrentColumnHeaders();
                for (int i = 0; arr is not null && i < arr.Length; i++)
                {
                    names.Add(arr.GetElement(i).CurrentName ?? string.Empty);
                }
            }
        }
        catch (COMException)
        {
        }

        if (names.Any(n => n.Length > 0))
        {
            return names;
        }

        names.Clear();
        var header = ElementSearch.Find(op.Core, grid, Selector.Parse("type=Header"), 1, TimeSpan.FromSeconds(2), op.Token).Matches.FirstOrDefault();
        if (header is not null)
        {
            var cr = op.Core.NewCacheRequest(UiaCore.NodeProperties);
            foreach (var hi in ElementSearch.Children(op.Core.Walker(false), header, cr))
            {
                var n = UiaCore.ReadCached(hi);
                if (n.Type == "HeaderItem")
                {
                    names.Add(n.Name.Length > 0 ? n.Name : CellText(op, hi, 0));
                }
            }
        }

        return names;
    }

    private static string CellText(UiOp op, IUIAutomationElement e, int depth)
    {
        try
        {
            var n = op.Core.ReadLive(e);
            if (n.HasValue && !n.IsPassword && !string.IsNullOrEmpty(n.Value))
            {
                return n.Value!;
            }

            if (n.Name.Length > 0)
            {
                return n.Name;
            }

            if (depth >= 3)
            {
                return string.Empty;
            }

            var walker = op.Core.Walker(false);
            var acc = new List<string>();
            var c = walker.GetFirstChildElement(e);
            while (c is not null && acc.Count < 5)
            {
                var s = CellText(op, c, depth + 1);
                if (s.Length > 0)
                {
                    acc.Add(s);
                }

                c = walker.GetNextSiblingElement(c);
            }

            return string.Join(" ", acc);
        }
        catch (COMException)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, object?> TableResult(string source, List<string> headers, List<List<string>> rows, bool more, int? rowCount) => new()
    {
        ["source"] = source,
        ["headers"] = headers,
        ["rows"] = rows,
        ["row_count"] = rowCount ?? rows.Count,
        ["returned_rows"] = rows.Count,
        ["more"] = more,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
