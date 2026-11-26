using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario;

/// <summary>Outcome of <see cref="ScenarioValidator.Validate"/>.</summary>
public sealed class ScenarioValidation
{
    public ScenarioDefinition? Scenario { get; init; }

    public List<ScenarioIssue> Issues { get; } = new();

    public bool Ok => Issues.All(i => i.Severity != IssueSeverity.Error);

    public IEnumerable<ScenarioIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ScenarioIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);
}

/// <summary>
/// Lints a scenario folder before it runs (scenario_validate; scenario_run refuses to start on errors):
/// YAML syntax (with line), required keys, tools unknown to the current registry, tools a scenario must not call,
/// missing model / UI scripts, forbidden UI script content, <c>purpose: setup</c> without <c>why</c>, check syntax
/// (expression grammar), duplicate check ids, and references to steps/files that no step produces.
/// </summary>
public static class ScenarioValidator
{
    /// <summary>Tool-name prefixes a scenario step may not call (recursion / recording loops).</summary>
    public static readonly IReadOnlyList<string> ForbiddenPrefixes = new[] { "scenario_", "recorder_" };

    /// <summary>
    /// Validates the scenario. <paramref name="registry"/> null → tool names are not checked.
    /// <paramref name="datasetDir"/> resolves <c>{dataset}</c> for file checks (missing → those checks are skipped with a warning).
    /// </summary>
    public static ScenarioValidation Validate(string scenarioDirOrFile, ToolRegistry? registry, string? datasetDir = null)
    {
        var load = ScenarioLoader.Load(scenarioDirOrFile);
        var scenario = load.Scenario;
        var v = new ScenarioValidation { Scenario = scenario };
        v.Issues.AddRange(load.Issues);
        if (scenario is null)
        {
            return v;
        }

        const string scenarioFile = ScenarioDefinition.FileName;
        var vars = new ScenarioVariables(scenario.Directory).Set("dataset", datasetDir)
            .Set("run_dir", "<run_dir>").Set("pid", "0").Set("model", "<model>");

        if (!string.Equals(scenario.Name, Path.GetFileName(scenario.Directory.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase))
        {
            v.Issues.Add(ScenarioIssue.Warning("scenario-name", $"name '{scenario.Name}' differs from the folder name '{Path.GetFileName(scenario.Directory)}' (convention: name = folder name).", scenarioFile, 1));
        }

        if (scenario.RetryDefault < 0 || scenario.RetryDefault > 5)
        {
            v.Issues.Add(ScenarioIssue.Error("retry-default-range", $"retry_default must be between 0 and 5 (got {scenario.RetryDefault}).", scenarioFile));
        }

        if (scenario.RetryDelaySec < 0)
        {
            v.Issues.Add(ScenarioIssue.Error("retry-delay-range", $"retry_delay_sec must be non-negative (got {scenario.RetryDelaySec}).", scenarioFile));
        }

        if (scenario.HangTimeoutSec <= 0)
        {
            v.Issues.Add(ScenarioIssue.Error("hang-timeout-range", $"hang_timeout_sec must be positive (got {scenario.HangTimeoutSec}).", scenarioFile));
        }

        if (scenario.RerunOnCrash is not (0 or 1))
        {
            v.Issues.Add(ScenarioIssue.Error("rerun-on-crash-range", $"rerun_on_crash must be 0 or 1 (got {scenario.RerunOnCrash}).", scenarioFile));
        }

        CheckModel(scenario, vars, datasetDir, v);

        var codes = new HashSet<string>(StringComparer.Ordinal);
        var dataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "step_status.json", "run.log", "setup-guard.json" };
        foreach (var step in scenario.Steps)
        {
            if (!codes.Add(step.Code))
            {
                v.Issues.Add(ScenarioIssue.Warning("step-duplicate", $"Step {step.Index + 1} writes data\\{step.Code}.json like an earlier step (the later result overwrites it). Give steps unique 'name's.", scenarioFile, step.Line));
            }

            if (step.Retry is { } ret && (ret < 0 || ret > 5))
            {
                v.Issues.Add(ScenarioIssue.Error("step-retry-range", $"Step {step.Index + 1}: retry must be between 0 and 5 (got {ret}).", scenarioFile, step.Line));
            }

            if (step.RetryDelaySec is { } rds && rds < 0)
            {
                v.Issues.Add(ScenarioIssue.Error("step-retry-delay-range", $"Step {step.Index + 1}: retry_delay_sec must be non-negative (got {rds}).", scenarioFile, step.Line));
            }

            dataNames.Add(step.Code + ".json");
            if (step.IsUi)
            {
                CheckUiStep(step, vars, datasetDir, v);
            }
            else
            {
                CheckToolStep(step, registry, v);
            }

            foreach (var unknown in ScenarioVariables.UnknownPlaceholders(step.Args.ToJsonString() + " " + step.Ui))
            {
                v.Issues.Add(ScenarioIssue.Warning("variable-unknown", $"Step {step.Index + 1} uses {{{unknown}}} — known variables: {string.Join(", ", ScenarioVariables.Known.Select(k => "{" + k + "}"))}. It is passed through unchanged.", scenarioFile, step.Line));
            }

            var used = ScenarioVariables.UsedPlaceholders(step.Args.ToJsonString() + " " + step.Ui).ToArray();
            if (used.Contains("model") && scenario.Model is null)
            {
                v.Issues.Add(ScenarioIssue.Error("variable-model", $"Step {step.Index + 1} uses {{model}} but scenario.yaml has no 'model'.", scenarioFile, step.Line));
            }
        }

        CheckChecks(scenario, codes, dataNames, v);
        return v;
    }

    private static void CheckModel(ScenarioDefinition scenario, ScenarioVariables vars, string? datasetDir, ScenarioValidation v)
    {
        if (scenario.Model is null)
        {
            return;
        }

        if (scenario.Model.Contains("{dataset}", StringComparison.Ordinal) && datasetDir is null)
        {
            v.Issues.Add(ScenarioIssue.Warning("model-dataset", "model uses {dataset}: pass 'dataset' (folder) to check it; scenario_run needs it too.", ScenarioDefinition.FileName));
            return;
        }

        string path;
        try
        {
            path = vars.ResolveFile(scenario.Model);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            v.Issues.Add(ScenarioIssue.Error("model-path", $"model '{scenario.Model}': {ex.Message}", ScenarioDefinition.FileName));
            return;
        }

        if (!File.Exists(path))
        {
            v.Issues.Add(ScenarioIssue.Error("model-missing", $"model not found: {path}", ScenarioDefinition.FileName));
        }
        else if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
        {
            v.Issues.Add(ScenarioIssue.Warning("model-extension", $"model '{Path.GetFileName(path)}' is not a .rvt/.rfa file.", ScenarioDefinition.FileName));
        }
    }

    private static void CheckUiStep(ScenarioStep step, ScenarioVariables vars, string? datasetDir, ScenarioValidation v)
    {
        const string file = ScenarioDefinition.FileName;
        if (step.IsSetup)
        {
            v.Issues.Add(ScenarioIssue.Error("ui-setup", $"Step {step.Index + 1}: a ui step cannot be 'purpose: setup' (setup only through approved tools).", file, step.Line));
        }

        if (string.IsNullOrWhiteSpace(step.Name))
        {
            v.Issues.Add(ScenarioIssue.Warning("ui-name", $"Step {step.Index + 1}: give the ui step a 'name' (data file and step code); default '{step.Code}'.", file, step.Line));
        }

        if (step.Ui!.Contains("{dataset}", StringComparison.Ordinal) && datasetDir is null)
        {
            v.Issues.Add(ScenarioIssue.Warning("ui-dataset", $"Step {step.Index + 1}: ui script uses {{dataset}} — pass 'dataset' to check it.", file, step.Line));
            return;
        }

        string path;
        try
        {
            path = vars.ResolveFile(step.Ui!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            v.Issues.Add(ScenarioIssue.Error("ui-path", $"Step {step.Index + 1}: ui '{step.Ui}': {ex.Message}", file, step.Line));
            return;
        }

        foreach (var issue in UiScriptLint.Lint(path, step.Ui!))
        {
            v.Issues.Add(issue);
        }
    }

    private static void CheckToolStep(ScenarioStep step, ToolRegistry? registry, ScenarioValidation v)
    {
        const string file = ScenarioDefinition.FileName;
        var tool = step.Tool!;
        if (ForbiddenPrefixes.Any(p => tool.StartsWith(p, StringComparison.Ordinal)))
        {
            v.Issues.Add(ScenarioIssue.Error("tool-forbidden", $"Step {step.Index + 1}: '{tool}' cannot be a scenario step (no nested scenarios / recording).", file, step.Line));
            return;
        }

        if (step.IsSetup && string.IsNullOrWhiteSpace(step.Why))
        {
            v.Issues.Add(ScenarioIssue.Error("setup-why", $"Step {step.Index + 1}: 'purpose: setup' needs an explicit 'why' (which scenario situation it builds).", file, step.Line));
        }

        if (tool == "doc_close" && step.Args.TryGetPropertyValue("save", out var save) && save is JsonValue sv && sv.TryGetValue<bool>(out var b) && b)
        {
            v.Issues.Add(ScenarioIssue.Error("tool-forbidden", $"Step {step.Index + 1}: doc_close with save: true is forbidden — the runner closes the sandbox copy without saving.", file, step.Line));
        }

        if (registry is null)
        {
            return;
        }

        if (!registry.TryGet(tool, out var t))
        {
            v.Issues.Add(ScenarioIssue.Error("tool-unknown", $"Step {step.Index + 1}: unknown tool '{tool}' (not registered in this gateway — check spelling or the module that provides it).", file, step.Line));
            return;
        }

        if (t.Permission == ToolPermission.Admin)
        {
            v.Issues.Add(ScenarioIssue.Error("tool-forbidden", $"Step {step.Index + 1}: '{tool}' is an Admin tool (process/installation level). The runner launches/closes Revit itself (launch: true); scenarios cannot call Admin tools.", file, step.Line));
            return;
        }

        CheckArgsAgainstSchema(step, t, v);
    }

    private static void CheckArgsAgainstSchema(ScenarioStep step, IGatewayTool tool, ScenarioValidation v)
    {
        var schema = tool.InputSchema;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var allowsExtra = !(schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False);
        foreach (var (key, _) in step.Args)
        {
            if (!props.TryGetProperty(key, out _) && !allowsExtra)
            {
                v.Issues.Add(ScenarioIssue.Error("tool-arg", $"Step {step.Index + 1}: '{tool.Name}' has no argument '{key}'.", ScenarioDefinition.FileName, step.Line));
            }
        }

        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in req.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null && s != "pid"))
            {
                if (!step.Args.ContainsKey(r!))
                {
                    v.Issues.Add(ScenarioIssue.Error("tool-arg", $"Step {step.Index + 1}: '{tool.Name}' requires argument '{r}'.", ScenarioDefinition.FileName, step.Line));
                }
            }
        }
    }

    private static void CheckChecks(ScenarioDefinition scenario, HashSet<string> codes, HashSet<string> dataNames, ScenarioValidation v)
    {
        const string file = ScenarioDefinition.FileName;
        var checks = scenario.Checks;
        if (checks.Count == 0)
        {
            v.Issues.Add(ScenarioIssue.Warning("checks-missing", "No checks: nothing will be evaluated (verdict at most SUSPECT). Add 'checks:' with an id, title and expect expression each.", file));
            return;
        }

        foreach (var dup in checks.GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            v.Issues.Add(ScenarioIssue.Error("check-duplicate", $"Check id {dup.Key} is used {dup.Count()} times (ids are unique and stable; never reuse one).", file, dup.Skip(1).First().Line));
        }

        foreach (var check in checks)
        {
            if (string.IsNullOrWhiteSpace(check.Title))
            {
                v.Issues.Add(ScenarioIssue.Warning("check-title", $"{check.Id}: missing 'title'.", file, check.Line));
            }

            CheckExpression expr;
            try
            {
                expr = CheckExpression.Parse(check.Expect);
            }
            catch (CheckExpressionException ex)
            {
                v.Issues.Add(ScenarioIssue.Error("check-expect", $"{check.Id}: {ex.Message}", file, check.Line));
                continue;
            }

            foreach (var step in expr.ReferencedSteps().Where(s => !codes.Contains(s)))
            {
                v.Issues.Add(ScenarioIssue.Warning("check-step", $"{check.Id}: step_ok({step}) - no step has that name (steps: {string.Join(", ", codes)}).", file, check.Line));
            }

            foreach (var data in expr.ReferencedFiles())
            {
                var leaf = Path.GetFileName(data.Replace('/', '\\'));
                if (data.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !dataNames.Contains(leaf))
                {
                    v.Issues.Add(ScenarioIssue.Warning("check-file", $"{check.Id}: reads '{data}' but no step writes data\\{leaf} (step results are data\\<name or tool>.json).", file, check.Line));
                }
            }
        }
    }
}
