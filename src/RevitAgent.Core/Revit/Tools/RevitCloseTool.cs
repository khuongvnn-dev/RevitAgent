using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_close — close a gateway-owned Revit (graceful first, kill after grace).</summary>
public sealed class RevitCloseTool : GatewayTool
{
    private readonly RevitProcessService _processes;

    public RevitCloseTool(RevitProcessService processes) => _processes = processes;

    public override string Name => "revit_close";

    public override string Description =>
        "Close a Revit the gateway launched: WM_CLOSE, wait grace_sec, kill. A save prompt gets No only with discard_changes:true, " +
        "else MODAL_BLOCKED; a harmless dialog gets Close/Later (handled). A Revit the gateway did not launch is refused (PERMISSION_DENIED) " +
        "unless the user explicitly asked: force_foreign + confirm_pid; foreign Revit: owner approval, graceful only. Never saves.";

    public override ToolPermission Permission => ToolPermission.Admin;

    public override TimeSpan Timeout => TimeSpan.FromMinutes(6);

    // A foreign close first waits for the owner's answer (dashboard / Telegram).
    public override TimeSpan TimeoutFor(ToolCall call) =>
        call.GetBool("force_foreign") ? Timeout + Policy.OwnerApprovalRequest.DefaultTimeout + TimeSpan.FromSeconds(15) : Timeout;

    protected override Schema BuildSchema() => Schema.Object()
        .Pid()
        .Boolean("discard_changes", "Do not save at a save prompt (sandbox copies or consent).", defaultValue: false)
        .Integer("grace_sec", "Seconds before kill.", defaultValue: 60, minimum: 1, maximum: 300)
        .Boolean("force_foreign", "Close a Revit the gateway did not launch (user explicitly asked).", defaultValue: false)
        .Integer("confirm_pid", "Equal to pid, with force_foreign.")
        .Boolean("force_kill", "Kill at once (unsaved changes lost).", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var request = new CloseRequest
        {
            Pid = RevitToolHelpers.ResolvePid(call, required: true),
            DiscardChanges = call.GetBool("discard_changes"),
            Grace = RevitToolHelpers.Seconds(call, "grace_sec", 60, 1, 300),
            ForceForeign = call.GetBool("force_foreign"),
            ConfirmPid = call.GetInt("confirm_pid"),
            ForceKill = call.GetBool("force_kill"),
            SessionId = call.SessionId,
        };

        return ToolResult.Json(await _processes.CloseAsync(request, cancellationToken).ConfigureAwait(false));
    }
}
