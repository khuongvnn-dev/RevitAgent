using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_get_state — readiness snapshot with blocker details.</summary>
public sealed class RevitGetStateTool : GatewayTool
{
    private readonly RevitReadiness _readiness;
    private readonly SandboxService _sandbox;
    private readonly AgentPaths _paths;
    private readonly LaunchedProcessRegistry _launched;

    public RevitGetStateTool(RevitReadiness readiness, SandboxService sandbox, AgentPaths paths, LaunchedProcessRegistry launched)
    {
        _readiness = readiness;
        _sandbox = sandbox;
        _paths = paths;
        _launched = launched;
    }

    public override string Name => "revit_get_state";

    public override string Description =>
        "Is this Revit ready to be driven? Returns {pid, ready, state (ready | busy = no main window yet | modal | not_responding | exited), blocked, responding, main_window_enabled, " +
        "bridge_pipe_available, launched_by_gateway, blocking_dialogs[] (dialogs and modal windows), blockers[] (also a disabled or hung main window)}. Each blocker: hwnd, kind (dialog | modal_window | disabled | hung), title, class, text, " +
        "buttons [{text, id, enabled}], category — info (an informational rule would answer it), forbidden (security / license / sign-in / recovery / " +
        "delete / overwrite / replace / save prompt: a human must answer), unknown (no rule: read it, answer with revit_handle_dialog only if clearly informational). " +
        "Detection is Win32-only and safe while Revit is busy; reading dialog texts can take a few seconds. include_screenshot:true attaches PNGs of the blockers. " +
        "Changes nothing.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .Pid(required: false)
        .Boolean("include_details", "Read texts and buttons of each dialog (WM_GETTEXT / UI Automation with timeouts).", defaultValue: true)
        .Boolean("include_screenshot", "Attach a PNG of each blocker (max 3); cite its path.", defaultValue: false)
        .String("run_id", "Save screenshots into this sandbox run's evidence folder (from sandbox_create_run).");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = RevitToolHelpers.ResolvePid(call, required: false);
        RevitToolHelpers.EnsureRevitTarget(pid, _launched);
        var includeShots = call.GetBool("include_screenshot");
        var dir = includeShots ? RevitToolHelpers.DialogShotDir(call, pid, _sandbox, _paths) : null;
        var state = _readiness.GetState(pid, call.GetBool("include_details", true), dir);
        var result = ToolResult.Json(state);
        return Task.FromResult(includeShots ? RevitToolHelpers.AttachScreenshots(result, state.Blockers.Select(b => b.ScreenshotPath)) : result);
    }
}
