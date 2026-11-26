using System.Globalization;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario;

/// <summary>Shared argument handling of the scenario tools.</summary>
internal static class ScenarioArgs
{
    public static string RequireScenario(ToolCall call)
    {
        var dir = call.GetString("scenario_dir") ?? call.GetString("scenario_path");
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw GatewayException.Validation("Pass scenario_dir (folder with scenario.yaml) or scenario_path (the scenario.yaml file).");
        }

        string full;
        try
        {
            full = Path.GetFullPath(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw GatewayException.Validation($"Invalid scenario path '{dir}': {ex.Message}");
        }

        if (!Directory.Exists(full) && !File.Exists(full))
        {
            throw GatewayException.NotFound($"Scenario '{full}' does not exist.");
        }

        return full;
    }

    public static ToolRegistry Registry(IServiceProvider services) =>
        (ToolRegistry?)services.GetService(typeof(ToolRegistry)) ?? throw new GatewayException(GatewayErrorCodes.Internal, "Tool registry is not available.");
}

/// <summary>scenario_validate — lint a scenario folder without running anything.</summary>
public sealed class ScenarioValidateTool : GatewayTool
{
    private readonly IServiceProvider _services;

    public ScenarioValidateTool(IServiceProvider services) => _services = services;

    public override string Name => "scenario_validate";

    public override string Description =>
        "Lint a scenario folder (scenario.yaml + ui\\*.json UI scripts) without touching Revit: YAML errors with line numbers, steps calling tools unknown to this gateway or forbidden in scenarios (Admin, scenario_*, recorder_*), missing model / UI scripts, forbidden UI script content (launch/run, close without window, auto-answering security/license/recovery/delete/overwrite dialogs), 'purpose: setup' without 'why', check expression syntax and duplicate check ids. Run it before scenario_run (which refuses to start on errors). Fast (<1 s).";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("scenario_dir", "Scenario folder containing scenario.yaml (e.g. examples\\scenarios\\levels-inventory).")
        .String("scenario_path", "Alternative to scenario_dir: path of the scenario.yaml file.")
        .String("dataset", "Folder substituted for {dataset} (to check dataset paths).");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var dir = ScenarioArgs.RequireScenario(call);
        var v = ScenarioValidator.Validate(dir, ScenarioArgs.Registry(_services), call.GetString("dataset"));
        var s = v.Scenario;
        return Task.FromResult(ToolResult.Json(new
        {
            ok = v.Ok,
            scenario_dir = s?.Directory ?? dir,
            scenario = s?.Name,
            error_count = v.Errors.Count(),
            warning_count = v.Warnings.Count(),
            errors = v.Errors.Select(e => e.ToJson()).ToArray(),
            warnings = v.Warnings.Select(e => e.ToJson()).ToArray(),
            steps = s?.Steps.Count ?? 0,
            ui_steps = s?.Steps.Count(x => x.IsUi) ?? 0,
            tools = s?.Steps.Where(x => !x.IsUi).Select(x => x.Tool).Distinct().ToArray() ?? Array.Empty<string?>(),
            checks = s?.Checks.Count ?? 0,
            model = s?.Model,
            revit_version = s?.RevitVersion,
            next = v.Ok ? "Run it: scenario_run {scenario_dir, pid} (or launch: true)." : "Fix the errors (file + line given), then validate again.",
        }));
    }
}

/// <summary>scenario_run — execute a scenario end to end and grade it.</summary>
public sealed class ScenarioRunTool : GatewayTool
{
    private readonly IServiceProvider _services;
    private readonly AgentPaths _paths;
    private readonly GatewayPolicy _policy;
    private readonly SemaphoreSlim _instanceGate = new(1, 1);

    public ScenarioRunTool(IServiceProvider services, AgentPaths paths, GatewayPolicy policy)
    {
        _services = services;
        _paths = paths;
        _policy = policy;
    }

    public override string Name => "scenario_run";

    public override string Description =>
        "Run a scenario (scenario.yaml: steps + checks) end to end WITHOUT AI and evaluate its checks: creates a RUN_DIR in the sandbox, attaches to Revit (pid) or launches revit_version (launch: true), copies the model into RUN_DIR and opens only the copy (verified with doc_info), runs each step (any gateway tool, or a 'ui' script via ui_run_script) with a readiness check before it, saves every result to data\\<step>.json, then ALWAYS cleans up (closes the copy without saving; closes Revit only if this run launched it). Evaluates the checks deterministically (never PASS when the data is missing) and writes result.json, report.md, report.html, run-info.json. Returns verdict counts and report paths. Long-running: minutes to hours (per-step timeouts from the scenario). One run at a time.";

    public override ToolPermission Permission => ToolPermission.Write;

    public override TimeSpan Timeout => TimeSpan.FromHours(2);

    protected override Schema BuildSchema() => Schema.Object()
        .String("scenario_dir", "Scenario folder containing scenario.yaml.")
        .String("scenario_path", "Alternative to scenario_dir: path of the scenario.yaml file.")
        .Integer("pid", "Use this running Revit (from revit_list_instances). It is never closed by the run.")
        .Boolean("launch", "Start Revit <revit_version> with revit_launch for this run and close it afterwards.", defaultValue: false)
        .Boolean("keep_open", "Leave the document copy (and a Revit launched by this run) open after the run, for inspection.", defaultValue: false)
        .String("dataset", "Folder substituted for {dataset} in scenario.yaml.");

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var dir = ScenarioArgs.RequireScenario(call);
        var options = new ScenarioRunOptions
        {
            Scenario = dir,
            Pid = call.GetInt("pid"),
            Launch = call.GetBool("launch"),
            KeepOpen = call.GetBool("keep_open"),
            DatasetDir = call.GetString("dataset") is { Length: > 0 } ds ? Path.GetFullPath(ds) : null,
            SessionId = call.SessionId,
        };

        var batchRunner = _services.GetService(typeof(ScenarioBatchRunner)) as ScenarioBatchRunner;
        var gate = batchRunner?.ConcurrencyGate ?? _instanceGate;

        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new GatewayException(GatewayErrorCodes.Busy, "Another scenario_run is in progress on this gateway. Wait for it to finish, then retry.");
        }

        try
        {
            var runStore = _services.GetService(typeof(RunStore.RunStore)) as RunStore.RunStore;
            var liveness = _services.GetService(typeof(Func<int, ScenarioRunner.RevitProcessLiveness>)) as Func<int, ScenarioRunner.RevitProcessLiveness>;
            var journal = _services.GetService(typeof(Func<int, string?>)) as Func<int, string?>;
            var screenshot = _services.GetService(typeof(Func<int, byte[]?>)) as Func<int, byte[]?>;
            var clock = _services.GetService(typeof(Func<DateTimeOffset>)) as Func<DateTimeOffset>;

            var runner = new ScenarioRunner(ScenarioArgs.Registry(_services), _paths, _policy, runStore)
            {
                ProcessLivenessProbe = liveness,
                JournalPathFinder = journal,
                ScreenshotCapture = screenshot,
                Clock = clock,
            };
            var summary = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
            return ToolResult.Json(summary.ToJson());
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>scenario_list_runs — recent runs created by scenario_run.</summary>
public sealed class ScenarioListRunsTool : GatewayTool
{
    private readonly AgentPaths _paths;
    private readonly RunStore.RunStore? _runStore;

    public ScenarioListRunsTool(AgentPaths paths, RunStore.RunStore? runStore = null)
    {
        _paths = paths;
        _runStore = runStore;
    }

    public override string Name => "scenario_list_runs";

    public override string Description =>
        "List recent scenario_run runs (newest first) from the sandbox runs folder: run id, scenario, verdict, start/end, RUN_DIR and report paths. Filter by scenario name (substring) or verdict. Read-only, fast.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("scenario", "Only runs whose scenario name contains this text (case-insensitive).")
        .String("verdict", "Only runs with this verdict.", enumValues: new[] { "PASS", "FAIL", "SUSPECT", "ERROR" })
        .Integer("limit", "Maximum runs to return (default 20, max 100).", defaultValue: 20, minimum: 1, maximum: 100);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var limit = call.GetLimit(defaultValue: 20, max: 100);
        var scenario = call.GetString("scenario");
        var verdict = call.GetString("verdict");
        var root = _runStore?.RootDir ?? _paths.RunsDir;

        if (_runStore != null)
        {
            var allRuns = _runStore.ListRuns(new RunStore.RunFilter { Scenario = scenario, Verdict = verdict, Limit = 1000 });
            var rows = allRuns.Select(r => new
            {
                run_id = r.Id,
                scenario = r.Scenario,
                verdict = r.Verdict,
                started = r.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ended = r.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
                error = (string?)null,
                run_dir = r.RunDir,
                report_md = File.Exists(Path.Combine(r.RunDir, "report.md")) ? Path.Combine(r.RunDir, "report.md") : null,
                report_html = File.Exists(Path.Combine(r.RunDir, "report.html")) ? Path.Combine(r.RunDir, "report.html") : null,
            }).ToArray();

            return Task.FromResult(ToolResult.Json(new
            {
                runs_dir = root,
                total = rows.Length,
                returned = Math.Min(limit, rows.Length),
                runs = rows.Take(limit).ToArray(),
            }));
        }

        var runs = new List<(DateTimeOffset Started, object Row)>();
        root = _paths.RunsDir;
        var scanned = 0;
        foreach (var info in EnumerateRunInfos(root))
        {
            scanned++;
            JsonElement ri;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                ri = doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            if (ResultFields.FindString(ri, "runner") != ScenarioRunner.RunnerName)
            {
                continue;
            }

            var name = ResultFields.FindString(ri, "scenario") ?? ResultFields.FindString(ri, "name") ?? string.Empty;
            var v = ResultFields.FindString(ri, "verdict") ?? "RUNNING_OR_INCOMPLETE";
            if (scenario is not null && !name.Contains(scenario, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (verdict is not null && !string.Equals(v, verdict, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(info)!;
            var startedText = ResultFields.FindString(ri, "started");
            var started = DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var st) ? st : File.GetLastWriteTime(info);
            runs.Add((started, new
            {
                run_id = ResultFields.FindString(ri, "run") ?? Path.GetFileName(dir),
                scenario = name,
                verdict = v,
                started = startedText,
                ended = ResultFields.FindString(ri, "ended"),
                error = ResultFields.FindString(ri, "error"),
                run_dir = dir,
                report_md = File.Exists(Path.Combine(dir, "report.md")) ? Path.Combine(dir, "report.md") : null,
                report_html = File.Exists(Path.Combine(dir, "report.html")) ? Path.Combine(dir, "report.html") : null,
            }));
        }

        var ordered = runs.OrderByDescending(r => r.Started).ToArray();
        return Task.FromResult(ToolResult.Json(new
        {
            runs_dir = root,
            total = ordered.Length,
            returned = Math.Min(limit, ordered.Length),
            runs = ordered.Take(limit).Select(r => r.Row).ToArray(),
        }));
    }

    /// <summary>run-info.json files at depth 1 or 2 under the runs folder (sandbox_create_run may nest run folders).</summary>
    private static IEnumerable<string> EnumerateRunInfos(string root)
    {
        IEnumerable<string> Dirs(string d)
        {
            try
            {
                return Directory.EnumerateDirectories(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        foreach (var d1 in Dirs(root))
        {
            var f1 = Path.Combine(d1, "run-info.json");
            if (File.Exists(f1))
            {
                yield return f1;
                continue;
            }

            foreach (var d2 in Dirs(d1))
            {
                var f2 = Path.Combine(d2, "run-info.json");
                if (File.Exists(f2))
                {
                    yield return f2;
                }
            }
        }
    }
}
