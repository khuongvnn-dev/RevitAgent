using RevitAgent.Core.Notify;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_launch — start a gateway-owned Revit without a model and run the start readiness gate.</summary>
public sealed class RevitLaunchTool : GatewayTool
{
    private readonly RevitProcessService _processes;
    private readonly IHumanEscalation? _escalation;

    public RevitLaunchTool(RevitProcessService processes, IHumanEscalation? escalation = null)
    {
        _processes = processes;
        _escalation = escalation;
    }

    public override string Name => "revit_launch";

    public override string Description =>
        "Start a NEW gateway-owned Revit without a model and wait until ready; rule-matched info dialogs are answered, " +
        "harmless lingering ones get Close/Cancel/Later (handled). " +
        "Forbidden dialogs (security, license, sign-in, recovery, delete/overwrite/replace) are NEVER answered: ready:false, MODAL_BLOCKED; ask the user. " +
        "Unsigned add-in prompts are answered only by the owner's allowlist (addin_trust). " +
        "Only if the user asked for a new Revit. 30-180 s. Then sandbox_copy_model, doc_open.";

    public override ToolPermission Permission => ToolPermission.Admin;

    public override TimeSpan Timeout => TimeSpan.FromMinutes(15);

    /// <summary>With escalate_dialogs the owner gets the full Telegram question time on top of the start wait.</summary>
    public override TimeSpan TimeoutFor(ToolCall call) =>
        call.GetBool("escalate_dialogs") ? Timeout + ReadinessOptions.HumanWaitBudgetFor(_escalation) : Timeout;

    protected override Schema BuildSchema() => Schema.Object()
        .Integer("year", "Installed year, e.g. 2024.")
        .String("exe_path", "Or an installed Revit.exe path.")
        .StringArray("args", "Extra switches, e.g. [/language, ENU]; no model or journal files.")
        .Integer("timeout_sec", "Max wait (s).", defaultValue: 300, minimum: 10, maximum: 840)
        .Integer("settle_sec", "Seconds the state must stay clear.", defaultValue: 5, minimum: 0, maximum: 60)
        .Boolean("tie_to_gateway", "Kill Revit when the gateway exits.", defaultValue: false)
        .Boolean("wait_for_bridge", "Also wait for the Bridge (default: if installed).")
        .Boolean("escalate_dialogs", "UNATTENDED only: ask the owner on Telegram about forbidden dialogs (safe button on timeout). Interactive agents: human_ask.", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var request = new LaunchRequest
        {
            Year = call.GetInt("year"),
            ExePath = call.GetString("exe_path"),
            Args = call.GetStringList("args"),
            Timeout = RevitToolHelpers.Seconds(call, "timeout_sec", 300, 10, 840),
            Settle = RevitToolHelpers.Seconds(call, "settle_sec", 5, 0, 60),
            TieToGateway = call.GetBool("tie_to_gateway"),
            EscalateDialogs = call.GetBool("escalate_dialogs"),
            WaitForBridge = call.Has("wait_for_bridge") ? call.GetBool("wait_for_bridge") : null,
        };

        var result = await _processes.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        return RevitToolHelpers.AttachScreenshots(ToolResult.Json(result), result.Screenshots);
    }
}
