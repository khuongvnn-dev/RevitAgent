using System.Text.Json.Serialization;

namespace RevitAgent.Core.Revit;

/// <summary>How a button can be clicked precisely.</summary>
public enum DialogButtonKind
{
    /// <summary>Classic Win32 button with a real control id (WM_COMMAND/BN_CLICKED to its parent).</summary>
    Win32,

    /// <summary>TaskDialog button: id from the UIA AutomationId CommandButton_&lt;n&gt; / CommandLink_&lt;n&gt; (TDM_CLICK_BUTTON).</summary>
    TaskDialog,

    /// <summary>TaskDialog/DirectUI button window without a known TaskDialog id (BM_CLICK on that exact window).</summary>
    Win32Window,

    /// <summary>Button only reachable through UI Automation (WPF and other windowless UIs): Invoke pattern.</summary>
    Uia,
}

/// <summary>One clickable button of a dialog.</summary>
public sealed record DialogButton
{
    /// <summary>Label without accelerator markers ("&amp;Yes" → "Yes").</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Identifier usable as the <c>button</c> argument of revit_handle_dialog: Win32 control id ("6"),
    /// TaskDialog id ("CommandButton_8", "CommandLink_1002") or UIA AutomationId. Null when there is none.
    /// </summary>
    public string? Id { get; init; }

    public bool Enabled { get; init; } = true;

    [JsonIgnore]
    public DialogButtonKind Kind { get; init; }

    [JsonIgnore]
    public IntPtr Hwnd { get; init; }

    /// <summary>TaskDialog button id (for TDM_CLICK_BUTTON) or Win32 control id.</summary>
    [JsonIgnore]
    public int? NumericId { get; init; }
}

/// <summary>What the gateway could read from a dialog window.</summary>
public sealed record DialogDetails
{
    public IReadOnlyList<string> Texts { get; init; } = Array.Empty<string>();

    public IReadOnlyList<DialogButton> Buttons { get; init; } = Array.Empty<DialogButton>();

    /// <summary>comctl32 TaskDialog (#32770 hosting a DirectUIHWND) — what Revit's TaskDialog uses.</summary>
    public bool IsTaskDialog { get; init; }

    /// <summary>win32 | uia | win32+uia | title_only (UI Automation unavailable or timed out).</summary>
    public string Source { get; init; } = "win32";

    /// <summary>
    /// False when some content could not be read (time budget, a control that did not answer, UIA timeout).
    /// Rules are never applied to incompletely read dialogs: a forbidden word could be in the unread part.
    /// </summary>
    public bool Complete { get; init; } = true;

    /// <summary>Non-fatal problems while reading (e.g. "UI Automation timed out after 2.5 s").</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The dialog shows a progress bar (Win32 msctls_progress32 or a UIA ProgressBar): work is under way, so its Cancel
    /// would abort that work. The gateway never safe-dismisses such a dialog (<see cref="SafeDismissPolicy"/>).
    /// </summary>
    public bool HasProgressBar { get; init; }
}

/// <summary>Kinds of blockers found by the readiness gate.</summary>
public static class BlockerKinds
{
    /// <summary>Visible #32770 window (MessageBox, Revit TaskDialog, classic dialog).</summary>
    public const string Dialog = "dialog";

    /// <summary>Other enabled window while the main window is disabled (WPF/WinForms modal of an add-in).</summary>
    public const string ModalWindow = "modal_window";

    /// <summary>Main window disabled but no modal window could be identified.</summary>
    public const string Disabled = "disabled";

    /// <summary>Main window "Not Responding" (IsHungAppWindow).</summary>
    public const string Hung = "hung";
}

/// <summary>Safety category of a blocker.</summary>
public static class DialogCategories
{
    /// <summary>Matches an informational dialog rule: the gate may answer it automatically.</summary>
    public const string Info = "info";

    /// <summary>Security / license / sign-in / recovery / delete / overwrite / replace / save prompt: a human must answer.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>No rule: the agent may inspect it and answer with revit_handle_dialog if it is clearly informational.</summary>
    public const string Unknown = "unknown";
}

/// <summary>One thing that keeps Revit from being ready.</summary>
public sealed record Blocker
{
    public string Hwnd { get; init; } = string.Empty;

    public string Kind { get; init; } = BlockerKinds.Dialog;

    public string Title { get; init; } = string.Empty;

    public string Class { get; init; } = string.Empty;

    /// <summary>Joined static texts / content (truncated).</summary>
    public string? Text { get; init; }

    public IReadOnlyList<DialogButton>? Buttons { get; init; }

    public string Category { get; init; } = DialogCategories.Unknown;

    /// <summary>Why the category is forbidden (e.g. "matches forbidden category 'security'").</summary>
    public string? ForbiddenReason { get; init; }

    /// <summary>True for a "save changes?" prompt (answerable only with discard_changes:true and the don't-save button).</summary>
    public bool? SavePrompt { get; init; }

    /// <summary>Name of the informational rule that matches it.</summary>
    public string? MatchedRule { get; init; }

    public bool? IsTaskDialog { get; init; }

    public string? DetailsSource { get; init; }

    public string? OwnerHwnd { get; init; }

    public string? ScreenshotPath { get; init; }

    /// <summary>Seconds this blocker has been observed during the current wait (wait/launch only).</summary>
    public double? SeenForSec { get; init; }

    public string? Note { get; init; }
}

/// <summary>A dialog the gateway answered (rule, revit_handle_dialog, revit_close with discard_changes).</summary>
public sealed record HandledDialog
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public string Hwnd { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Button { get; init; } = string.Empty;

    public string? ButtonId { get; init; }

    /// <summary>Rule name, "revit_handle_dialog", or "revit_close(discard_changes)".</summary>
    public string By { get; init; } = string.Empty;

    /// <summary>wm_command | tdm_click_button | bm_click | uia_invoke.</summary>
    public string Method { get; init; } = string.Empty;

    public bool DialogClosed { get; init; }

    public string? Error { get; init; }

    /// <summary>Set when the gateway answered the unsigned-add-in prompt from the allowlist (name, location, matched entry, decision).</summary>
    public AddinTrustInfo? AddinTrust { get; init; }

    /// <summary>Set when the owner decided (or the safe-button timeout did) via Telegram escalation of a forbidden dialog.</summary>
    public HumanDecision? Human { get; init; }

    /// <summary>Set when the gateway pressed a safe-dismiss button itself to unblock one of its own operations (<see cref="SafeDismissPolicy"/>).</summary>
    public SafeDismissInfo? SafeDismiss { get; init; }
}

/// <summary>Why and how the gateway safe-dismissed a dialog of a Revit it launched (audit kind "safe_dismiss").</summary>
public sealed record SafeDismissInfo
{
    /// <summary>The gateway operation the dialog blocked: revit_close | doc_open | revit_launch | revit_wait_ready.</summary>
    public string Operation { get; init; } = string.Empty;

    /// <summary>The dialog's message text (truncated).</summary>
    public string? Text { get; init; }

    public string? ScreenshotPath { get; init; }
}

/// <summary>How a forbidden dialog of a gateway-launched Revit was answered through the owner's Telegram chat.</summary>
public sealed record HumanDecision
{
    /// <summary>Always "telegram".</summary>
    public string Via { get; init; } = "telegram";

    public string Choice { get; init; } = string.Empty;

    public string AnsweredBy { get; init; } = string.Empty;

    /// <summary>True when the owner did not answer and the safe button was pressed.</summary>
    public bool TimedOut { get; init; }

    public DateTimeOffset AskedAt { get; init; }

    public DateTimeOffset AnsweredAt { get; init; }
}

/// <summary>Readiness snapshot of one Revit process (revit_get_state).</summary>
public sealed record RevitState
{
    public int Pid { get; init; }

    public bool ProcessAlive { get; init; }

    public string? MainWindowHwnd { get; init; }

    public string? MainWindowTitle { get; init; }

    /// <summary>False when the main window is "Not Responding" (IsHungAppWindow); null without a main window.</summary>
    public bool? Responding { get; init; }

    public bool? MainWindowEnabled { get; init; }

    /// <summary>Every blocker, including a disabled or hung main window.</summary>
    public IReadOnlyList<Blocker> Blockers { get; init; } = Array.Empty<Blocker>();

    /// <summary>The dialogs / modal windows among <see cref="Blockers"/> (each with hwnd, title, class, text, category, buttons).</summary>
    public IReadOnlyList<Blocker> BlockingDialogs => Blockers.Where(b => b.Kind is BlockerKinds.Dialog or BlockerKinds.ModalWindow).ToArray();

    /// <summary>A dialog, modal window or disabled main window blocks input.</summary>
    public bool Blocked { get; init; }

    /// <summary>Alive, main window present, enabled, responding and no blocker.</summary>
    public bool Ready { get; init; }

    /// <summary>ready | busy (no main window yet) | modal | not_responding | exited.</summary>
    public string State { get; init; } = RevitStates.Ready;

    public bool? BridgePipeAvailable { get; init; }

    public bool LaunchedByGateway { get; init; }

    public IReadOnlyList<string>? RuleErrors { get; init; }
}
