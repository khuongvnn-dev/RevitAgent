using RevitAgent.Core.Notify;
using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_wait_ready — the readiness gate: wait, answer informational dialogs by rule, stop at forbidden ones.</summary>
public sealed class RevitWaitReadyTool : GatewayTool
{
    private readonly RevitReadiness _readiness;
    private readonly SandboxService _sandbox;
    private readonly LaunchedProcessRegistry _launched;
    private readonly IHumanEscalation? _escalation;

    public RevitWaitReadyTool(RevitReadiness readiness, SandboxService sandbox, LaunchedProcessRegistry launched, IHumanEscalation? escalation = null)
    {
        _readiness = readiness;
        _sandbox = sandbox;
        _launched = launched;
        _escalation = escalation;
    }

    public override string Name => "revit_wait_ready";

    public override string Description =>
        "Wait until Revit is ready: main window present, enabled and responding, no dialog, stable for settle_sec. Dialogs matching an informational rule " +
        "(built-in Revit startup rules + %REVITAGENT_HOME%\\config\\dialog-rules.json) are answered with exactly the rule's button and listed in handled[]. " +
        "Unsigned add-in prompts of a Revit the gateway launched are answered only by the owner's allowlist (addin_trust). " +
        "In a Revit the gateway launched, a harmless dialog (no forbidden or destructive words) still open after the grace below gets its Close / Cancel / Later button pressed (handled[]). " +
        "Errors: MODAL_BLOCKED immediately when a forbidden dialog appears (security / unsigned add-in not on that path, license, sign-in, recovery, delete / overwrite / replace, save prompt — " +
        "do NOT answer it, ask the user; screenshot path in details), or when an unknown dialog stays open longer than max(10 s, 2×settle_sec) (inspect with revit_list_dialogs, " +
        "answer with revit_handle_dialog if informational); TARGET_NOT_READY on timeout or when Revit stays Not Responding. Use before acting and after long operations.";

    public override ToolPermission Permission => ToolPermission.Interact;

    public override TimeSpan Timeout => TimeSpan.FromMinutes(11);

    /// <summary>With escalate_dialogs the owner gets the full Telegram question time on top of the Revit wait.</summary>
    public override TimeSpan TimeoutFor(ToolCall call) =>
        call.GetBool("escalate_dialogs") ? Timeout + ReadinessOptions.HumanWaitBudgetFor(_escalation) : Timeout;

    protected override Schema BuildSchema() => Schema.Object()
        .Pid()
        .Integer("timeout_sec", "Maximum wait. Default 300 for phase=start, 120 for phase=step.", minimum: 1, maximum: 600)
        .Integer("settle_sec", "The state must stay clear this long; also the grace for transient dialogs.", defaultValue: 5, minimum: 0, maximum: 60)
        .String("phase", "start = right after Revit started (before opening a document); step = between actions.", enumValues: new[] { "start", "step" }, defaultValue: "step")
        .Boolean("apply_rules", "Answer dialogs that match an informational rule. false = only observe.", defaultValue: true)
        .Boolean("require_bridge", "Also wait for the RevitAgent Bridge pipe of this process.", defaultValue: false)
        .String("run_id", "Save blocker screenshots into this sandbox run's evidence folder.")
        .Boolean("escalate_dialogs", "UNATTENDED runs only: if a forbidden dialog (license, sign-in, delete / overwrite / replace, save prompt, …) appears in this Revit, the gateway asks the owner on Telegram (screenshot + the dialog's buttons); the owner answers there or clicks in Revit himself; on timeout the SAFE button (Cancel / Do Not Load / No / Close) is pressed. Default false: an interactive agent should ask the human itself with human_ask.", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = RevitToolHelpers.ResolvePid(call, required: true);
        var phase = (call.GetString("phase", "step") ?? "step").Trim().ToLowerInvariant() switch
        {
            "start" => ReadinessPhase.Start,
            "step" => ReadinessPhase.Step,
            var other => throw GatewayException.Validation($"phase must be 'start' or 'step' (got '{other}')."),
        };
        var options = new ReadinessOptions
        {
            Phase = phase,
            Timeout = RevitToolHelpers.Seconds(call, "timeout_sec", phase == ReadinessPhase.Start ? 300 : 120, 1, 600),
            Settle = RevitToolHelpers.Seconds(call, "settle_sec", 5, 0, 60),
            ApplyRules = call.GetBool("apply_rules", true),
            RequireBridgePipe = call.GetBool("require_bridge"),
            EscalateForbiddenDialogs = call.GetBool("escalate_dialogs"),
            EvidenceDir = RevitToolHelpers.EvidenceDir(call, _sandbox) ?? _sandbox.Evidence.EvidenceDirFor(call.SessionId, pid),
            SafeDismissFor = Name,
        };

        RevitToolHelpers.EnsureRevitTarget(pid, _launched);
        using var actionLock = _readiness.AcquireActionLock(pid);
        var result = await _readiness.WaitReadyAsync(pid, options, cancellationToken).ConfigureAwait(false);
        if (!result.Ready)
        {
            throw new GatewayException(result.Code ?? Contracts.Protocol.GatewayErrorCodes.TargetNotReady, $"{result.Reason} {result.NextStep}".Trim(), result);
        }

        return ToolResult.Json(result);
    }
}
