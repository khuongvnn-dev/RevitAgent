using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_list_dialogs — current blockers with full details.</summary>
public sealed class RevitListDialogsTool : GatewayTool
{
    private readonly RevitReadiness _readiness;
    private readonly AgentPaths _paths;
    private readonly LaunchedProcessRegistry _launched;
    private readonly SandboxService _sandbox;

    public RevitListDialogsTool(RevitReadiness readiness, AgentPaths paths, LaunchedProcessRegistry launched, SandboxService sandbox)
    {
        _sandbox = sandbox;
        _readiness = readiness;
        _paths = paths;
        _launched = launched;
    }

    public override string Name => "revit_list_dialogs";

    public override string Description =>
        "Dialogs blocking a Revit: hwnd, title, text, buttons [{text, id, enabled}], category (info|forbidden|unknown), " +
        "forbidden_reason, save_prompt. Empty = nothing blocks. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .Pid(required: false)
        .Boolean("include_screenshot", "Attach a PNG per dialog (max 3); cite its path.", defaultValue: false)
        .Integer("limit", "Max dialogs.", defaultValue: 20, minimum: 1, maximum: 100);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = RevitToolHelpers.ResolvePid(call, required: false);
        RevitToolHelpers.EnsureRevitTarget(pid, _launched);
        var includeShots = call.GetBool("include_screenshot");
        var limit = call.GetLimit(defaultValue: 20, max: 100);
        var state = _readiness.GetState(pid, includeDetails: true, includeShots ? RevitToolHelpers.DialogShotDir(call, pid, _sandbox, _paths) : null);
        var dialogs = state.Blockers.Take(limit).ToArray();
        var result = ToolResult.Json(new
        {
            pid,
            count = state.Blockers.Count,
            truncated = state.Blockers.Count > limit,
            state = state.State,
            main_window_enabled = state.MainWindowEnabled,
            responding = state.Responding,
            dialogs,
            rule_errors = state.RuleErrors,
        });
        return Task.FromResult(includeShots ? RevitToolHelpers.AttachScreenshots(result, dialogs.Select(d => d.ScreenshotPath)) : result);
    }
}
