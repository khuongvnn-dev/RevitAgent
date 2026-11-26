using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Scenario.Recorder;

/// <summary>Files written by <see cref="ScenarioExporter.Export"/>.</summary>
public sealed class ExportResult
{
    public required string ScenarioDir { get; init; }

    public List<string> Files { get; } = new();

    public int ToolSteps { get; set; }

    public int UiSteps { get; set; }

    public int Checks { get; set; }

    public List<string> Todos { get; } = new();

    public List<string> Skipped { get; } = new();
}

/// <summary>
/// Turns a recording into a DRAFT scenario folder: <c>scenario.yaml</c> (runs of consecutive ui_* actions become one
/// <c>ui</c> step with a generated UI script <c>ui\NN-ui.json</c>; other calls become tool steps; Revit launch,
/// sandbox and document open/close calls are left to the runner) whose <c>checks:</c> C01.. come from
/// the verification reads the agent made (expected values = the recorded results). Selectors are preferred over refs;
/// a ref the result did not resolve becomes a step that fails loudly (<c>TODO-ref:…</c>) and is listed as a TODO.
/// </summary>
public static class ScenarioExporter
{
    /// <summary>UI tools that map 1:1 to UI script steps.</summary>
    public static readonly IReadOnlyDictionary<string, string> UiCommands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ui_click"] = "click", ["ui_type"] = "type", ["ui_select"] = "select", ["ui_toggle"] = "toggle", ["ui_keys"] = "keys",
        ["ui_wait"] = "wait", ["ui_expect"] = "expect", ["ui_expand"] = "expand", ["ui_scroll"] = "scroll",
    };

    /// <summary>Handled by scenario_run itself (attach/launch, RUN_DIR, model copy, open/close) — not exported as steps.</summary>
    private static readonly Dictionary<string, string> RunnerOwned = new(StringComparer.Ordinal)
    {
        ["revit_launch"] = "the runner launches Revit (scenario_run launch: true, scenario revit_version)",
        ["revit_close"] = "the runner closes the Revit it launched",
        ["revit_wait_ready"] = "the runner waits for Revit",
        ["sandbox_create_run"] = "the runner creates RUN_DIR",
        ["sandbox_copy_model"] = "the runner copies 'model' into RUN_DIR",
        ["doc_open"] = "the runner opens the model copy",
        ["doc_close"] = "the runner closes the copy without saving",
        ["bridge_install"] = "install the Bridge before running scenarios",
        ["bridge_uninstall"] = "not part of a scenario",
        ["revit_handle_dialog"] = "dialogs answered while exploring are not replayed — if an informational dialog is expected, handle it in a ui script; never auto-answer security/license/recovery/delete/overwrite dialogs",
    };

    private static readonly string[] ArrayNames = { "elements", "items", "rows", "results", "data", "values", "levels", "views" };
    private static readonly string[] LabelFields = { "name", "Name", "title", "Title", "value", "Value" };

    private static readonly JsonSerializerOptions ScriptJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ExportResult Export(RecordingSession session, string name, string scenarioDir)
    {
        var calls = session.Calls;
        var result = new ExportResult { ScenarioDir = scenarioDir };
        Directory.CreateDirectory(Path.Combine(scenarioDir, "ui"));

        // pre-pass: what the runner will own
        int? year = null;
        string? model = null;
        var modelFromDocOpen = false;
        var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runDirs = new List<string>();
        foreach (var c in calls)
        {
            switch (c.Tool)
            {
                case "revit_launch":
                    year ??= Int(c.Args, "year");
                    break;
                case "sandbox_create_run" when ResultFields.FindString(c.Result, ResultFields.RunDir) is { } rd:
                    runDirs.Add(rd.TrimEnd('\\', '/'));
                    break;
                case "sandbox_copy_model":
                    model ??= Str(c.Args, "source_path");
                    if (ResultFields.FindString(c.Result, ResultFields.CopyPath) is { } cp)
                    {
                        copies.Add(cp);
                    }

                    break;
                case "doc_open" when Str(c.Args, "path") is { } p && !copies.Contains(p) && model is null:
                    model = p;
                    modelFromDocOpen = true;
                    break;
            }
        }

        var steps = new List<ExportStep>();
        var buffer = new List<RecordedCall>();
        void FlushUi()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            var code = $"{steps.Count + 1:D2}-ui";
            var script = BuildUiScript(name, code, buffer, result.Todos);
            var rel = $"ui\\{code}.json";
            File.WriteAllText(Path.Combine(scenarioDir, "ui", code + ".json"), script.Json.ToJsonString(ScriptJson), new UTF8Encoding(false));
            result.Files.Add(rel);
            steps.Add(new ExportStep(code, null, rel, new JsonObject(), null, script.Expects, script.Todo, buffer.Count));
            result.UiSteps++;
            buffer.Clear();
        }

        foreach (var c in calls)
        {
            if (UiCommands.ContainsKey(c.Tool))
            {
                buffer.Add(c);
                continue;
            }

            FlushUi();
            if (RunnerOwned.TryGetValue(c.Tool, out var why))
            {
                result.Skipped.Add($"#{c.Seq} {c.Tool}: {why}");
                continue;
            }

            if (c.Tool == "ui_run_script")
            {
                var code = $"{steps.Count + 1:D2}-ui";
                var rel = $"ui\\{code}.json";
                var dst = Path.Combine(scenarioDir, "ui", code + ".json");
                string? todo = null;
                if (Str(c.Args, "script_path") is { } sp && File.Exists(sp))
                {
                    File.Copy(sp, dst, overwrite: true);
                }
                else if (c.Args.TryGetProperty("script", out var inline) && inline.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                {
                    var text = inline.ValueKind == JsonValueKind.String ? inline.GetString()! : inline.GetRawText();
                    File.WriteAllText(dst, text, new UTF8Encoding(false));
                }
                else
                {
                    todo = $"{rel}: the recorded ui_run_script file was not found — copy the script here.";
                    File.WriteAllText(dst, "{\n  \"name\": \"TODO: script not found\",\n  \"steps\": []\n}\n", new UTF8Encoding(false));
                    result.Todos.Add(todo);
                }

                result.Files.Add(rel);
                steps.Add(new ExportStep(code, null, rel, new JsonObject(), null, 0, todo, 1));
                result.UiSteps++;
                continue;
            }

            var stepCode = $"{steps.Count + 1:D2}-{c.Tool}";
            steps.Add(new ExportStep(stepCode, c.Tool, null, SanitizeArgs(c.Args, copies, runDirs), c, 0, null, 1));
            result.ToolSteps++;
        }

        FlushUi();

        var checks = BuildChecks(steps, result);
        File.WriteAllText(Path.Combine(scenarioDir, ScenarioDefinition.FileName), ScenarioYaml(session, name, year, model, modelFromDocOpen, steps, checks, result), new UTF8Encoding(false));
        result.Files.Insert(0, ScenarioDefinition.FileName);
        result.Checks = checks.Count;
        return result;
    }

    // ---- ui scripts ------------------------------------------------------------------------------

    private static (JsonObject Json, int Expects, string? Todo) BuildUiScript(string scenarioName, string code, List<RecordedCall> calls, List<string> todos)
    {
        var steps = new JsonArray();
        var stepTodos = new JsonArray();
        var expects = 0;
        foreach (var c in calls)
        {
            var cmd = UiCommands[c.Tool];
            var s = new JsonObject { ["action"] = cmd };
            var window = Str(c.Args, "window");
            var (target, todo) = Target(c);
            var needsTarget = cmd is "click" or "type" or "select" or "toggle" or "expand" or "scroll" or "expect";
            if (target is not null)
            {
                s["selector"] = target;
            }
            else if (needsTarget && window is null)
            {
                todo ??= $"#{c.Seq} {c.Tool}: no selector recorded";
                s["selector"] = "TODO-selector";
            }

            if (window is not null)
            {
                s["window"] = window;
            }

            // The script step takes the tool's own arguments: carry over everything except targeting and session fields.
            foreach (var p in c.Args.EnumerateObject())
            {
                var pointClick = cmd == "click" && target is null && p.Name is "x" or "y";
                if ((p.Name is "pid" or "hwnd" or "ref" or "selector" or "window" or "x" or "y") && !pointClick)
                {
                    continue;
                }

                if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                s[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            }

            if (cmd == "expect")
            {
                expects++;
            }

            if (todo is not null)
            {
                s["name"] = "TODO: " + todo;
                stepTodos.Add(todo);
                todos.Add($"ui\\{code}.json step {steps.Count + 1}: {todo}");
            }

            steps.Add(s);
        }

        var json = new JsonObject
        {
            ["name"] = $"{scenarioName} - {code} (recorded {DateTime.Now:yyyy-MM-dd})",
            ["timeout"] = 15,
            ["screenshots"] = "fail",
            ["steps"] = steps,
        };
        if (stepTodos.Count > 0)
        {
            json["todo"] = stepTodos;
        }

        return (json, expects, stepTodos.Count > 0 ? $"{stepTodos.Count} TODO selector(s) in ui\\{code}.json" : null);
    }

    /// <summary>Selector from args (selector/target), else the selector the tool resolved for a ref, else (null, TODO).</summary>
    private static (string? Target, string? Todo) Target(RecordedCall c)
    {
        var sel = Str(c.Args, "selector") ?? Str(c.Args, "target");
        if (sel is not null && !Regex.IsMatch(sel, @"^e\d+$"))
        {
            return (sel, null);
        }

        var reference = Str(c.Args, "ref") ?? sel;
        if (reference is null)
        {
            return (null, null);
        }

        var resolved = ResultFields.FindString(c.Result, "selector", "resolved_selector", "element.selector", "target.selector", "element.resolved_selector");
        if (resolved is not null)
        {
            return (resolved, null);
        }

        var aid = ResultFields.FindString(c.Result, "automation_id", "element.automation_id", "automationId", "element.automationId", "target.automation_id");
        if (aid is not null)
        {
            return ($"id={aid}", null);
        }

        var nm = ResultFields.FindString(c.Result, "element.name", "name", "target.name");
        var type = ResultFields.FindString(c.Result, "element.control_type", "control_type", "element.type", "target.control_type");
        if (nm is not null && type is not null)
        {
            return ($"name={nm};type={type}", null);
        }

        return ($"TODO-ref:{reference}", $"#{c.Seq} {c.Tool}: only ref '{reference}' was used and the result did not resolve a selector — replace with id=<AutomationId> (ui_inspect)");
    }

    // ---- scenario.yaml ------------------------------------------------------------------------------

    private static JsonObject SanitizeArgs(JsonElement args, HashSet<string> copies, List<string> runDirs)
    {
        var o = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        o.Remove("pid");
        return (JsonObject)Replace(o, copies, runDirs)!;
    }

    private static JsonNode? Replace(JsonNode? n, HashSet<string> copies, List<string> runDirs) => n switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Replace(p.Value, copies, runDirs)))),
        JsonArray a => new JsonArray(a.Select(x => Replace(x, copies, runDirs)).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(ReplacePath(v.GetValue<string>(), copies, runDirs)),
        null => null,
        _ => n.DeepClone(),
    };

    private static string ReplacePath(string s, HashSet<string> copies, List<string> runDirs)
    {
        if (copies.Contains(s))
        {
            return "{model}";
        }

        foreach (var rd in runDirs)
        {
            if (s.StartsWith(rd + "\\", StringComparison.OrdinalIgnoreCase) || s.Equals(rd, StringComparison.OrdinalIgnoreCase))
            {
                return "{run_dir}" + s[rd.Length..];
            }
        }

        return s;
    }

    private static string ScenarioYaml(RecordingSession session, string name, int? year, string? model, bool modelFromDocOpen, List<ExportStep> steps, List<DraftCheck> checks, ExportResult result)
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"# DRAFT exported by recorder_export (RevitAgent {Mcp.McpGatewayServer.Version}) on {DateTime.Now:yyyy-MM-dd HH:mm} from MCP session '{session.SessionId}' ({session.Calls.Count} recorded calls).");
        sb.AppendLine("# Review before trusting it: selectors marked TODO, step args, and the expected values in checks (taken from the recording, so a person must confirm them).");
        sb.AppendLine("# Format: docs/agent-guide/scenarios.md. A check that cannot be measured is NOT_MEASURABLE, never PASS.");
        sb.AppendLine("# Check: scenario_validate {scenario_dir: <this folder>}  -  Replay without AI: scenario_run {scenario_dir, pid | launch: true}");
        foreach (var s in result.Skipped)
        {
            sb.AppendLine("# not a step: " + s);
        }

        sb.AppendLine($"name: {name}");
        sb.AppendLine($"description: {YamlEmit.Quote(session.Label is { Length: > 0 } l ? l : $"Recorded scenario {name}")}");
        if (year is not null)
        {
            sb.AppendLine($"revit_version: {year}");
        }
        else
        {
            sb.AppendLine("# revit_version: 2024    # TODO: set the Revit year when the scenario should launch Revit (launch: true)");
        }

        if (model is not null)
        {
            sb.AppendLine($"model: {YamlEmit.Quote(model)}{(modelFromDocOpen ? "    # TODO: doc_open was called on this path directly - point to the ORIGINAL model (the runner copies it into RUN_DIR)" : "    # original, read-only - the runner opens a copy in RUN_DIR")}");
        }
        else
        {
            sb.AppendLine("# model: 'input\\model.rvt'    # optional: original model; the runner copies it into RUN_DIR and opens the copy");
        }

        sb.AppendLine("keep_model_copy: false");
        sb.AppendLine("timeouts: { startup_min: 5, open_model_min: 10, step_min: 30 }");
        sb.AppendLine();
        sb.AppendLine("steps:");
        if (steps.Count == 0)
        {
            sb.AppendLine("  []    # TODO: nothing replayable was recorded");
        }

        foreach (var s in steps)
        {
            sb.AppendLine($"  - name: {s.Code}");
            if (s.Tool is not null)
            {
                sb.AppendLine($"    tool: {s.Tool}");
                if (s.Args.Count > 0)
                {
                    YamlEmit.Mapping(sb, "    ", "args", s.Args);
                }
            }
            else
            {
                sb.AppendLine($"    ui: {YamlEmit.Quote(s.Ui!)}{(s.Todo is null ? string.Empty : "    # TODO: " + s.Todo)}");
                sb.AppendLine("    timeout_sec: 300");
            }

            sb.AppendLine("    on_error: stop");
        }

        sb.AppendLine();
        sb.AppendLine("checks:");
        if (checks.Count == 0)
        {
            sb.AppendLine("  []    # TODO: no verification read was recorded - add checks");
        }

        var n = 0;
        foreach (var c in checks)
        {
            n++;
            sb.AppendLine($"  - id: C{n:D2}");
            sb.AppendLine($"    title: {YamlEmit.Quote(c.Title)}");
            sb.AppendLine($"    expect: {YamlEmit.Quote(c.Expect)}");
            sb.AppendLine("    severity: fail");
            sb.AppendLine($"    note: {YamlEmit.Quote($"Recorded {date}: {c.Recorded}. Measured from {c.Measure}. Confirm the expected value before relying on it.")}");
            if (c.Todo is not null)
            {
                sb.AppendLine($"    # TODO: {c.Todo}");
            }
        }

        return sb.ToString();
    }

    // ---- checks ----------------------------------------------------------------------------------

    private static List<DraftCheck> BuildChecks(List<ExportStep> steps, ExportResult result)
    {
        var checks = new List<DraftCheck>();
        foreach (var s in steps)
        {
            if (s.Ui is not null)
            {
                checks.Add(new DraftCheck(
                    $"UI step {s.Code} completes",
                    $"step_ok({s.Code})",
                    $"UI script {s.Ui} ({s.UiActions} actions, {s.Expects} expect steps) finished and every expect held",
                    "data\\step_status.json",
                    s.Todo));
                continue;
            }

            if (s.Call is not { } c || !ScenarioRecorder.VerificationReads.Contains(c.Tool) || c.Tool == "ui_wait")
            {
                continue;
            }

            var file = s.Code + ".json";
            if (c.Result is not { } res)
            {
                checks.Add(new DraftCheck($"{c.Tool} runs", $"step_ok({s.Code})", "the result was too large or had no JSON", $"data\\{file}", "add a value check on data\\" + file));
                continue;
            }

            if (c.Tool == "doc_info")
            {
                var title = ResultFields.FindString(res, "title", "name", "document.title");
                checks.Add(title is not null && Safe(title)
                    ? new DraftCheck("The expected model is open", $"value({file} -> {(ResultFields.TryPath(res, "title", out _) ? "title" : "name")}) == \"{title}\"", $"doc_info title = \"{title}\"", $"data\\{file} -> title", null)
                    : new DraftCheck("A document is open", $"step_ok({s.Code})", "doc_info succeeded", $"data\\{file}", null));
                continue;
            }

            // schedule_read: count the data rows below the column headings (its first top-level array is 'fields').
            if (c.Tool == "schedule_read" && ResultFields.TryPath(res, "body.data_rows", out var dataRows) && dataRows.ValueKind == JsonValueKind.Number)
            {
                var n = dataRows.GetRawText();
                checks.Add(new DraftCheck($"schedule_read returns {n} data rows", $"value({file} -> body.data_rows) == {n}", $"schedule_read {ArgsSummary(s.Args)} returned {n} data rows (column headings not counted)", $"data\\{file} -> body.data_rows", null));
                continue;
            }

            var (prop, items) = FindArray(res);
            if (prop is null)
            {
                var count = ResultFields.FindInt(res, "count", "total", "row_count");
                checks.Add(count is not null
                    ? new DraftCheck($"{c.Tool} returns {count} items", $"value({file} -> {(ResultFields.TryPath(res, "count", out _) ? "count" : ResultFields.TryPath(res, "total", out _) ? "total" : "row_count")}) == {count}", $"{c.Tool} {ArgsSummary(s.Args)} returned {count}", $"data\\{file} -> count", null)
                    : new DraftCheck($"{c.Tool} runs", $"step_ok({s.Code})", $"{c.Tool} {ArgsSummary(s.Args)} succeeded", $"data\\{file}", $"no list found in the {c.Tool} result - add a value check"));
                continue;
            }

            var terms = new List<string> { $"count({file} -> {prop}) == {items.Count}" };
            var labels = new List<string>();
            if (items.Count is > 0 and <= 10)
            {
                var field = LabelFields.FirstOrDefault(f => items.All(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String));
                if (field is not null)
                {
                    foreach (var g in items.Select(i => i.GetProperty(field).GetString()!).GroupBy(v => v, StringComparer.Ordinal))
                    {
                        if (Safe(g.Key))
                        {
                            terms.Add($"count({file} -> {prop}[{field}=\"{g.Key}\"]) == {g.Count()}");
                            labels.Add(g.Key);
                        }
                    }
                }
            }

            checks.Add(new DraftCheck(
                $"{c.Tool} returns exactly {items.Count} items",
                string.Join(" and ", terms),
                $"{c.Tool} {ArgsSummary(s.Args)} returned {items.Count} items{(labels.Count > 0 ? ": " + string.Join(", ", labels) : string.Empty)}",
                $"data\\{file} -> {prop}",
                null));
        }

        if (checks.Count == 0 && steps.Count > 0)
        {
            var last = steps[^1];
            checks.Add(new DraftCheck("The scenario runs to the end", $"step_ok({last.Code})", $"the last step {last.Code} succeeded", "data\\step_status.json", "no verification read was recorded - add a check that verifies the result in the model (model_query_elements / schedule_read)"));
        }

        foreach (var c in checks.Where(c => c.Todo is not null))
        {
            result.Todos.Add("scenario.yaml checks: " + c.Todo);
        }

        return checks;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static (string? Prop, List<JsonElement> Items) FindArray(JsonElement res)
    {
        if (res.ValueKind != JsonValueKind.Object)
        {
            return (null, new List<JsonElement>());
        }

        foreach (var n in ArrayNames)
        {
            if (res.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array)
            {
                return (n, a.EnumerateArray().ToList());
            }
        }

        foreach (var p in res.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array && Regex.IsMatch(p.Name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                return (p.Name, p.Value.EnumerateArray().ToList());
            }
        }

        return (null, new List<JsonElement>());
    }

    /// <summary>Value usable inside an expect literal (no quotes/brackets/backticks).</summary>
    private static bool Safe(string v) => v.Length is > 0 and <= 120 && v.IndexOfAny(new[] { '"', '[', ']', '`', '\n', '\r', '|' }) < 0;

    private static string ArgsSummary(JsonObject args) => args.Count == 0 ? string.Empty : "(" + ResultFields.Clip(string.Join(", ", args.Select(p => $"{p.Key}={p.Value?.ToJsonString()}")), 160) + ")";

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement o, string name) =>
        int.TryParse(Str(o, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    private static bool Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    private sealed record ExportStep(string Code, string? Tool, string? Ui, JsonObject Args, RecordedCall? Call, int Expects, string? Todo, int UiActions);

    private sealed record DraftCheck(string Title, string Expect, string Recorded, string Measure, string? Todo);
}
