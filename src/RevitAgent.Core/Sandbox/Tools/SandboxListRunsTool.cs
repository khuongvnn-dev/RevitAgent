using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Sandbox.Tools;

/// <summary>sandbox_list_runs — run folders, newest first.</summary>
public sealed class SandboxListRunsTool : GatewayTool
{
    private readonly SandboxService _sandbox;

    public SandboxListRunsTool(SandboxService sandbox) => _sandbox = sandbox;

    public override string Name => "sandbox_list_runs";

    public override string Description =>
        "List sandbox runs, newest first: run_id, path, created_at, name, purpose, models (copies made with sandbox_copy_model), copy_count. " +
        "Use it to reuse a run_id or find a model copy to reopen. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("contains", "Only runs whose id or name contains this text (case-insensitive).")
        .Integer("limit", "Maximum number of runs to return.", defaultValue: 20, minimum: 1, maximum: 200);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var limit = call.GetLimit(defaultValue: 20, max: 200);
        var runs = _sandbox.ListRuns(limit + 1, call.GetString("contains"));
        return Task.FromResult(ToolResult.Json(new
        {
            runs_dir = _sandbox.RunsDir,
            count = Math.Min(runs.Count, limit),
            truncated = runs.Count > limit,
            runs = runs.Take(limit).ToArray(),
        }));
    }
}
