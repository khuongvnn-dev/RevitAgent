using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.RunStore.Tools;

/// <summary>
/// runs_get — full metadata (run.json) and relative file list (+ sizes) for a run.
/// </summary>
public sealed class RunsGetTool : GatewayTool
{
    private readonly RunStore _runStore;

    public RunsGetTool(RunStore runStore) => _runStore = runStore;

    public override string Name => "runs_get";

    public override string Description =>
        "Get full run metadata (run.json) and a recursive list of all files with their byte sizes for a run_id. Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("run_id", "Run identifier (folder name or id from runs_list, e.g. 103144_levels-inventory_a1b2c3).", required: true);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var runId = call.RequireString("run_id");
        var details = _runStore.GetRun(runId);

        return Task.FromResult(ToolResult.Json(new
        {
            run_id = details.RunId,
            run_dir = details.RunDir,
            run = details.Run,
            files = details.Files.Select(f => new
            {
                path = f.Path,
                size = f.Size,
            }).ToArray(),
        }));
    }
}
