using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Sandbox.Tools;

/// <summary>sandbox_create_run — new run folder (RUN_DIR) with data\, evidence\ and run-info.json.</summary>
public sealed class SandboxCreateRunTool : GatewayTool
{
    private readonly SandboxService _sandbox;

    public SandboxCreateRunTool(SandboxService sandbox) => _sandbox = sandbox;

    public override string Name => "sandbox_create_run";

    public override string Description =>
        "Create a sandbox run folder <runs>\\<yyyyMMdd-HHmmss>-<slug>\\ with data\\ (outputs), evidence\\ (screenshots, reports) and run-info.json. " +
        "Returns run_id and paths. Everything an agent writes (model copies, evidence) belongs in a run; copy models into it with sandbox_copy_model. " +
        "Writes only inside the gateway runs folder.";

    public override ToolPermission Permission => ToolPermission.Write;

    protected override Schema BuildSchema() => Schema.Object()
        .String("name", "Short name of the run (becomes the slug, e.g. \"door schedule check\").")
        .String("purpose", "Free text: what this run is for (stored in run-info.json).");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var run = _sandbox.CreateRun(call.GetString("name"), call.GetString("purpose"), call.SessionId);
        return Task.FromResult(ToolResult.Json(new
        {
            run_id = run.RunId,
            path = run.Path,
            data_dir = run.DataDir,
            evidence_dir = run.EvidenceDir,
            created_at = run.CreatedAt,
            next_step = "Copy the model into this run with sandbox_copy_model (run_id), then open the copy with doc_open.",
        }));
    }
}
