using System.Text.Json;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Bridge;

/// <summary>bridge_status: which Revit processes have a reachable Bridge, and its version/busy state.</summary>
public sealed class BridgeStatusTool : GatewayTool
{
    private readonly BridgeClient _client;
    private readonly IRevitProcessProbe _processes;

    public BridgeStatusTool(BridgeClient client, IRevitProcessProbe processes)
    {
        _client = client;
        _processes = processes;
    }

    public override string Name => "bridge_status";

    public override string Description =>
        "Check the RevitAgent Bridge (the in-Revit add-in behind doc_*, model_*, schedule_*, ribbon_*, command_*, view_*, selection_* and dialog_* tools). " +
        "With pid: that Revit only; without: every running Revit. Per process: pipe_present, reachable, and from the Bridge itself bridge_version, protocol, Revit version/build, " +
        "busy/busy_tool (a Revit call in flight), uptime and dialog event count. Never touches the Revit API, so it answers even while Revit is busy. " +
        "pipe_present=false means the Bridge is not loaded: install it with bridge_install and restart that Revit.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

    protected override Schema BuildSchema() =>
        Schema.Object().Integer("pid", "Revit process id. Omit to check every running Revit.", minimum: 1);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        IReadOnlyList<RevitProcessInfo> targets;
        if (call.GetInt("pid") is { } pid)
        {
            var process = _processes.Get(pid) ?? throw GatewayException.NotFound($"No running process with pid {pid}. Call bridge_status without pid to list Revit processes.");
            targets = new[] { process };
        }
        else
        {
            targets = _processes.ListRevit();
        }

        var instances = new List<object>();
        foreach (var p in targets)
        {
            instances.Add(await DescribeAsync(p, cancellationToken).ConfigureAwait(false));
        }

        return ToolResult.Json(new
        {
            count = instances.Count,
            reachable = instances.Count(i => i is Dictionary<string, object?> d && d.TryGetValue("reachable", out var r) && r is true),
            instances,
            hint = instances.Count == 0 ? "No Revit is running. Start one with revit_launch." : null,
        });
    }

    private async Task<object> DescribeAsync(RevitProcessInfo p, CancellationToken cancellationToken)
    {
        var entry = new Dictionary<string, object?>
        {
            ["pid"] = p.Pid,
            ["process_name"] = p.ProcessName,
            ["year"] = p.Year,
            ["exe"] = p.ExePath,
            ["start_time"] = p.StartTime,
            ["pipe"] = @"\\.\pipe\" + _client.PipeName(p.Pid),
        };
        if (!p.IsRevit)
        {
            entry["pipe_present"] = false;
            entry["reachable"] = false;
            entry["error"] = new { code = Contracts.Protocol.GatewayErrorCodes.ValidationFailed, message = $"Process {p.Pid} is not Revit." };
            return entry;
        }

        var present = _client.IsPipePresent(p.Pid);
        entry["pipe_present"] = present;
        if (!present)
        {
            entry["reachable"] = false;
            entry["error"] = new
            {
                code = Contracts.Protocol.GatewayErrorCodes.DependencyFailed,
                message = $"RevitAgent Bridge is not loaded in Revit pid {p.Pid}. Install it with bridge_install and restart that Revit.",
            };
            return entry;
        }

        try
        {
            var ping = await _client.PingAsync(p.Pid, cancellationToken).ConfigureAwait(false);
            entry["reachable"] = true;
            entry["bridge"] = ping;
        }
        catch (GatewayException ex)
        {
            entry["reachable"] = false;
            entry["error"] = new { code = ex.Code, message = ex.Message };
        }

        return entry;
    }
}
