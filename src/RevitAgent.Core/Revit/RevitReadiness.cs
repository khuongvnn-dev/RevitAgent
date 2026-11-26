using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Revit.Native;
using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Core.Revit;

/// <summary>Phase of the readiness gate.</summary>
public enum ReadinessPhase
{
    /// <summary>Right after Revit started (before a document is opened).</summary>
    Start,

    /// <summary>Between steps / while waiting for a tool.</summary>
    Step,
}

/// <summary>Options of one readiness wait.</summary>
public sealed record ReadinessOptions
{
    public ReadinessPhase Phase { get; init; } = ReadinessPhase.Step;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The state must stay clear this long to count as ready (also: grace for transient dialogs).</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Answer dialogs matched by informational rules (never forbidden ones).</summary>
    public bool ApplyRules { get; init; } = true;

    /// <summary>Also require the Bridge pipe of the process (when the Bridge add-in is installed for that Revit).</summary>
    public bool RequireBridgePipe { get; init; }

    /// <summary>
    /// Unattended work: a forbidden dialog of a Revit the gateway launched is put to the owner on Telegram (screenshot + the dialog's buttons);
    /// the owner's answer is pressed, the owner may also click in Revit, and on timeout the SAFE button is pressed (see <see cref="SafeButtonPolicy"/>).
    /// Default false: an interactive AI agent asks the human itself (human_ask).
    /// </summary>
    public bool EscalateForbiddenDialogs { get; init; }

    /// <summary>
    /// Total time this wait may spend waiting for the owner's answers (escalations). That time does not count against
    /// <see cref="Timeout"/> (the clock pauses while the owner decides). Default: <see cref="HumanWaitBudgetFor"/>.
    /// </summary>
    public TimeSpan? HumanWaitBudget { get; init; }

    /// <summary>Folder for blocker screenshots (default: captures\dialogs).</summary>
    public string? EvidenceDir { get; init; }

    /// <summary>
    /// The gateway operation this wait belongs to (revit_launch / revit_wait_ready). When set (and <see cref="ApplyRules"/>), a
    /// non-forbidden dialog of a Revit this gateway launched that is still open after the grace period gets its safe-dismiss
    /// button pressed (<see cref="SafeDismissPolicy"/>, twice per window at most) before the wait reports MODAL_BLOCKED.
    /// Null (default): never.
    /// </summary>
    public string? SafeDismissFor { get; init; }

    /// <summary>Owner-wait budget of one escalating wait: two full questions (the per-window maximum) plus a margin.</summary>
    public static TimeSpan HumanWaitBudgetFor(IHumanEscalation? escalation) =>
        2 * (escalation?.DefaultTimeout ?? TimeSpan.FromMinutes(30)) + TimeSpan.FromMinutes(1);
}

/// <summary>Outcome of a readiness wait.</summary>
public sealed record ReadinessResult
{
    public int Pid { get; init; }

    public bool Ready { get; init; }

    /// <summary>ready | modal | not_responding | timeout | exited.</summary>
    public string State { get; init; } = RevitStates.Ready;

    /// <summary>Error code an agent should treat this as (MODAL_BLOCKED, TARGET_NOT_READY, NOT_FOUND); null when ready.</summary>
    public string? Code { get; init; }

    public string? Reason { get; init; }

    /// <summary>What the agent should do next.</summary>
    public string? NextStep { get; init; }

    public IReadOnlyList<HandledDialog> Handled { get; init; } = Array.Empty<HandledDialog>();

    public IReadOnlyList<Blocker> Blockers { get; init; } = Array.Empty<Blocker>();

    public IReadOnlyList<string> Screenshots { get; init; } = Array.Empty<string>();

    public double WaitedSec { get; init; }

    public bool? BridgePipeAvailable { get; init; }

    public IReadOnlyList<string>? RuleErrors { get; init; }
}

/// <summary>Result of one precise dialog click.</summary>
public sealed record DialogClickResult
{
    public int Pid { get; init; }

    public HandledDialog Handled { get; init; } = new();

    /// <summary>Blockers still present after the click (titles/hwnds only).</summary>
    public IReadOnlyList<Blocker> RemainingBlockers { get; init; } = Array.Empty<Blocker>();
}

/// <summary>
/// Readiness gate for a Revit process, reusable by every package (UI, Bridge, scenarios).
/// Detection is Win32-only and message-free (safe on a busy Revit); details (texts/buttons) use WM_GETTEXT with
/// SendMessageTimeout and, for TaskDialogs/WPF, UI Automation with a strict timeout.
/// </summary>
public interface IRevitReadiness
{
    /// <summary>Message-free snapshot: main window + blockers. Never blocks on the target.</summary>
    RevitSnapshot Detect(int pid);

    /// <summary>Readiness snapshot with blocker details and categories (info | forbidden | unknown).</summary>
    RevitState GetState(int pid, bool includeDetails = true, string? screenshotDir = null);

    /// <summary>Waits until ready, applying only informational rules; stops at the first forbidden dialog.</summary>
    Task<ReadinessResult> WaitReadyAsync(int pid, ReadinessOptions options, CancellationToken cancellationToken);

    /// <summary>Clicks one button of one blocking dialog precisely; refuses forbidden dialogs (PERMISSION_DENIED).</summary>
    DialogClickResult HandleDialog(int pid, IntPtr hwnd, string button, bool discardChanges, TimeSpan verifyTimeout, string by = "revit_handle_dialog");

    /// <summary>Throws MODAL_BLOCKED / TARGET_NOT_READY / NOT_FOUND unless the process is clear (for tools about to act).</summary>
    void EnsureNoBlockers(int pid);

    /// <summary>
    /// One pass: answers the informational dialogs that match a rule (e.g. 'Unresolved References' → Ignore and continue);
    /// never touches forbidden or unknown dialogs. Used while a long Bridge call (doc_open) waits on Revit.
    /// </summary>
    IReadOnlyList<HandledDialog> AnswerRuledDialogs(int pid, IReadOnlySet<string>? skipHwnds = null) => Array.Empty<HandledDialog>();

    /// <summary>
    /// One pass while <paramref name="operation"/> (e.g. doc_open) waits on a Revit THIS gateway launched: presses the safe-dismiss
    /// button of each blocking dialog that <see cref="SafeDismissPolicy"/> allows (screenshot, audit "safe_dismiss", owner notification).
    /// Does nothing for a Revit the gateway did not launch.
    /// </summary>
    IReadOnlyList<HandledDialog> SafeDismissDialogs(int pid, string operation, IReadOnlySet<string>? skipHwnds = null) => Array.Empty<HandledDialog>();

    /// <summary>Serializes acting calls per pid; throws BUSY when another acting call holds the pid.</summary>
    IDisposable AcquireActionLock(int pid);
}

/// <inheritdoc cref="IRevitReadiness"/>
public sealed class RevitReadiness : IRevitReadiness
{
    public static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    private readonly AgentPaths _paths;
    private readonly DialogRuleProvider _rules;
    private readonly LaunchedProcessRegistry _launched;
    private readonly GatewayEvents _events;
    private readonly AddinAllowlist _allowlist;
    private readonly AuditLog _audit;
    private readonly IHumanEscalation? _escalation;
    private readonly INotifier? _notifier;
    private readonly EvidenceService _evidence;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public RevitReadiness(
        AgentPaths paths, DialogRuleProvider rules, LaunchedProcessRegistry launched, GatewayEvents events, AddinAllowlist allowlist, AuditLog audit,
        IHumanEscalation? escalation = null, INotifier? notifier = null, EvidenceService? evidence = null)
    {
        _evidence = evidence ?? new EvidenceService();
        _escalation = escalation;
        _notifier = notifier;
        _paths = paths;
        _rules = rules;
        _launched = launched;
        _events = events;
        _allowlist = allowlist;
        _audit = audit;
    }

    public RevitSnapshot Detect(int pid)
    {
        if (!Win32Windows.IsProcessAlive(pid))
        {
            return new RevitSnapshot(pid, false, null, null, Array.Empty<RawBlocker>());
        }

        var windows = Win32Windows.TopLevelWindows(pid);
        var main = Win32Windows.FindMainWindow(windows);
        var blockers = new List<RawBlocker>();

        var dialogs = windows.Where(w => w.IsDialogClass && !Win32Windows.IsNoise(w)).ToList();
        blockers.AddRange(dialogs.Select(d => new RawBlocker(d, BlockerKinds.Dialog)));

        if (main is { Enabled: false } && dialogs.Count == 0)
        {
            // A non-#32770 modal (WPF/WinForms window of an add-in) disables the main window.
            var popup = Win32Windows.LastActivePopup(main.Hwnd);
            var modal = windows
                .Where(w => w.Hwnd != main.Hwnd && w.Enabled && !w.IsDialogClass && !Win32Windows.IsNoise(w) &&
                            (w.Title.Length > 0 || w.Hwnd == popup) && !(w.IsToolWindow && w.Title.Length == 0))
                .OrderByDescending(w => w.Hwnd == popup)
                .ToList();
            if (modal.Count > 0)
            {
                blockers.AddRange(modal.Select(w => new RawBlocker(w, BlockerKinds.ModalWindow)));
            }
            else
            {
                blockers.Add(new RawBlocker(main, BlockerKinds.Disabled));
            }
        }

        if (main is { Hung: true })
        {
            blockers.Add(new RawBlocker(main, BlockerKinds.Hung));
        }

        return new RevitSnapshot(pid, true, ProcessName(pid), main, blockers);
    }

    public RevitState GetState(int pid, bool includeDetails = true, string? screenshotDir = null)
    {
        var snap = Detect(pid);
        var rules = _rules.Load();
        var blockers = new List<Blocker>();
        foreach (var raw in snap.Blockers)
        {
            var b = Describe(raw, snap.ProcessName, rules, includeDetails, out _);
            if (screenshotDir is not null)
            {
                b = b with { ScreenshotPath = Capture(pid, raw.Window, raw.Kind, screenshotDir) };
            }

            blockers.Add(b);
        }

        return ToState(snap, blockers, rules);
    }

    public async Task<ReadinessResult> WaitReadyAsync(int pid, ReadinessOptions options, CancellationToken cancellationToken)
    {
        var wall = Stopwatch.StartNew();
        var settle = options.Settle < TimeSpan.Zero ? TimeSpan.Zero : options.Settle;
        var timeout = options.Timeout < settle ? settle : options.Timeout;

        // Time spent waiting for the owner (escalations) does not count against the readiness timeout.
        var humanBudget = options.HumanWaitBudget ?? ReadinessOptions.HumanWaitBudgetFor(_escalation);
        var humanWaited = TimeSpan.Zero;
        var sw = new PausableClock(wall, () => humanWaited);
        var handled = new List<HandledDialog>();
        var shots = new List<string>();
        var firstSeen = new Dictionary<string, TimeSpan>();
        var ruleAttempts = new Dictionary<IntPtr, int>();
        var trustAttempts = new Dictionary<IntPtr, int>();
        var escalations = new Dictionary<IntPtr, int>();
        var dismissAttempts = new Dictionary<IntPtr, int>();
        var cache = new Dictionary<IntPtr, (string Title, Blocker Blocker, DialogDetails? Details)>();
        TimeSpan? clearSince = null;
        var last = new List<Blocker>();
        var rules = _rules.Load();
        bool? pipe = null;

        // The Bridge was refused at its unsigned-add-in prompt: its pipe will never appear, so stop waiting for it.
        bool bridgeRefused = false;
        bool NeedPipe() => options.RequireBridgePipe && !bridgeRefused;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = sw.Elapsed;
            var snap = Detect(pid);
            if (!snap.Alive)
            {
                return Result(pid, false, RevitStates.Exited, GatewayErrorCodes.NotFound, $"Process {pid} exited while waiting for readiness.",
                    "Check revit_list_instances; relaunch with revit_launch if the user wants Revit running.", handled, Array.Empty<Blocker>(), shots, wall, pipe, rules);
            }

            if (NeedPipe())
            {
                pipe = BridgePipes.IsAvailable(pid);
            }

            if (snap.Blockers.Count == 0)
            {
                last.Clear();
                firstSeen.Clear();
                var clear = snap.IsClear && (!NeedPipe() || pipe != false);
                if (clear)
                {
                    clearSince ??= now;
                    if (now - clearSince.Value >= settle)
                    {
                        return Result(pid, true, RevitStates.Ready, null, null, null, handled, Array.Empty<Blocker>(), shots, wall, pipe, rules);
                    }
                }
                else
                {
                    clearSince = null;
                }
            }
            else
            {
                clearSince = null;
                var current = new List<(RawBlocker Raw, Blocker Blocker, DialogDetails? Details)>();
                foreach (var raw in snap.Blockers)
                {
                    var key = raw.Window.HwndHex + "|" + raw.Kind;
                    if (!firstSeen.ContainsKey(key))
                    {
                        firstSeen[key] = now;
                    }

                    if (!cache.TryGetValue(raw.Window.Hwnd, out var entry) || entry.Title != raw.Window.Title || raw.Kind is BlockerKinds.Disabled or BlockerKinds.Hung)
                    {
                        var described = Describe(raw, snap.ProcessName, rules, includeDetails: true, out var details);
                        entry = (raw.Window.Title, described, details);
                        cache[raw.Window.Hwnd] = entry;
                    }

                    current.Add((raw, entry.Blocker with { SeenForSec = Math.Round((now - firstSeen[key]).TotalSeconds, 1) }, entry.Details));
                }

                last = current.Select(c => c.Blocker).ToList();

                // 0. "Security - Unsigned Add-In" of a Revit THIS gateway launched: the owner's allowlist decides (Load Once /
                //    Do Not Load). Only here, never on an agent's request; anything unclear falls through to the forbidden stop.
                var unsigned = current.FirstOrDefault(c => c.Raw.Kind == BlockerKinds.Dialog && AddinAllowlistPolicy.IsUnsignedAddinTitle(c.Raw.Window.Title));
                if (unsigned.Raw is not null && trustAttempts.GetValueOrDefault(unsigned.Raw.Window.Hwnd) < 3 && _launched.IsOwned(pid))
                {
                    trustAttempts[unsigned.Raw.Window.Hwnd] = trustAttempts.GetValueOrDefault(unsigned.Raw.Window.Hwnd) + 1;
                    var (answered, retry) = TryAnswerUnsignedAddin(pid, unsigned.Raw, snap.ProcessName, rules, options, shots);
                    if (answered is not null)
                    {
                        handled.Add(answered);
                        if (answered.AddinTrust is { Decision: "do_not_load" } t &&
                            string.Equals(t.Name, AddinAllowlist.BridgeAddinName, StringComparison.OrdinalIgnoreCase))
                        {
                            bridgeRefused = true;
                        }
                        cache.Remove(unsigned.Raw.Window.Hwnd);
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (retry)
                    {
                        cache.Remove(unsigned.Raw.Window.Hwnd);
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                // 1. Forbidden → stop immediately (screenshot for the human), never answer.
                var forbidden = current.FirstOrDefault(c => c.Blocker.Category == DialogCategories.Forbidden);
                if (forbidden.Raw is not null)
                {
                    // 1b. Unattended run that opted in, Revit launched by this gateway: ask the owner on Telegram (twice per window at most,
                    //     when the click did not close it). Anything unclear falls through to the stop below.
                    if (options.EscalateForbiddenDialogs && _escalation is not null && _launched.IsOwned(pid) &&
                        forbidden.Raw.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow &&
                        escalations.GetValueOrDefault(forbidden.Raw.Window.Hwnd) < 2)
                    {
                        escalations[forbidden.Raw.Window.Hwnd] = escalations.GetValueOrDefault(forbidden.Raw.Window.Hwnd) + 1;
                        var asked = wall.Elapsed;
                        var outcome = await EscalateAsync(pid, forbidden.Raw, snap.ProcessName, rules, options, humanBudget - humanWaited, shots, cancellationToken).ConfigureAwait(false);
                        humanWaited += wall.Elapsed - asked;
                        if (outcome.Handled is not null)
                        {
                            handled.Add(outcome.Handled);
                        }

                        if (outcome.Resolved)
                        {
                            cache.Remove(forbidden.Raw.Window.Hwnd);
                            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    var shot = Capture(pid, forbidden.Raw.Window, "forbidden", options.EvidenceDir);
                    AddShot(shots, shot);
                    var blockers = WithShot(last, forbidden.Blocker.Hwnd, shot);
                    return Result(pid, false, RevitStates.Modal, GatewayErrorCodes.ModalBlocked,
                        $"Dialog '{forbidden.Blocker.Title}' is in a forbidden category ({forbidden.Blocker.ForbiddenReason}). It was not answered.",
                        "Do NOT answer it yourself: show the screenshot to the user and ask them to answer it in Revit, then call revit_wait_ready again." +
                        (forbidden.Blocker.SavePrompt == true ? " (Only in a Revit the gateway launched, a save prompt of a sandbox copy may be dismissed with revit_handle_dialog button='No' and discard_changes:true.)" : string.Empty),
                        handled, blockers, shots, wall, pipe, rules);
                }

                // 2. Informational rules → click precisely, then re-observe.
                if (options.ApplyRules)
                {
                    var ruled = current.FirstOrDefault(c => c.Blocker.Category == DialogCategories.Info && c.Details is not null &&
                                                          ruleAttempts.GetValueOrDefault(c.Raw.Window.Hwnd) < 2);
                    if (ruled.Raw is not null)
                    {
                        // Re-read right before clicking: the cached details may be stale (content changed in place).
                        var fresh = Describe(ruled.Raw, snap.ProcessName, rules, includeDetails: true, out var freshDetails);
                        var rule = fresh.Category == DialogCategories.Info && freshDetails is { Complete: true }
                            ? rules.Match(snap.ProcessName, ruled.Raw.Window.Title, freshDetails.Texts)
                            : null;
                        if (rule is null)
                        {
                            cache[ruled.Raw.Window.Hwnd] = (ruled.Raw.Window.Title, fresh, freshDetails);
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        ruleAttempts[ruled.Raw.Window.Hwnd] = ruleAttempts.GetValueOrDefault(ruled.Raw.Window.Hwnd) + 1;
                        handled.Add(ApplyRule(pid, ruled.Raw.Window, freshDetails!, rule));
                        cache.Remove(ruled.Raw.Window.Hwnd);
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                // 3. Still there after the grace period → blocked (unknown dialog, disabled main window, hung).
                var dialogGrace = TimeSpan.FromSeconds(Math.Max(10, 2 * settle.TotalSeconds));
                var hungGrace = TimeSpan.FromSeconds(Math.Max(30, 6 * settle.TotalSeconds));
                var stuck = current.FirstOrDefault(c => now - firstSeen[c.Raw.Window.HwndHex + "|" + c.Raw.Kind] >= (c.Raw.Kind == BlockerKinds.Hung ? hungGrace : dialogGrace));
                if (stuck.Raw is not null)
                {
                    // 3a. revit_launch / revit_wait_ready on a Revit THIS gateway launched: a harmless dialog with a safe-dismiss
                    //     button (Close / Cancel / Later, SafeDismissPolicy) is pressed instead of stopping. Twice per window at most.
                    var stuckHwnd = stuck.Raw.Window.Hwnd;
                    if (options.SafeDismissFor is { Length: > 0 } operation && options.ApplyRules &&
                        stuck.Raw.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow &&
                        dismissAttempts.GetValueOrDefault(stuckHwnd) < 2)
                    {
                        dismissAttempts[stuckHwnd] = dismissAttempts.GetValueOrDefault(stuckHwnd) + 1;
                        var dismissed = TrySafeDismiss(pid, stuck.Raw, operation, evidenceDir: options.EvidenceDir, shots: shots);
                        if (dismissed is not null)
                        {
                            handled.Add(dismissed);
                            cache.Remove(stuckHwnd);
                            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }

                    var shot = Capture(pid, stuck.Raw.Window, stuck.Raw.Kind, options.EvidenceDir);
                    AddShot(shots, shot);
                    var blockers = WithShot(last, stuck.Blocker.Hwnd, shot);
                    var (code, reason, next) = DescribeStuck(stuck.Blocker, stuck.Raw.Kind);
                    return Result(pid, false, stuck.Raw.Kind == BlockerKinds.Hung ? RevitStates.NotResponding : RevitStates.Modal, code, reason, next, handled, blockers, shots, wall, pipe, rules);
                }
            }

            if (sw.Elapsed >= timeout)
            {
                var first = snap.Blockers.FirstOrDefault();
                var blockers = last.ToList();
                if (first is not null)
                {
                    var shot = Capture(pid, first.Window, "timeout", options.EvidenceDir);
                    AddShot(shots, shot);
                    blockers = WithShot(blockers, first.Window.HwndHex, shot).ToList();
                }

                var why = snap.Blockers.Count > 0 ? $"{snap.Blockers.Count} blocker(s): '{snap.Blockers[0].Window.Title}' ({snap.Blockers[0].Kind})"
                    : snap.Main is null ? "no main window yet (still starting?)"
                    : NeedPipe() && pipe == false ? "the RevitAgent Bridge pipe did not appear (add-in not loaded?)"
                    : "state did not stay clear for the settle time";
                return Result(pid, false, RevitStates.Timeout, GatewayErrorCodes.TargetNotReady, $"Not ready after {timeout.TotalSeconds:0} s: {why}.",
                    "Call revit_get_state to see the current state; inspect blockers with revit_list_dialogs; retry revit_wait_ready with a longer timeout if Revit is still loading.",
                    handled, blockers, shots, wall, pipe, rules);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public DialogClickResult HandleDialog(int pid, IntPtr hwnd, string button, bool discardChanges, TimeSpan verifyTimeout, string by = "revit_handle_dialog")
    {
        if (!Win32Windows.IsProcessAlive(pid))
        {
            throw GatewayException.NotFound($"Process {pid} is not running. Call revit_list_instances.");
        }

        if (!Win32Windows.Exists(hwnd) || Win32Windows.ProcessIdOf(hwnd) != pid)
        {
            throw GatewayException.NotFound($"Window {Win32Windows.FormatHwnd(hwnd)} does not exist or does not belong to pid {pid}. Call revit_list_dialogs for current dialogs.");
        }

        var snap = Detect(pid);
        var raw = snap.Blockers.FirstOrDefault(b => b.Window.Hwnd == hwnd && b.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow);
        if (raw is null)
        {
            throw GatewayException.NotFound(
                $"Window {Win32Windows.FormatHwnd(hwnd)} is not a blocking dialog of pid {pid} right now.",
                new { current_blockers = snap.Blockers.Select(b => new { hwnd = b.Window.HwndHex, title = b.Window.Title, kind = b.Kind }).ToArray() });
        }

        var rules = _rules.Load();
        var blocker = Describe(raw, snap.ProcessName, rules, includeDetails: true, out var details);
        details ??= new DialogDetails { Source = "title_only" };
        var matches = ButtonMatcher.Find(details, button);
        var available = details.Buttons.Select(b => new { text = b.Text, id = b.Id, enabled = b.Enabled }).ToArray();
        if (matches.Count == 0)
        {
            throw GatewayException.NotFound(
                details.Buttons.Count == 0
                    ? $"No clickable button could be read from '{raw.Window.Title}' ({details.Source}{(details.Note is null ? string.Empty : "; " + details.Note)}). Use ui_snapshot / ui_click on this window instead."
                    : $"Button '{button}' not found on '{raw.Window.Title}'. Use one of the listed buttons (text or id).",
                new { hwnd = raw.Window.HwndHex, buttons = available });
        }

        if (matches.Count > 1)
        {
            throw GatewayException.Validation(
                $"Button '{button}' is ambiguous on '{raw.Window.Title}' ({matches.Count} matches). Pass the button id instead.",
                new { hwnd = raw.Window.HwndHex, buttons = available });
        }

        var target = matches[0];
        var verdict = Verdict(raw.Window, details);
        if (verdict.IsForbidden)
        {
            // Discarding changes is only honored in a Revit the gateway launched: another Revit may hold the user's work.
            var owned = _launched.IsOwned(pid);
            var allowed = discardChanges && owned && verdict.DiscardExceptionAllowed && details is { Complete: true } && DialogSafety.IsDontSaveButton(target);
            if (!allowed)
            {
                var hint = !verdict.DiscardExceptionAllowed ? string.Empty
                    : !owned ? " discard_changes is only honored for a Revit the gateway launched (revit_launch); this one may hold the user's unsaved work."
                    : discardChanges ? " With discard_changes:true only the don't-save button (No / Don't save) may be clicked."
                    : " If the document is a sandbox copy and the user agreed to lose its changes, call again with the don't-save button and discard_changes:true.";
                throw GatewayException.Denied(
                    $"Dialog '{raw.Window.Title}' is in a forbidden category ({verdict.Reason}): a human must answer it. The gateway never clicks it — tell the user and ask them to answer it in Revit.{hint}",
                    new { hwnd = raw.Window.HwndHex, title = raw.Window.Title, text = blocker.Text, category = blocker.Category, buttons = available });
            }
        }

        if (!target.Enabled)
        {
            throw new GatewayException(GatewayErrorCodes.Conflict, $"Button '{target.Text}' is disabled on '{raw.Window.Title}'.", new { hwnd = raw.Window.HwndHex, buttons = available });
        }

        var handled = Click(raw.Window, target, by, verifyTimeout);
        _events.RaiseStatus("dialog", $"pid {pid}: '{raw.Window.Title}' → '{target.Text}' ({by}, {handled.Method}{(handled.DialogClosed ? string.Empty : ", still open")})");
        var after = Detect(pid);
        return new DialogClickResult
        {
            Pid = pid,
            Handled = handled,
            RemainingBlockers = after.Blockers.Select(b => new Blocker { Hwnd = b.Window.HwndHex, Kind = b.Kind, Title = b.Window.Title, Class = b.Window.ClassName }).ToArray(),
        };
    }

    public void EnsureNoBlockers(int pid)
    {
        var snap = Detect(pid);
        if (!snap.Alive)
        {
            throw GatewayException.NotFound($"Process {pid} is not running. Call revit_list_instances.");
        }

        if (snap.Blockers.Count > 0)
        {
            var b = snap.Blockers[0];
            throw new GatewayException(
                b.Kind == BlockerKinds.Hung ? GatewayErrorCodes.TargetNotReady : GatewayErrorCodes.ModalBlocked,
                b.Kind == BlockerKinds.Hung
                    ? $"Revit (pid {pid}) is not responding. Retry in a few seconds."
                    : $"Revit (pid {pid}) is blocked by '{b.Window.Title}' ({b.Kind}). Inspect it with revit_list_dialogs.",
                new { blockers = snap.Blockers.Select(x => new { hwnd = x.Window.HwndHex, title = x.Window.Title, kind = x.Kind }).ToArray() });
        }

        if (snap.Main is null)
        {
            throw GatewayException.NotReady($"Revit (pid {pid}) has no main window yet (still starting?). Retry in a few seconds.");
        }
    }

    public IDisposable AcquireActionLock(int pid)
    {
        var gate = _locks.GetOrAdd(pid, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
        {
            throw new GatewayException(GatewayErrorCodes.Busy, $"Another gateway call is acting on pid {pid} (wait/close/dialog). Retry when it finishes.");
        }

        return new Releaser(gate);
    }

    /// <summary>Saves a PNG of <paramref name="window"/> (5 s limit; PrintWindow may stall on a hung target). Returns the path or null.</summary>
    public string? Capture(int pid, WindowInfo window, string tag, string? dir = null)
    {
        // No explicit folder: the current run of that Revit (its model copy was opened there), else captures\dialogs.
        var folder = dir ?? _evidence.EvidenceDirFor(null, pid) ?? Path.Combine(_paths.CapturesDir, "dialogs");
        var safeTag = Regex.Replace(tag, "[^A-Za-z0-9_.-]", "_");
        var path = EvidenceService.FindEvidenceDir(Path.Combine(Path.GetFullPath(folder), "x")) is not null
            ? Path.Combine(folder, EvidenceService.FileName(EvidenceService.CurrentCaller?.Tool ?? "gateway", $"dialog-{safeTag}-pid{pid}-{window.HwndHex}", ".png"))
            : Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-pid{pid}-{window.HwndHex}-{safeTag}.png");
        var saved = 0;
        var thread = new Thread(() =>
        {
            try
            {
                // Written to a temp name and renamed, so a capture abandoned on timeout never yields a partial PNG.
                var png = WindowCapture.CapturePng(window.Hwnd);
                if (png is null)
                {
                    return;
                }

                Directory.CreateDirectory(folder);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, png);
                File.Move(tmp, path, overwrite: true);
                Interlocked.Exchange(ref saved, 1);
            }
            catch (Exception)
            {
                // Evidence is best effort.
            }
        })
        { IsBackground = true, Name = "RevitAgent blocker screenshot" };
        thread.Start();
        if (thread.Join(ScreenshotTimeout) && saved == 1)
        {
            _evidence.Record(path, "dialog", pid: pid);
            return path;
        }

        return null;
    }

    /// <summary>Reads details, classifies and matches rules for one raw blocker.</summary>
    public Blocker Describe(RawBlocker raw, string? processName, DialogRuleSet rules, bool includeDetails, out DialogDetails? details)
    {
        details = null;
        var w = raw.Window;
        var isDialogLike = raw.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow;
        if (!isDialogLike)
        {
            return new Blocker
            {
                Hwnd = w.HwndHex,
                Kind = raw.Kind,
                Title = w.Title,
                Class = w.ClassName,
                Category = DialogCategories.Unknown,
                Note = raw.Kind == BlockerKinds.Hung
                    ? "Main window is Not Responding (busy with a long operation, or hung)."
                    : "Main window is disabled but no modal window could be identified (try ui_snapshot / ui_screenshot).",
            };
        }

        if (includeDetails)
        {
            details = DialogInspector.Inspect(w, UiaTimeout);
        }

        var texts = details?.Texts ?? Array.Empty<string>();
        var verdict = details is null ? DialogSafety.Classify(w.Title, null) : Verdict(w, details);

        // A rule only applies to a fully read dialog: a forbidden word could hide in the unread part.
        var rule = verdict.IsForbidden || details is not { Complete: true } ? null : rules.Match(processName, w.Title, texts);
        var joined = texts.Count == 0 ? null : string.Join("\n", texts);
        if (joined is { Length: > 2000 })
        {
            joined = joined[..2000] + "…";
        }

        return new Blocker
        {
            Hwnd = w.HwndHex,
            Kind = raw.Kind,
            Title = w.Title,
            Class = w.ClassName,
            Text = joined,
            Buttons = details?.Buttons,
            Category = verdict.IsForbidden ? DialogCategories.Forbidden : rule is not null ? DialogCategories.Info : DialogCategories.Unknown,
            ForbiddenReason = verdict.Reason,
            SavePrompt = verdict.IsSavePrompt ? true : null,
            MatchedRule = rule?.Id,
            IsTaskDialog = details?.IsTaskDialog,
            DetailsSource = details?.Source ?? "title_only",
            OwnerHwnd = w.IsOwned ? Win32Windows.FormatHwnd(w.Owner) : null,
            Note = details?.Note,
        };
    }

    internal RevitState ToState(RevitSnapshot snap, IReadOnlyList<Blocker> blockers, DialogRuleSet rules) => new()
    {
        Pid = snap.Pid,
        ProcessAlive = snap.Alive,
        MainWindowHwnd = snap.Main?.HwndHex,
        MainWindowTitle = snap.Main?.Title,
        Responding = snap.Main is null ? null : !snap.Main.Hung,
        MainWindowEnabled = snap.Main?.Enabled,
        Blockers = blockers,
        Blocked = snap.IsBlocked,
        Ready = snap.IsClear,
        State = snap.State,
        BridgePipeAvailable = snap.Alive ? BridgePipes.IsAvailable(snap.Pid) : null,
        LaunchedByGateway = snap.Alive && _launched.IsOwned(snap.Pid),
        RuleErrors = rules.Errors.Count > 0 ? rules.Errors : null,
    };

    /// <inheritdoc />
    public IReadOnlyList<HandledDialog> AnswerRuledDialogs(int pid, IReadOnlySet<string>? skipHwnds = null)
    {
        var snap = Detect(pid);
        if (!snap.Alive || snap.Blockers.Count == 0)
        {
            return Array.Empty<HandledDialog>();
        }

        var rules = _rules.Load();
        var handled = new List<HandledDialog>();
        foreach (var raw in snap.Blockers)
        {
            if (skipHwnds?.Contains(raw.Window.HwndHex) == true)
            {
                continue;
            }

            var blocker = Describe(raw, snap.ProcessName, rules, includeDetails: true, out var details);
            if (blocker.Category != DialogCategories.Info || details is not { Complete: true })
            {
                continue;
            }

            var rule = rules.Match(snap.ProcessName, raw.Window.Title, details.Texts);
            if (rule is not null)
            {
                handled.Add(ApplyRule(pid, raw.Window, details, rule));
            }
        }

        return handled;
    }

    /// <inheritdoc />
    public IReadOnlyList<HandledDialog> SafeDismissDialogs(int pid, string operation, IReadOnlySet<string>? skipHwnds = null)
    {
        if (!_launched.IsOwned(pid))
        {
            return Array.Empty<HandledDialog>();
        }

        var snap = Detect(pid);
        var handled = new List<HandledDialog>();
        foreach (var raw in snap.Blockers.Where(b => b.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow))
        {
            if (skipHwnds?.Contains(raw.Window.HwndHex) != true && TrySafeDismiss(pid, raw, operation) is { } dismissed)
            {
                handled.Add(dismissed);
            }
        }

        return handled;
    }

    /// <summary>
    /// Presses the safe-dismiss button of one dialog that blocks the gateway operation <paramref name="operation"/>, when
    /// <see cref="SafeDismissPolicy"/> allows it (Revit launched by this gateway, harmless title / text / enabled buttons, one
    /// Close / Cancel / Later-type button). Each press saves a screenshot, writes the audit entry "safe_dismiss" (pid, title,
    /// text, button, operation), raises a dashboard status line and notifies the owner (Telegram, notify only) when configured.
    /// Returns the press (also when the click failed: see <see cref="HandledDialog.Error"/>), or null when nothing was pressed.
    /// </summary>
    /// <param name="details">Details read just before (re-read when null).</param>
    /// <param name="allowCancel">False for revit_close: Cancel on a closing question cancels the close.</param>
    public HandledDialog? TrySafeDismiss(
        int pid, RawBlocker raw, string operation, DialogDetails? details = null, bool allowCancel = true, string? evidenceDir = null, List<string>? shots = null)
    {
        var window = raw.Window;
        var owned = _launched.IsOwned(pid);
        if (!owned || string.IsNullOrWhiteSpace(operation) || raw.Kind is not (BlockerKinds.Dialog or BlockerKinds.ModalWindow) ||
            Win32Windows.ProcessIdOf(window.Hwnd) != pid || Win32Windows.Describe(window.Hwnd)?.Title != window.Title)
        {
            return null;
        }

        details ??= DialogInspector.Inspect(window, UiaTimeout);
        var button = SafeDismissPolicy.Pick(window.Title, details, owned, blocksGatewayOperation: true, allowCancel);
        if (button is null)
        {
            return null;
        }

        var started = Stopwatch.StartNew();
        var shot = Capture(pid, window, "safe_dismiss", evidenceDir);
        if (shots is not null)
        {
            AddShot(shots, shot);
        }

        var text = string.Join("\n", details.Texts);
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars] + "…";
        }

        var info = new SafeDismissInfo { Operation = operation, Text = text.Length > 0 ? text : null, ScreenshotPath = shot };
        var clicked = Click(window, button, "safe_dismiss: " + operation, TimeSpan.FromSeconds(3)) with { SafeDismiss = info };
        _events.RaiseStatus("safe_dismiss", $"pid {pid}: '{window.Title}' → '{button.Text}' to unblock {operation}" +
            (clicked.Error is null ? string.Empty : $" — click failed: {clicked.Error}"));
        try
        {
            _audit.Record(new AuditEntry(
                DateTimeOffset.Now,
                Guid.NewGuid().ToString("N")[..12],
                "gateway",
                "safe_dismiss",
                "Interact",
                GatewayJson.Serialize(new { pid, title = window.Title, text, button = button.Text, button_id = button.Id, operation, dialog_closed = clicked.DialogClosed, screenshot = shot }),
                clicked.Error is null,
                clicked.Error is null ? null : "CLICK_FAILED",
                started.ElapsedMilliseconds,
                $"Gateway pressed a safe-dismiss button to unblock {operation}"));
        }
        catch (Exception)
        {
            // Audit must never break the gate.
        }

        NotifySafeDismiss(pid, window.Title, button.Text, operation, text, shot);
        return clicked;
    }

    /// <summary>Tells the owner (notify only, never asks) that the gateway dismissed a dialog. Fire and forget; failures go to the diagnostics log.</summary>
    private void NotifySafeDismiss(int pid, string title, string button, string operation, string text, string? shot)
    {
        if (_notifier is null)
        {
            return;
        }

        var year = _launched.Get(pid)?.Year;
        var body = text.Length > MaxNotifyTextChars ? text[..MaxNotifyTextChars] + "…" : text;
        var message = $"Revit pid {pid}{(year is null ? string.Empty : $" ({year})")}: gateway đã bấm '{button}' trên hộp thoại '{title}' để tiếp tục {operation}." +
            (body.Length > 0 ? "\n\n" + body : string.Empty);
        var images = shot is not null ? new[] { shot } : null;
        var notifier = _notifier;
        _ = Task.Run(async () =>
        {
            try
            {
                await notifier.NotifyAsync(NotificationKind.DialogDismissed, message, images).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _audit.Diagnostic("Safe-dismiss notification failed", ex);
            }
        });
    }

    /// <summary>Wall clock minus the time spent waiting for the owner (escalations).</summary>
    private sealed class PausableClock(Stopwatch wall, Func<TimeSpan> paused)
    {
        public TimeSpan Elapsed => wall.Elapsed - paused();
    }

    /// <summary>Title, texts and ENABLED buttons (a disabled "Delete Checked..." does not make Review Warnings forbidden).</summary>
    private static DialogSafetyVerdict Verdict(WindowInfo w, DialogDetails d) => DialogSafety.ClassifyDialog(w.Title, d);

    private HandledDialog ApplyRule(int pid, WindowInfo window, DialogDetails details, DialogRule rule)
    {
        var matches = ButtonMatcher.Find(details, rule.Press);
        if (matches.Count != 1 || !matches[0].Enabled)
        {
            var why = matches.Count == 0
                ? details.Buttons.Count == 0 ? $"no buttons readable ({details.Source}{(details.Note is null ? string.Empty : "; " + details.Note)})" : $"'{rule.Press}' not among the buttons"
                : matches.Count > 1 ? $"'{rule.Press}' is ambiguous" : $"button '{matches[0].Text}' is disabled";
            return new HandledDialog { Hwnd = window.HwndHex, Title = window.Title, Button = rule.Press, By = "rule: " + rule.Id, Method = "none", DialogClosed = false, Error = why };
        }

        var handled = Click(window, matches[0], "rule: " + rule.Id, TimeSpan.FromSeconds(3));
        _events.RaiseStatus("dialog", $"pid {pid}: rule '{rule.Id}' answered '{window.Title}' with '{matches[0].Text}'");
        return handled;
    }

    /// <summary>
    /// Answers the unsigned-add-in prompt from the allowlist. Handled = what was clicked; Retry = the dialog could not be read
    /// completely yet (try again shortly); both null/false = leave it to a human (parse failure, other forbidden words, no such button).
    /// </summary>
    private (HandledDialog? Handled, bool Retry) TryAnswerUnsignedAddin(int pid, RawBlocker raw, string? processName, DialogRuleSet rules, ReadinessOptions options, List<string> shots)
    {
        var window = raw.Window;
        Describe(raw, processName, rules, includeDetails: true, out var details);
        if (details is not { Complete: true } || details.Texts.Count == 0)
        {
            return (null, true);
        }

        // Nothing but the security category may be involved (a forbidden word elsewhere, e.g. a license or delete prompt, stays human-only).
        var verdict = Verdict(window, details);
        var everything = string.Join(Environment.NewLine, new[] { window.Title }.Concat(details.Texts).Concat(details.Buttons.Select(b => Win32Windows.StripMnemonic(b.Text))));
        if (verdict.IsSavePrompt || DialogSafety.FindForbiddenCategories(everything).Any(c => c != "security"))
        {
            return (null, false);
        }

        var decision = _allowlist.Decide(details.Texts);
        if (decision.Action == AddinTrustAction.NoDecision)
        {
            return (null, false);
        }

        var buttons = details.Buttons.Where(b => b.Kind == DialogButtonKind.TaskDialog && string.Equals(b.Id, decision.ButtonId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (buttons.Count != 1 || !buttons[0].Enabled)
        {
            return (null, false);
        }

        var started = Stopwatch.StartNew();
        var shot = Capture(pid, window, "addin_trust", options.EvidenceDir);
        AddShot(shots, shot);
        var info = new AddinTrustInfo { Name = decision.Name, Location = decision.Location, MatchedEntryId = decision.MatchedEntryId, Decision = decision.Code };
        var clicked = Click(window, buttons[0], "allowlist: " + decision.Label, TimeSpan.FromSeconds(3)) with { AddinTrust = info };

        _events.RaiseStatus("addin_trust", $"pid {pid}: unsigned add-in '{decision.Name}' ({decision.Location}) → {decision.Label}" +
            (decision.MatchedEntryId is null ? " (not on the allowlist)" : $" (allowlist entry {decision.MatchedEntryId})") + (clicked.Error is null ? string.Empty : $" — click failed: {clicked.Error}"));
        _events.RaiseAddinTrust(new AddinTrustEvent(pid, decision.Name ?? string.Empty, decision.Location ?? string.Empty, decision.Code, shot));
        try
        {
            _audit.Record(new AuditEntry(
                DateTimeOffset.Now,
                Guid.NewGuid().ToString("N")[..12],
                "gateway",
                "addin_trust",
                "Interact",
                GatewayJson.Serialize(new { pid, name = decision.Name, location = decision.Location, matched_entry_id = decision.MatchedEntryId, button = decision.Label, button_id = decision.ButtonId, screenshot = shot }),
                clicked.Error is null,
                clicked.Error is null ? null : "CLICK_FAILED",
                started.ElapsedMilliseconds,
                decision.Reason));
        }
        catch (Exception)
        {
            // Audit must never break the gate.
        }

        return (clicked, false);
    }

    private sealed record EscalationOutcome(bool Resolved, HandledDialog? Handled);

    private static readonly TimeSpan MinAskTime = TimeSpan.FromSeconds(3);
    private const int MaxOptions = 6;
    private const int MaxTextChars = 800;
    private const int MaxNotifyTextChars = 600; // a Telegram photo caption holds 1024 characters

    /// <summary>
    /// Puts one forbidden dialog to the owner (Telegram) and presses the owner's choice, or the safe button on timeout.
    /// Resolved = the dialog is gone or was answered (re-observe); not resolved = nothing was pressed, the caller reports MODAL_BLOCKED.
    /// Fails closed: an unreadable dialog, a changed window, an unreachable owner or a missing/ambiguous safe button press nothing.
    /// </summary>
    private async Task<EscalationOutcome> EscalateAsync(
        int pid, RawBlocker raw, string? processName, DialogRuleSet rules, ReadinessOptions options, TimeSpan remaining, List<string> shots, CancellationToken ct)
    {
        var none = new EscalationOutcome(false, null);
        var window = raw.Window;
        Describe(raw, processName, rules, includeDetails: true, out var details);
        if (details is not { Complete: true } || details.Buttons.Count == 0 || remaining < MinAskTime)
        {
            return none;
        }

        var verdict = Verdict(window, details);
        var safe = SafeButtonPolicy.Pick(details.Buttons, verdict.IsSavePrompt, verdict.DiscardExceptionAllowed);

        // Offered labels: enabled, non-empty, unique; at most MaxOptions with the safe one always kept.
        var offered = details.Buttons.Where(b => b.Enabled && SafeButtonPolicy.Normalize(b.Text).Length > 0)
            .GroupBy(b => SafeButtonPolicy.Normalize(b.Text)).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
        if (offered.Count == 0)
        {
            return none;
        }

        if (offered.Count > MaxOptions)
        {
            var keep = new List<DialogButton>();
            if (safe is not null && offered.Contains(safe))
            {
                keep.Add(safe);
            }

            keep.AddRange(offered.Where(b => !keep.Contains(b)).Take(MaxOptions - keep.Count));
            offered = offered.Where(keep.Contains).ToList();
        }

        var safeLabel = safe is not null && offered.Contains(safe) ? safe.Text : null;
        var shot = Capture(pid, window, "escalation", options.EvidenceDir);
        AddShot(shots, shot);
        var text = string.Join("\n", details.Texts);
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars] + "…";
        }

        var year = _launched.Get(pid)?.Year;
        text += $"\n\nRevit pid {pid}{(year is null ? string.Empty : $" ({year})")}. " +
            (safeLabel is null ? "Không có nút an toàn: hết giờ sẽ không bấm gì." : $"Hết giờ sẽ bấm: {safeLabel}.");

        var configured = _escalation!.DefaultTimeout;
        var askTimeout = configured is { } c && c < remaining ? c : remaining;
        var vanished = false;
        var question = new HumanQuestion(window.Title, text, shot, offered.Select(b => b.Text).ToArray(), safeLabel ?? string.Empty, askTimeout)
        {
            CancelNote = () => vanished ? "Đã được xử lý trực tiếp trong Revit." : "Đã hủy câu hỏi (phiên chạy đã dừng).",
        };

        using var askCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ask = _escalation.AskAsync(question, askCts.Token);
        while (!ask.IsCompleted)
        {
            await Task.WhenAny(ask, Task.Delay(PollInterval, CancellationToken.None)).ConfigureAwait(false);
            if (ask.IsCompleted || ct.IsCancellationRequested)
            {
                break;
            }

            if (!Win32Windows.Exists(window.Hwnd) || !Win32Windows.IsVisible(window.Hwnd) || Win32Windows.Describe(window.Hwnd)?.Title != window.Title)
            {
                vanished = true;
                break;
            }
        }

        if (vanished || ct.IsCancellationRequested)
        {
            askCts.Cancel();
            try
            {
                await ask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancelled (expected) or failed: the question is abandoned either way.
            }

            ct.ThrowIfCancellationRequested();
            _events.RaiseStatus("human_dialog", $"pid {pid}: '{window.Title}' was handled directly in Revit; the Telegram question was withdrawn");
            return new EscalationOutcome(true, null);
        }

        HumanAnswer answer;
        try
        {
            answer = await ask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _audit.Diagnostic($"Telegram escalation of '{window.Title}' failed: {ex.Message}");
            return none;
        }
        catch (OperationCanceledException)
        {
            return none;
        }

        // A timed-out answer only counts when the owner actually received the question (not when Telegram is off or the send failed).
        if (answer.TimedOut && answer.Reason != HumanAnswerReasons.Timeout)
        {
            return none;
        }

        var pressLabel = answer.TimedOut ? safeLabel : answer.Choice;
        if (string.IsNullOrEmpty(pressLabel) || !offered.Any(b => SafeButtonPolicy.Normalize(b.Text) == SafeButtonPolicy.Normalize(pressLabel)))
        {
            return none;
        }

        // Re-check right before pressing: the very same window, same title, and the button still unique and enabled.
        var snap = Detect(pid);
        var again = snap.Blockers.FirstOrDefault(b => b.Window.Hwnd == window.Hwnd && b.Kind == raw.Kind);
        if (again is null)
        {
            return new EscalationOutcome(true, null); // gone meanwhile
        }

        if (again.Window.Title != window.Title)
        {
            return none;
        }

        Describe(again, snap.ProcessName, rules, includeDetails: true, out var details2);
        var wanted = SafeButtonPolicy.Normalize(pressLabel);
        var matches = details2 is { Complete: true } ? details2.Buttons.Where(b => SafeButtonPolicy.Normalize(b.Text) == wanted).ToList() : new List<DialogButton>();
        if (matches.Count != 1 || !matches[0].Enabled)
        {
            return none;
        }

        if (answer.TimedOut)
        {
            var verdict2 = Verdict(again.Window, details2!);
            var safe2 = SafeButtonPolicy.Pick(details2!.Buttons, verdict2.IsSavePrompt, verdict2.DiscardExceptionAllowed);
            if (safe2 is null || SafeButtonPolicy.Normalize(safe2.Text) != wanted)
            {
                return none;
            }
        }

        var started = Stopwatch.StartNew();
        var human = new HumanDecision { Choice = matches[0].Text, AnsweredBy = answer.AnsweredBy, TimedOut = answer.TimedOut, AskedAt = answer.AskedAt, AnsweredAt = answer.AnsweredAt };
        var clicked = Click(again.Window, matches[0], answer.TimedOut ? "telegram (timeout, safe button)" : "telegram", TimeSpan.FromSeconds(3)) with { Human = human };
        _events.RaiseStatus("human_dialog", $"pid {pid}: '{window.Title}' → '{matches[0].Text}' ({(answer.TimedOut ? "owner did not answer, safe button" : "answered on Telegram by " + answer.AnsweredBy)})" +
            (clicked.Error is null ? string.Empty : $" — click failed: {clicked.Error}"));
        try
        {
            _audit.Record(new AuditEntry(
                DateTimeOffset.Now,
                Guid.NewGuid().ToString("N")[..12],
                "gateway",
                "human_dialog_decision",
                "Interact",
                GatewayJson.Serialize(new { pid, title = window.Title, choice = matches[0].Text, button_id = matches[0].Id, via = "telegram", answered_by = answer.AnsweredBy, timed_out = answer.TimedOut, asked_at = answer.AskedAt, answered_at = answer.AnsweredAt, screenshot = shot }),
                clicked.Error is null,
                clicked.Error is null ? null : "CLICK_FAILED",
                started.ElapsedMilliseconds,
                answer.TimedOut ? "Owner did not answer: safe button" : "Owner's Telegram answer"));
        }
        catch (Exception)
        {
            // Audit must never break the gate.
        }

        return new EscalationOutcome(clicked.Error is null, clicked);
    }

    /// <summary>Clicks exactly one button and waits for the dialog to close.</summary>
    private static HandledDialog Click(WindowInfo dialog, DialogButton b, string by, TimeSpan verifyTimeout)
    {
        string method;
        string? error = null;
        switch (b.Kind)
        {
            case DialogButtonKind.Win32 when b.NumericId is { } id && Win32Windows.Exists(b.Hwnd):
                method = "wm_command";
                var parent = NativeMethods.GetParent(b.Hwnd);
                if (!NativeMethods.PostMessage(parent == IntPtr.Zero ? dialog.Hwnd : parent, NativeMethods.WmCommand, NativeMethods.MakeWParam(id, NativeMethods.BnClicked), b.Hwnd))
                {
                    error = "PostMessage(WM_COMMAND) failed";
                }

                break;
            case DialogButtonKind.TaskDialog when b.NumericId is { } tdId:
                method = "tdm_click_button";
                if (!NativeMethods.PostMessage(dialog.Hwnd, NativeMethods.TdmClickButton, (IntPtr)tdId, IntPtr.Zero))
                {
                    error = "PostMessage(TDM_CLICK_BUTTON) failed";
                }

                break;
            case DialogButtonKind.Uia:
                method = "uia_invoke";
                if (!UiaDialogReader.InvokeButton(dialog.Hwnd, b.Id, b.Text, UiaTimeout, out var note))
                {
                    error = note ?? "UI Automation Invoke failed";
                }

                break;
            default:
                method = "bm_click";
                if (b.Hwnd == IntPtr.Zero || !Win32Windows.Exists(b.Hwnd))
                {
                    error = "button window not found";
                }
                else if (!NativeMethods.PostMessage(b.Hwnd, NativeMethods.BmClick, IntPtr.Zero, IntPtr.Zero))
                {
                    error = "PostMessage(BM_CLICK) failed";
                }

                break;
        }

        var closed = error is null && WaitClosed(dialog.Hwnd, verifyTimeout);
        return new HandledDialog
        {
            Hwnd = dialog.HwndHex,
            Title = dialog.Title,
            Button = b.Text,
            ButtonId = b.Id,
            By = by,
            Method = method,
            DialogClosed = closed,
            Error = error,
        };
    }

    private static bool WaitClosed(IntPtr hwnd, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            if (!Win32Windows.Exists(hwnd) || !Win32Windows.IsVisible(hwnd))
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (sw.Elapsed < timeout);

        return !Win32Windows.Exists(hwnd) || !Win32Windows.IsVisible(hwnd);
    }

    private static (string Code, string Reason, string Next) DescribeStuck(Blocker b, string kind) => kind switch
    {
        BlockerKinds.Hung => (GatewayErrorCodes.TargetNotReady,
            $"Revit main window '{b.Title}' is Not Responding (for {b.SeenForSec:0} s).",
            "Revit may be busy with a long operation: call revit_wait_ready again later. If it never recovers, ask the user before closing it (revit_close)."),
        BlockerKinds.Disabled => (GatewayErrorCodes.ModalBlocked,
            $"Revit main window '{b.Title}' is disabled by a modal window that is not a standard dialog (for {b.SeenForSec:0} s).",
            "Find the modal window with revit_list_dialogs / ui_screenshot and close it with the ui_* tools if it is informational; otherwise ask the user."),
        _ => (GatewayErrorCodes.ModalBlocked,
            $"Dialog '{b.Title}' has no rule and is still open after {b.SeenForSec:0} s.",
            "Read it (revit_list_dialogs; screenshot attached). If it is clearly informational, answer it with revit_handle_dialog; if unsure, ask the user. " +
            "A recurring informational dialog can get a rule in %REVITAGENT_HOME%\\config\\dialog-rules.json."),
    };

    private static IReadOnlyList<Blocker> WithShot(IEnumerable<Blocker> blockers, string hwnd, string? shot) =>
        blockers.Select(b => b.Hwnd == hwnd && shot is not null ? b with { ScreenshotPath = shot } : b).ToArray();

    private static void AddShot(List<string> shots, string? shot)
    {
        if (shot is not null)
        {
            shots.Add(shot);
        }
    }

    private static ReadinessResult Result(
        int pid, bool ready, string state, string? code, string? reason, string? next,
        List<HandledDialog> handled, IReadOnlyList<Blocker> blockers, List<string> shots, Stopwatch sw, bool? pipe, DialogRuleSet rules) => new()
    {
        Pid = pid,
        Ready = ready,
        State = state,
        Code = code,
        Reason = reason,
        NextStep = next,
        Handled = handled.ToArray(),
        Blockers = blockers,
        Screenshots = shots.ToArray(),
        WaitedSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
        BridgePipeAvailable = pipe,
        RuleErrors = rules.Errors.Count > 0 ? rules.Errors : null,
    };

    private static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
