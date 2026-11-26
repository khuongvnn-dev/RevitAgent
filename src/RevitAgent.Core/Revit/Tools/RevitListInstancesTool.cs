using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_list_instances — every running Revit.exe, observed without sending anything to it.</summary>
public sealed class RevitListInstancesTool : GatewayTool
{
    private readonly RevitProcessService _processes;

    public RevitListInstancesTool(RevitProcessService processes) => _processes = processes;

    public override string Name => "revit_list_instances";

    public override string Description =>
        "Running Revit processes: pid (for other tools), year, state (ready|busy|modal|not_responding), " +
        "launched_by_gateway (only those can be closed), bridge_pipe_available (Bridge loaded: doc/model tools work), active_document_guess. " +
        "Sends nothing to Revit; safe while busy.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .Integer("year", "Only this Revit year.")
        .Boolean("launched_by_gateway", "Only gateway-launched (true) or only other (false) Revits.")
        .Integer("limit", "Max processes.", defaultValue: 20, minimum: 1, maximum: 100);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var year = call.GetInt("year");
        bool? owned = call.Has("launched_by_gateway") ? call.GetBool("launched_by_gateway") : null;
        var limit = call.GetLimit(defaultValue: 20, max: 100);
        var all = _processes.ListInstances();
        var filtered = all.Where(i => (year is null || i.Year == year) && (owned is null || i.LaunchedByGateway == owned)).ToArray();
        return Task.FromResult(ToolResult.Json(new
        {
            count = filtered.Length,
            total_running = all.Count,
            truncated = filtered.Length > limit,
            instances = filtered.Take(limit).ToArray(),
        }));
    }
}
