using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Sandbox.Tools;

/// <summary>sandbox_copy_model — verified copy of a model (and extra files) into a run.</summary>
public sealed class SandboxCopyModelTool : GatewayTool
{
    private readonly SandboxService _sandbox;

    public SandboxCopyModelTool(SandboxService sandbox) => _sandbox = sandbox;

    public override string Name => "sandbox_copy_model";

    public override string Description =>
        "Copy a Revit file (.rvt/.rfa/.rte/.rft) and extra_files into a sandbox run, so you never work on the original " +
        "(only read; sha256 proves it: original_unchanged). Returns copy_path: open THAT with doc_open. About 1 s per 100 MB.";

    public override ToolPermission Permission => ToolPermission.Write;

    public override TimeSpan Timeout => TimeSpan.FromMinutes(15);

    protected override Schema BuildSchema() => Schema.Object()
        .String("source_path", "Original file path (only read).", required: true)
        .String("run_id", "Existing run; omit for a new one.")
        .StringArray("extra_files", "Files to copy along (linked .rvt, shared parameters).")
        .String("name", "New run name.");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var result = _sandbox.CopyModel(
            call.RequireString("source_path"),
            call.GetString("run_id"),
            call.GetStringList("extra_files"),
            call.GetString("name"),
            call.SessionId,
            cancellationToken);
        return Task.FromResult(ToolResult.Json(result));
    }
}
