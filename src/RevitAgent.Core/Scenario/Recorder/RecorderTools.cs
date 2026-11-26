using System.Text.RegularExpressions;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario.Recorder;

/// <summary>recorder_start — begin recording this MCP session's tool calls.</summary>
public sealed class RecorderStartTool : GatewayTool
{
    private readonly ScenarioRecorder _recorder;

    public RecorderStartTool(ScenarioRecorder recorder) => _recorder = recorder;

    public override string Name => "recorder_start";

    public override string Description =>
        "Start recording THIS session's successful tool calls, to turn an exploration into a replayable scenario: UI actions (ui_click/type/select/toggle/keys/wait/expect...), Revit commands and writes, plus your verification reads (ui_expect, ui_read_table, model_query_elements, schedule_read, doc_info). Failed calls and recorder_*/scenario_*/gateway_* tools are not recorded. Changes nothing in Revit. Then do the task, recorder_stop, recorder_export.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("name", "Optional label for the recording (becomes the scenario description).")
        .Boolean("restart", "Discard a recording already running in this session and start over.", defaultValue: false);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var s = _recorder.Start(call.SessionId, call.GetString("name"), call.GetBool("restart"));
        return Task.FromResult(ToolResult.Json(new
        {
            recording = true,
            session_id = s.SessionId,
            started = s.Started,
            next = "Do the task (prefer id= selectors over refs so the replay is stable), verify the result (model_query_elements / schedule_read / ui_expect), then recorder_stop and recorder_export {name}.",
        }));
    }
}

/// <summary>recorder_stop — stop recording (the recording stays available for export).</summary>
public sealed class RecorderStopTool : GatewayTool
{
    private readonly ScenarioRecorder _recorder;

    public RecorderStopTool(ScenarioRecorder recorder) => _recorder = recorder;

    public override string Name => "recorder_stop";

    public override string Description =>
        "Stop recording this session. Returns what was captured (count per tool). The recording stays in memory (until the gateway restarts) for recorder_export.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var s = _recorder.Stop(call.SessionId);
        var calls = s.Calls;
        return Task.FromResult(ToolResult.Json(new
        {
            recording = false,
            session_id = s.SessionId,
            started = s.Started,
            stopped = s.Stopped,
            calls = calls.Count,
            dropped = s.Dropped,
            tools = calls.GroupBy(c => c.Tool).ToDictionary(g => g.Key, g => g.Count()),
            next = calls.Count == 0 ? "Nothing was recorded." : "recorder_export {name: 'my-scenario'} writes a draft scenario folder.",
        }));
    }
}

/// <summary>recorder_export — write the recording as a draft scenario folder.</summary>
public sealed class RecorderExportTool : GatewayTool
{
    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

    private readonly ScenarioRecorder _recorder;
    /// <summary>Marker written by recorder_export; overwrite is only allowed in folders that carry it (or live under the scenarios folder).</summary>
    internal const string ExportMarker = ".recorder-export";

    private readonly AgentPaths _paths;

    public RecorderExportTool(ScenarioRecorder recorder, AgentPaths paths)
    {
        _recorder = recorder;
        _paths = paths;
    }

    public override string Name => "recorder_export";

    public override string Description =>
        "Write this session's recording as a DRAFT scenario folder (default <gateway home>\\scenarios\\<name>\\): scenario.yaml (consecutive ui_* actions become one 'ui' step with a generated UI script ui\\NN-ui.json, other calls become tool steps; launching Revit, sandbox and open/close are left to scenario_run) with 'checks' C01.. built from your verification reads, using the recorded values as expectations. Selectors that could not be resolved are marked TODO. Review the draft, scenario_validate it, then replay with scenario_run.";

    public override ToolPermission Permission => ToolPermission.Write;

    protected override Schema BuildSchema() => Schema.Object()
        .String("name", "Scenario name = folder name: lower-case letters, digits, '-' or '_' (e.g. room-schedule-ui).", required: true)
        .String("out_dir", "Target scenario folder (must be inside the gateway home folder). Default <home>\\scenarios\\<name>.")
        .Boolean("overwrite", "Replace an existing folder of the same name.", defaultValue: false)
        .String("session_id", "Export another session's recording (default: this session; recorder_stop returns the id).");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var name = call.RequireString("name").Trim();
        if (!NamePattern.IsMatch(name))
        {
            throw GatewayException.Validation($"Invalid name '{name}': use lower-case letters, digits, '-' or '_' (max 64), e.g. room-schedule-ui.");
        }

        var sessionId = call.GetString("session_id") ?? call.SessionId;
        var session = _recorder.Get(sessionId) ?? throw GatewayException.NotFound($"No recording for session '{sessionId}'. Call recorder_start, do the task, recorder_stop, then export.");
        if (session.Calls.Count == 0)
        {
            throw GatewayException.Validation("The recording is empty: nothing to export.");
        }

        var root = Path.GetFullPath(_paths.Root).TrimEnd('\\', '/');
        var dir = call.GetString("out_dir") is { Length: > 0 } od ? Path.GetFullPath(od) : Path.Combine(root, "scenarios", name);
        if (!dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw GatewayException.Denied($"out_dir must be inside the gateway home folder ({root}).");
        }

        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            if (!call.GetBool("overwrite"))
            {
                throw new GatewayException(GatewayErrorCodes.Conflict, $"Folder '{dir}' already exists. Pick another name or pass overwrite: true.");
            }

            // Overwrite deletes scenario.yaml / ui/*.json: only in a folder that is recognisably a recorder export
            // (marker file) or lives under <home>\scenarios\ - never in an arbitrary folder under the gateway home.
            var scenariosRoot = Path.Combine(root, "scenarios") + Path.DirectorySeparatorChar;
            if (!File.Exists(Path.Combine(dir, ExportMarker)) && !dir.StartsWith(scenariosRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw GatewayException.Denied($"overwrite refused: '{dir}' is not a scenario folder created by the recorder (no {ExportMarker} marker, not under {scenariosRoot.TrimEnd(Path.DirectorySeparatorChar)}). Pick another out_dir.");
            }

            File.Delete(Path.Combine(dir, ScenarioDefinition.FileName));

            var ui = Path.Combine(dir, "ui");
            if (Directory.Exists(ui))
            {
                foreach (var f in Directory.EnumerateFiles(ui, "*.json"))
                {
                    File.Delete(f);
                }
            }
        }

        Directory.CreateDirectory(dir);
        var r = ScenarioExporter.Export(session, name, dir);
        File.WriteAllText(Path.Combine(dir, ExportMarker), "created by recorder_export; overwrite may replace scenario.yaml and ui/*.json here\n");
        return Task.FromResult(ToolResult.Json(new
        {
            scenario_dir = r.ScenarioDir,
            files = r.Files,
            tool_steps = r.ToolSteps,
            ui_steps = r.UiSteps,
            checks = r.Checks,
            todos = r.Todos,
            not_exported = r.Skipped,
            recording_active = session.Active,
            next = r.Todos.Count > 0
                ? "Fix the TODOs (selectors, model path, expected values), then scenario_validate and scenario_run."
                : "Review scenario.yaml, then scenario_validate and scenario_run {scenario_dir, pid}.",
        }));
    }
}
