using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Machine;

/// <summary>
/// MCP tool allowing any AI agent to probe whether this machine is ready to run Revit tests unattended overnight.
/// </summary>
public sealed class MachineCheckUnattendedTool : GatewayTool
{
    private readonly MachineReadiness _readiness;

    public MachineCheckUnattendedTool(MachineReadiness readiness)
    {
        _readiness = readiness;
    }

    public override string Name => "machine_check_unattended";

    public override string Description =>
        "Check whether this machine is ready to run Revit tests unattended overnight: interactive desktop session, " +
        "workstation lock, RDP state, sleep/hibernate timeouts on AC, pending reboot, elevation, Revit & Bridge install, " +
        "allowlist, run storage free space, and Telegram notifications. Read-only; never changes system settings.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() =>
        Schema.Object("Probe machine readiness for unattended overnight Revit execution.")
            .Integer("year", "Revit release year to check (default: 2024).", required: false, defaultValue: 2024);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var year = call.GetInt("year", 2024) ?? 2024;
        var options = new MachineCheckOptions { Year = year };
        var report = await _readiness.CheckAsync(options, cancellationToken).ConfigureAwait(false);
        return ToolResult.Json(report);
    }
}
