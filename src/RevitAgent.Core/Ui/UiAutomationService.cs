using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using Interop.UIAutomationClient;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Native;

namespace RevitAgent.Core.Ui;

/// <summary>Context of one synchronous UI operation running on a worker thread.</summary>
public sealed class UiOp
{
    internal UiOp(UiAutomationService service, string session, CancellationToken token)
    {
        Service = service;
        Session = session;
        Token = token;
    }

    public UiAutomationService Service { get; }

    public UiaCore Core => Service.Core;

    public string Session { get; }

    public CancellationToken Token { get; }

    /// <summary>Non-fatal remarks for the agent (e.g. "ref moved in the tree").</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Called before each auto-wait attempt (the script runner handles dialog rules here).</summary>
    public Action<UiOp>? BeforeAttempt { get; set; }

    /// <summary>When set, every resolved window must belong to this process (ui_run_script safety boundary).</summary>
    public int? RestrictPid { get; set; }

    public void Sleep(int ms)
    {
        Token.WaitHandle.WaitOne(ms);
        Token.ThrowIfCancellationRequested();
    }
}

/// <summary>An element (or a whole window) resolved from ref / selector / window arguments.</summary>
public sealed class UiTarget
{
    public required TopLevelWindow Window { get; init; }

    public required IUIAutomationElement Element { get; init; }

    public required UiNode Node { get; init; }

    public string? Ref { get; init; }

    /// <summary>The selector as the caller wrote it (null when targeted by ref or as the window itself).</summary>
    public string? Selector { get; init; }

    public bool IsWindow { get; init; }

    public AutomationElement Wrap(UiaCore core) => core.Wrap(Element);
}

/// <summary>
/// The out-of-process UI automation engine behind the ui_* tools (FlaUI.UIA3): runs UIA work on worker threads with
/// a wall-clock budget, serializes real input with one global lock, and resolves windows / refs / selectors.
/// </summary>
public sealed class UiAutomationService : IDisposable
{
    /// <summary>Default auto-wait (Playwright-style actionability) for actions.</summary>
    public static readonly TimeSpan DefaultActionWait = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly AuditLog _audit;

    public UiAutomationService(AgentPaths paths, AuditLog audit)
    {
        Paths = paths;
        _audit = audit;
    }

    public AgentPaths Paths { get; }

    public UiaCore Core { get; } = new();

    public RefStore Refs { get; } = new();

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated MTA thread (per-monitor DPI aware) and waits at most
    /// <paramref name="budget"/>. UIA blocks while the target is busy; then TARGET_NOT_READY is returned and the
    /// abandoned work is cancelled (it sends no further input). <paramref name="input"/> serializes with all other
    /// UI actions (BUSY after 20 s).
    /// </summary>
    public async Task<T> RunAsync<T>(string session, TimeSpan budget, bool input, CancellationToken cancellationToken, Func<UiOp, T> work, Action<UiOp>? configure = null)
    {
        if (input)
        {
            if (!await _inputLock.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false))
            {
                throw new GatewayException(GatewayErrorCodes.Busy, "Another UI action is still running (UI input is serialized). Retry in a few seconds.");
            }
        }

        Task<T>? task = null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var op = new UiOp(this, session, cts.Token);
            configure?.Invoke(op);
            task = Task.Factory.StartNew(
                () => NativeMethods.WithPhysicalDpi(() => Translate(() => work(op))),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            try
            {
                return await task.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                cts.Cancel();
                _ = task.ContinueWith(t => _audit.Diagnostic($"UI work finished after its {budget.TotalSeconds:0}s budget: {t.Status}", t.Exception), TaskScheduler.Default);
                throw GatewayException.NotReady(
                    $"Target UI not responding: UI Automation did not answer within {budget.TotalSeconds:0.#}s (the application is busy or blocked by a modal dialog). " +
                    "Check ui_list_windows / revit_get_state, then retry.");
            }
        }
        finally
        {
            if (task is { IsCompleted: false })
            {
                // Abandoned (timeout or client cancel): it is cancelled and sends no more input, but keep the input lock
                // until it really stops (bounded) so a following action can never interleave with it.
                cts.Cancel();
                ReleaseWhenDone(task, input, cts);
            }
            else
            {
                cts.Dispose();
                if (input)
                {
                    _inputLock.Release();
                }
            }
        }
    }

    private void ReleaseWhenDone(Task task, bool input, CancellationTokenSource cts)
    {
        int released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0 && input)
            {
                _inputLock.Release();
            }
        }

        // The token source is disposed only when the work has really finished (it may still be reading the token).
        _ = task.ContinueWith(
            _ =>
            {
                Release();
                cts.Dispose();
            },
            TaskScheduler.Default);
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Release(), TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- windows

    /// <summary>
    /// Resolves the window of a call. Empty spec → the session's current window (last snapshot/find target).
    /// </summary>
    public TopLevelWindow ResolveWindow(UiOp op, WindowSpec spec)
    {
        TopLevelWindow w;
        if (spec.IsEmpty)
        {
            var cur = Refs.CurrentWindow(op.Session);
            var d = cur is { } h ? TopLevelWindows.Describe(new IntPtr(h)) : null;
            if (op.RestrictPid is { } rp && (d is null || d.ProcessId != rp))
            {
                d = new WindowSpec { Pid = rp }.Resolve();
            }

            w = d ?? throw GatewayException.Validation("Pass 'pid' (target process), 'window' (title, /regex/ or hwnd:0x...) or 'hwnd' to choose the window.");
        }
        else
        {
            var s = op.RestrictPid is { } rp && spec.Pid is null ? new WindowSpec { Pid = rp, Window = spec.Window, Hwnd = spec.Hwnd } : spec;
            w = s.Resolve();
        }

        if (op.RestrictPid is { } p && w.ProcessId != p)
        {
            throw GatewayException.Denied($"Window {w.HwndHex} \"{w.Title}\" belongs to process {w.ProcessId}; this run may only drive process {p}.");
        }

        return w;
    }

    public bool IsMainWindow(TopLevelWindow w) => TopLevelWindows.PickMain(TopLevelWindows.List(w.ProcessId))?.Hwnd == w.Hwnd;

    // ---------------------------------------------------------------- targets

    /// <summary>
    /// Resolves the element of a call: <paramref name="reference"/> (e12) or <paramref name="selector"/>; with neither,
    /// the window itself when <paramref name="allowWindow"/>.
    /// </summary>
    public UiTarget ResolveTarget(UiOp op, WindowSpec spec, string? reference, string? selector, bool allowWindow, TimeSpan? searchBudget = null)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            return ResolveRef(op, spec, reference.Trim());
        }

        var window = ResolveWindow(op, spec);
        var root = op.Core.FromHandle(window.Hwnd);
        if (string.IsNullOrWhiteSpace(selector))
        {
            if (!allowWindow)
            {
                throw GatewayException.Validation("Pass 'ref' (from ui_snapshot / ui_find, e.g. e12) or 'selector' (e.g. id=btnOk, name=\"OK\" type=Button).");
            }

            return new UiTarget { Window = window, Element = root, Node = op.Core.ReadLive(root), IsWindow = true };
        }

        if (Selector.LooksLikeRef(selector))
        {
            return ResolveRef(op, spec, selector.Trim());
        }

        var sel = Selector.Parse(selector);
        var searchRoot = SearchRoot(op, spec, window, root, sel);
        var budget = searchBudget ?? TimeSpan.FromSeconds(15);
        var found = ElementSearch.Find(op.Core, searchRoot, sel, sel.Index + 1, budget, op.Token);
        if (found.Matches.Count <= sel.Index)
        {
            var why = found.Stopped is null ? string.Empty : $"; search stopped early ({found.Stopped}, {found.Visited} elements checked) — narrow it with in=<container> or a closer window";
            throw GatewayException.NotFound(
                $"No element matches '{selector}' in window \"{UiText.Clip(window.Title, 80)}\" ({found.Matches.Count} found{why}). Take a ui_snapshot to see what is there.",
                new { selector, window = window.Title, hwnd = window.HwndHex, found = found.Matches.Count });
        }

        var el = found.Matches[sel.Index];
        return new UiTarget { Window = window, Element = el, Node = op.Core.ReadLive(el), Selector = selector };
    }

    /// <summary>
    /// Playwright-style actionability: retries until the target exists (and is enabled) or <paramref name="wait"/>
    /// elapses. Unknown/stale refs fail fast (they never come back).
    /// </summary>
    public UiTarget ResolveActionable(UiOp op, WindowSpec spec, string? reference, string? selector, bool needEnabled, TimeSpan wait, bool allowWindow = false)
    {
        var deadline = DateTime.UtcNow + wait;
        GatewayException? last = null;
        while (true)
        {
            op.Token.ThrowIfCancellationRequested();
            op.BeforeAttempt?.Invoke(op);
            bool isRef = !string.IsNullOrWhiteSpace(reference) || Selector.LooksLikeRef(selector);
            try
            {
                var t = ResolveTarget(op, spec, reference, selector, allowWindow, TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 2, 15)));
                if (needEnabled && !t.Node.Enabled)
                {
                    throw GatewayException.NotReady($"{t.Node.Describe()} is disabled.", new { target = TargetInfo(op, t, computeSelector: false) });
                }

                return t;
            }
            catch (GatewayException ex) when (ex.Code is GatewayErrorCodes.NotFound or GatewayErrorCodes.TargetNotReady)
            {
                last = ex;
                if (isRef && ex.Code == GatewayErrorCodes.NotFound)
                {
                    throw;
                }
            }
            catch (COMException ex)
            {
                last = UiaCore.Translate(ex) ?? GatewayException.NotReady(ex.Message);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new GatewayException(last!.Code, $"{last.Message} (auto-waited {wait.TotalSeconds:0.#}s)", last.Details);
            }

            op.Sleep(250);
        }
    }

    /// <summary>All matches of a selector in the window (honours in= / from=end), onscreen first.</summary>
    public (TopLevelWindow Window, SearchResult Result) FindAll(UiOp op, WindowSpec spec, string selector, int need, TimeSpan budget, bool includeOffscreen)
    {
        var window = ResolveWindow(op, spec);
        var root = op.Core.FromHandle(window.Hwnd);
        var sel = Selector.Parse(selector);
        var searchRoot = SearchRoot(op, spec, window, root, sel);
        return (window, ElementSearch.Find(op.Core, searchRoot, sel, need, budget, op.Token, includeOffscreen));
    }

    /// <summary>Snapshot/find bookkeeping: register an element in the window's ref set and return its ref.</summary>
    public string AddRef(RefSet set, UiNode node) => set.FindByRuntimeId(node.RuntimeId) ?? set.Add(node);

    /// <summary>
    /// Target description for results; includes a resolved selector (+ window) so recorded calls can be replayed as
    /// UI script steps. When the caller used a selector it is returned verbatim.
    /// </summary>
    public object TargetInfo(UiOp op, UiTarget t, bool computeSelector = true)
    {
        string? selector = t.Selector;
        if (selector is null && !t.IsWindow && computeSelector)
        {
            try
            {
                selector = ComputeSelector(op, t);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The action may have closed the window (OK in a dialog): the action still succeeded. Best guess.
                selector = t.Node.AutomationId.Length > 0
                    ? Selector.Build(t.Node.AutomationId, null, null)
                    : Selector.Build(null, t.Node.Name, t.Node.Type);
            }
        }

        bool isMain = IsMainWindow(t.Window);
        return new
        {
            selector,
            @ref = t.Ref,
            window = t.Window.Title,
            hwnd = t.Window.HwndHex,
            pid = t.Window.ProcessId,
            is_main_window = isMain,
            element = t.IsWindow ? $"Window \"{UiText.Clip(t.Window.Title, 60)}\"" : t.Node.Describe(),
        };
    }

    /// <summary>
    /// A selector that finds this element again: <c>id=</c> when it has an AutomationId, else name + type; with
    /// <c>index=</c> when earlier matches exist. Bounded by a short search budget.
    /// </summary>
    public string ComputeSelector(UiOp op, UiTarget t)
    {
        var n = t.Node;
        var candidates = new List<string>();
        if (n.AutomationId.Length > 0)
        {
            candidates.Add(Selector.Build(n.AutomationId, null, null));
            candidates.Add(Selector.Build(n.AutomationId, null, null) + ";type=" + n.Type);
        }

        if (n.Name.Length > 0)
        {
            candidates.Add(Selector.Build(null, n.Name, n.Type));
        }

        if (candidates.Count == 0)
        {
            candidates.Add("type=" + n.Type);
        }

        var root = op.Core.FromHandle(t.Window.Hwnd);
        string? fallback = null;
        foreach (var c in candidates.Distinct())
        {
            Selector sel;
            try
            {
                sel = Selector.Parse(c);
            }
            catch (GatewayException)
            {
                continue;
            }

            // Replay resolves a selector to its first match, so "our element is the first match" is enough (and stops the
            // search early on big trees such as the Revit main window); otherwise look a little further for its index.
            var first = ElementSearch.Find(op.Core, root, sel, 1, TimeSpan.FromSeconds(1.5), op.Token, includeOffscreen: n.Offscreen);
            if (first.Matches.Count > 0 && op.Core.SameElement(first.Matches[0], t.Element))
            {
                return c;
            }

            if (fallback is not null)
            {
                continue;
            }

            var found = ElementSearch.Find(op.Core, root, sel, 20, TimeSpan.FromSeconds(1.5), op.Token, includeOffscreen: n.Offscreen);
            for (int i = 0; i < found.Matches.Count; i++)
            {
                if (op.Core.SameElement(found.Matches[i], t.Element))
                {
                    if (i == 0)
                    {
                        return c;
                    }

                    fallback ??= c + ";index=" + i;
                    break;
                }
            }
        }

        return fallback ?? candidates[0];
    }

    public void Dispose()
    {
        Core.Dispose();
        _inputLock.Dispose();
    }

    private UiTarget ResolveRef(UiOp op, WindowSpec spec, string reference)
    {
        if (!Selector.LooksLikeRef(reference))
        {
            throw GatewayException.Validation($"'{reference}' is not a ref (refs look like e12; use 'selector' for id=/name=/type=).");
        }

        long? hwnd = null;
        if (spec.NamesWindow)
        {
            hwnd = ResolveWindow(op, spec).Hwnd;
        }

        var set = Refs.Find(op.Session, hwnd, spec.Pid ?? op.RestrictPid);
        if (set is null)
        {
            throw GatewayException.NotFound($"No refs for {(spec.IsEmpty ? "this session" : spec.ToString())} yet: take a ui_snapshot (or ui_find) of the window first.");
        }

        var window = TopLevelWindows.Describe(new IntPtr(set.Hwnd))
            ?? throw GatewayException.NotFound($"The window of ref '{reference}' ({UiText.Hex(set.Hwnd)} \"{set.Title}\") no longer exists. Call ui_list_windows and take a new snapshot.");
        if (op.RestrictPid is { } rp && window.ProcessId != rp)
        {
            throw GatewayException.Denied($"Ref '{reference}' belongs to process {window.ProcessId}; this run may only drive process {rp}.");
        }

        var entry = set.Get(reference)
            ?? throw GatewayException.NotFound($"Unknown ref '{reference}' for window \"{UiText.Clip(window.Title, 60)}\" — refs come from the latest ui_snapshot / ui_find of that window. Take a new snapshot.");

        try
        {
            var node = op.Core.ReadLive(entry.Element);
            if (entry.RuntimeId is null || node.RuntimeId is null || node.RuntimeId.AsSpan().SequenceEqual(entry.RuntimeId))
            {
                if (node.Type == entry.Type)
                {
                    return new UiTarget { Window = window, Element = node.Element, Node = node, Ref = reference };
                }
            }
        }
        catch (COMException)
        {
            // Gone: try to re-find it below.
        }

        // The element was re-created (WPF re-template, list refresh). Accept only a UNIQUE match on type + id + name.
        if (entry.AutomationId.Length > 0 || entry.Name.Length > 0)
        {
            var parts = new List<string>();
            if (ControlTypes.Id(entry.Type) is not null)
            {
                parts.Add("type=" + entry.Type);
            }
            if (entry.AutomationId.Length > 0)
            {
                parts.Add("id=" + UiText.Quote(entry.AutomationId));
            }

            if (entry.Name.Length > 0)
            {
                parts.Add("name=" + UiText.Quote(entry.Name));
            }

            var sel = Selector.Parse(string.Join(";", parts));
            var found = ElementSearch.Find(op.Core, op.Core.FromHandle(window.Hwnd), sel, 2, TimeSpan.FromSeconds(3), op.Token);
            if (found.Matches.Count == 1)
            {
                op.Notes.Add($"ref {reference} was re-created by the app; matched it uniquely by type+id+name");
                var node = op.Core.ReadLive(found.Matches[0]);
                return new UiTarget { Window = window, Element = node.Element, Node = node, Ref = reference };
            }
        }

        throw GatewayException.NotFound($"Ref '{reference}' is stale (the UI changed or the element is gone). Take a new ui_snapshot.");
    }

    private IUIAutomationElement SearchRoot(UiOp op, WindowSpec spec, TopLevelWindow window, IUIAutomationElement root, Selector sel)
    {
        if (sel.In is not { Length: > 0 } container)
        {
            return root;
        }

        if (Selector.LooksLikeRef(container))
        {
            var t = ResolveRef(op, spec, container);
            if (t.Window.Hwnd != window.Hwnd)
            {
                throw GatewayException.Validation($"Container ref '{container}' belongs to another window.");
            }

            return t.Element;
        }

        var csel = Selector.Parse(container);
        var hits = ElementSearch.Find(op.Core, root, csel, csel.Index + 1, TimeSpan.FromSeconds(10), op.Token);
        if (hits.Matches.Count <= csel.Index)
        {
            throw GatewayException.NotFound($"Container '{container}' (in=) not found in window \"{UiText.Clip(window.Title, 60)}\".");
        }

        return hits.Matches[csel.Index];
    }

    private static T Translate<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (GatewayException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UiaCore.Translate(ex) is { } gx)
        {
            throw gx;
        }
    }
}
