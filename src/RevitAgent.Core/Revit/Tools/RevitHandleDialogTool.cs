using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_handle_dialog — click exactly one button of one blocking dialog; refuses forbidden dialogs.</summary>
public sealed class RevitHandleDialogTool : GatewayTool
{
    private readonly RevitReadiness _readiness;
    private readonly LaunchedProcessRegistry _launched;

    public RevitHandleDialogTool(RevitReadiness readiness, LaunchedProcessRegistry launched)
    {
        _readiness = readiness;
        _launched = launched;
    }

    public override string Name => "revit_handle_dialog";

    public override string Description =>
        "Click one button of a blocking Revit dialog (hwnd, button from revit_list_dialogs) and verify it closed. " +
        "Forbidden dialogs (security, unsigned add-in, license, sign-in, recovery, delete/overwrite/replace) are refused (PERMISSION_DENIED): " +
        "tell the user. Disabled buttons do not count. Only exception: the do-not-save button of a save prompt in a gateway-launched Revit, " +
        "with discard_changes:true.";

    public override ToolPermission Permission => ToolPermission.Interact;

    public override TimeSpan Timeout => TimeSpan.FromSeconds(90);

    protected override Schema BuildSchema() => Schema.Object()
        .Pid()
        .String("hwnd", "Dialog hwnd (0x1A2B3C).", required: true)
        .String("button", "Label, control id (6) or TaskDialog id (CommandButton_8).", required: true)
        .Boolean("discard_changes", "Allow do-not-save on a save prompt (sandbox copies or consent).", defaultValue: false)
        .Integer("verify_timeout_sec", "Seconds to wait for it to close.", defaultValue: 5, minimum: 1, maximum: 30);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = RevitToolHelpers.ResolvePid(call, required: true);
        var hwndText = call.RequireString("hwnd");
        if (!Win32Windows.TryParseHwnd(hwndText, out var hwnd))
        {
            throw GatewayException.Validation($"hwnd '{hwndText}' is not a window handle (use the hwnd from revit_list_dialogs, e.g. \"0x1A2B3C\").");
        }

        var button = call.RequireString("button");
        var verify = RevitToolHelpers.Seconds(call, "verify_timeout_sec", 5, 1, 30);
        RevitToolHelpers.EnsureRevitTarget(pid, _launched);
        using var actionLock = _readiness.AcquireActionLock(pid);
        var result = _readiness.HandleDialog(pid, hwnd, button, call.GetBool("discard_changes"), verify);
        return Task.FromResult(ToolResult.Json(result));
    }
}
