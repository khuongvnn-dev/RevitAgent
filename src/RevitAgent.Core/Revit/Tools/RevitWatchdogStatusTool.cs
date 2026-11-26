using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>
/// revit_watchdog_status — reports current watchdog state, watched pids, and recent events.
/// </summary>
public sealed class RevitWatchdogStatusTool : GatewayTool
{
    private readonly RevitWatchdog _watchdog;

    public RevitWatchdogStatusTool(RevitWatchdog watchdog) => _watchdog = watchdog;

    public override string Name => "revit_watchdog_status";

    public override string Description =>
        "Inspect the Revit watchdog background service: watched processes (PIDs launched by the gateway), " +
        "their liveness and hang states, configuration (hang_after_sec, enabled), and recent watchdog events (hang, recovered, crash). Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var status = _watchdog.GetStatus();
        return Task.FromResult(ToolResult.Json(new
        {
            enabled = status.Enabled,
            hang_after_sec = status.HangAfterSec,
            watched_pids = status.WatchedPids,
            processes = status.Processes.Select(p => new
            {
                pid = p.Pid,
                year = p.Year,
                state = p.State,
                hung_since = p.HungSince,
                hung_seconds = p.HungSeconds,
                last_known_title = p.LastKnownTitle,
            }).ToArray(),
            last_events = status.LastEvents.Select(e => new
            {
                kind = e.KindString,
                pid = e.Pid,
                year = e.Year,
                at = e.At,
            }).ToArray(),
        }));
    }
}
