using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace RevitAgent.Core.Scenario;

/// <summary>Inputs of the report writers (a finished run).</summary>
public sealed class ScenarioReportModel
{
    public required ScenarioDefinition Scenario { get; init; }

    public required string RunDir { get; init; }

    public required string RunId { get; init; }

    public required string Verdict { get; init; }

    public DateTimeOffset Started { get; init; }

    public DateTimeOffset Ended { get; init; }

    public int? Pid { get; init; }

    public bool Launched { get; init; }

    public string? ModelSource { get; init; }

    public string? ModelCopy { get; init; }

    public bool ModelChanged { get; init; }

    public string? EnvError { get; init; }

    public string? FirstToolError { get; init; }

    public required IReadOnlyList<StepRecord> Steps { get; init; }

    public required IReadOnlyList<CheckEvaluation> Checks { get; init; }

    public required IReadOnlyDictionary<string, object?> Cleanup { get; init; }
}

/// <summary>
/// Writes the outputs of a run: <c>data\checks.json</c> (every check with its evaluated terms),
/// <c>result.json</c> (<c>{scenario, run_id, verdict, error, checks:[{id, title, severity, expect, actual, evidence, verdict, note, key}]}</c>),
/// <c>report.md</c> and a self-contained <c>report.html</c>. A check whose data is missing is NOT_MEASURABLE, never PASS.
/// </summary>
public static class ScenarioReports
{
    public static void WriteAll(ScenarioReportModel m)
    {
        WriteChecks(m);
        WriteResult(m);
        File.WriteAllText(Path.Combine(m.RunDir, "report.md"), Markdown(m), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(m.RunDir, "report.html"), Html(m), new UTF8Encoding(false));
    }

    private static void WriteChecks(ScenarioReportModel m)
    {
        var results = new JsonArray();
        foreach (var r in m.Checks)
        {
            var terms = new JsonArray();
            foreach (var t in r.Terms)
            {
                terms.Add(new JsonObject { ["text"] = t.Text, ["verdict"] = t.Verdict, ["detail"] = t.Detail });
            }

            results.Add(new JsonObject
            {
                ["id"] = r.Id,
                ["severity"] = r.Severity,
                ["verdict"] = r.Verdict,
                ["key"] = r.Key,
                ["files"] = new JsonArray(r.Files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["terms"] = terms,
            });
        }

        var summary = new JsonObject();
        foreach (var v in new[] { Verdicts.Pass, Verdicts.Fail, Verdicts.NotMeasurable })
        {
            summary[v] = m.Checks.Count(r => r.Verdict == v);
        }

        ScenarioRunner.WriteJson(Path.Combine(m.RunDir, "data", "checks.json"), new JsonObject
        {
            ["tool"] = "revitagent-checks",
            ["scenario"] = m.Scenario.Name,
            ["scenario_sha256"] = ScenarioRunner.Sha256File(m.Scenario.ScenarioPath),
            ["run"] = m.RunId,
            ["summary"] = summary,
            ["results"] = results,
        });
    }

    private static void WriteResult(ScenarioReportModel m)
    {
        var checks = new JsonArray();
        foreach (var r in m.Checks)
        {
            checks.Add(new JsonObject
            {
                ["id"] = r.Id,
                ["title"] = r.Title,
                ["severity"] = r.Severity,
                ["expect"] = r.Expect,
                ["actual"] = r.Detail,
                ["evidence"] = string.Join("; ", r.Terms.Select(t => (t.Text.Length > 0 ? t.Text + " -> " : string.Empty) + t.Verdict + ": " + t.Detail)) +
                               (r.Files.Count > 0 ? " [" + string.Join(", ", r.Files) + "]" : string.Empty),
                ["verdict"] = r.Verdict,
                ["note"] = r.Note ?? string.Empty,
                ["key"] = r.Key,
            });
        }

        ScenarioRunner.WriteJson(Path.Combine(m.RunDir, "result.json"), new JsonObject
        {
            ["scenario"] = m.Scenario.Name,
            ["run_id"] = m.RunId,
            ["verdict"] = m.Verdict,
            ["error"] = m.ModelChanged ? "ERROR: the original model file changed during the run" : m.EnvError ?? (m.Verdict == Verdicts.Error ? m.FirstToolError : null),
            ["checks"] = checks,
        });
    }

    // ---- report.md --------------------------------------------------------------------------------

    public static string Markdown(ScenarioReportModel m)
    {
        var checks = m.Checks;
        int Count(string v) => checks.Count(r => r.Verdict == v);
        var minutes = (m.Ended - m.Started).TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"# scenario_run {m.Scenario.Name} - {m.Started:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        if (m.ModelChanged)
        {
            sb.AppendLine("**ERROR: the original model file changed during the run** - see run.log").AppendLine();
        }

        sb.AppendLine($"Result: **{m.Verdict}** - checks {Count(Verdicts.Pass)} PASS, {Count(Verdicts.Fail)} FAIL, {Count(Verdicts.NotMeasurable)} NOT_MEASURABLE | Revit {m.Scenario.RevitVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"}, pid {m.Pid?.ToString(CultureInfo.InvariantCulture) ?? "-"}{(m.Launched ? " (launched by the run)" : string.Empty)}, {minutes} min");
        sb.AppendLine($"Runner: RevitAgent scenario_run {Mcp.McpGatewayServer.Version} | run `{m.RunId}` | model: {(m.ModelSource is null ? "-" : $"`{m.ModelSource}` -> copy `{m.ModelCopy}`")}");
        sb.AppendLine();

        if (m.EnvError is not null)
        {
            sb.AppendLine("## Stopped early").AppendLine().AppendLine(m.EnvError).AppendLine();
        }

        sb.AppendLine("## Needs attention (FAIL / NOT_MEASURABLE / failed steps)").AppendLine();
        sb.AppendLine("| Check | Verdict | Expect | Actual | Evidence |");
        sb.AppendLine("|---|---|---|---|---|");
        var attention = checks.Where(r => r.Verdict != Verdicts.Pass).ToArray();
        foreach (var r in attention)
        {
            sb.AppendLine($"| {r.Id} | {r.Verdict}{(r.Severity == CheckSeverity.Warn ? " (warn)" : string.Empty)} | {Cell(r.Expect)} | {Cell(r.Detail)} | {Cell(string.Join(", ", r.Files))} |");
        }

        foreach (var s in m.Steps.Where(s => s.Status is "FAILED" or "ERROR" or "BLOCKED"))
        {
            sb.AppendLine($"| step {Cell(s.Code)} | {s.Status} | SUCCEEDED | {Cell(s.Message)} | {Cell(s.DataFile)} |");
        }

        if (attention.Length == 0 && !m.Steps.Any(s => s.Status is "FAILED" or "ERROR" or "BLOCKED"))
        {
            sb.AppendLine("| - | - | - | nothing | - |");
        }

        sb.AppendLine();
        sb.AppendLine("## All checks").AppendLine();
        sb.AppendLine("| Check | Title | Severity | Verdict | Terms |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in checks)
        {
            var detail = string.Join("<br>", r.Terms.Select(t => $"{(t.Text.Length > 0 ? "`" + t.Text + "` -> " : string.Empty)}{t.Verdict}: {t.Detail}"));
            sb.AppendLine($"| {r.Id} | {Cell(r.Title)} | {r.Severity} | {r.Verdict} | {Cell(detail, keepBreaks: true)} |");
        }

        if (checks.Count == 0)
        {
            sb.AppendLine("| - | no checks | - | - | add 'checks:' to scenario.yaml |");
        }

        sb.AppendLine();
        sb.AppendLine("## Step log").AppendLine();
        sb.AppendLine("| # | Step | Kind | Started | Duration | Result | Data |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in m.Steps)
        {
            var retriesSuffix = s.Attempts.Count > 1 ? $" ({s.Attempts.Count} attempts)" : string.Empty;
            sb.AppendLine($"| {s.Index + 1} | {Cell(s.Code)}{(s.Setup ? " (setup)" : string.Empty)} | {s.Kind} `{Cell(s.Target)}` | {(s.Started is { } t ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "-")} | {s.DurationMs / 1000.0:0.0}s | {s.Status}{retriesSuffix}{(s.Message is null ? string.Empty : ": " + Cell(s.Message))} | {Cell(s.DataFile)} |");
        }

        var retried = m.Steps.Where(s => s.Attempts.Count > 1).ToArray();
        if (retried.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Step retries").AppendLine();
            foreach (var s in retried)
            {
                sb.AppendLine($"- Step `{s.Code}` ({s.Attempts.Count} attempts):");
                foreach (var a in s.Attempts)
                {
                    sb.AppendLine($"  - Attempt {a.Attempt}: {a.Status}{(a.Error is null ? string.Empty : " - " + Cell(a.Error))} ({a.DurationMs / 1000.0:0.0}s)");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Cleanup").AppendLine();
        foreach (var (k, v) in m.Cleanup)
        {
            sb.AppendLine($"- {k}: {Value(v)}");
        }

        if (m.Cleanup.Count == 0)
        {
            sb.AppendLine("- nothing to clean up");
        }

        sb.AppendLine();
        sb.AppendLine("## Suggestions (not done automatically)").AppendLine();
        foreach (var line in Suggestions(m))
        {
            sb.AppendLine("- " + line);
        }

        sb.AppendLine();
        sb.AppendLine("Files: `result.json` | `data\\checks.json` | `data\\step_status.json` | `data\\<step>.json` | `run.log` | `run-info.json` | `report.html`");
        return sb.ToString();
    }

    private static IEnumerable<string> Suggestions(ScenarioReportModel m)
    {
        var any = false;
        var nm = m.Checks.Where(r => r.Verdict == Verdicts.NotMeasurable).Select(r => r.Id).ToArray();
        if (nm.Length > 0)
        {
            any = true;
            yield return $"NOT_MEASURABLE checks (missing data or a TBD threshold): {string.Join(", ", nm)} - check the step that writes the data file, and the thresholds.";
        }

        if (m.Checks.Count == 0)
        {
            any = true;
            yield return "The scenario has no checks, so the verdict is at most SUSPECT. Add 'checks:' to scenario.yaml that read data\\<step>.json.";
        }

        if (m.Scenario.ManualSteps.Count > 0)
        {
            any = true;
            yield return $"The scenario lists manual_steps ({m.Scenario.ManualSteps.Count}), so the verdict is at most SUSPECT.";
        }

        foreach (var s in m.Steps.Where(s => s.Status == "BLOCKED"))
        {
            any = true;
            yield return $"UI step {s.Code} is BLOCKED: it could not reach the part under test - re-check the selectors (ui_snapshot / ui_find) and fix the UI script `{s.Target}`.";
        }

        if (m.EnvError is not null)
        {
            any = true;
            yield return "An environment or input problem stopped the run: fix it (dialog, Revit, file) and run again. It is not an add-in failure until proven.";
        }

        if (!any)
        {
            yield return "None. Every check PASSed - keep the scenario and replay it with scenario_run.";
        }
    }

    private static string Value(object? v) => v switch
    {
        bool b => b ? "true" : "false",
        null => "-",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "-",
    };

    private static string Cell(string? s, bool keepBreaks = false)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "-";
        }

        var t = s.Replace("|", "/").Replace("\r", string.Empty);
        t = keepBreaks ? t.Replace("\n", " ") : t.Replace("\n", " ").Replace("<br>", " ");
        return ResultFields.Clip(t, 600);
    }

    // ---- report.html --------------------------------------------------------------------------------

    public static string Html(ScenarioReportModel m)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        static string Badge(string v) => $"<span class=\"b b-{E(v.ToLowerInvariant())}\">{E(v)}</span>";
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{E(m.Scenario.Name)} — {E(m.RunId)}</title><style>");
        sb.Append(":root{--bg:#fff;--fg:#1d1d1f;--mut:#6e6e73;--line:#e3e3e8;--pass:#1a7f37;--fail:#cf222e;--warn:#9a6700;--err:#8250df}");
        sb.Append("@media (prefers-color-scheme:dark){:root{--bg:#161618;--fg:#ececf1;--mut:#a1a1aa;--line:#2c2c31;--pass:#3fb950;--fail:#ff7b72;--warn:#d29922;--err:#bc8cff}}");
        sb.Append("body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,Segoe UI,sans-serif;max-width:1200px}");
        sb.Append("h1{font-size:20px;margin:0 0 4px}h2{font-size:16px;margin:24px 0 8px}.mut{color:var(--mut)}");
        sb.Append("table{border-collapse:collapse;width:100%;display:block;overflow-x:auto}th,td{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left;vertical-align:top}");
        sb.Append("code{font:12px Consolas,monospace}.b{display:inline-block;padding:1px 8px;border-radius:10px;font-weight:600;font-size:12px;border:1px solid currentColor}");
        sb.Append(".b-pass,.b-succeeded{color:var(--pass)}.b-fail,.b-failed{color:var(--fail)}.b-suspect,.b-not_measurable,.b-blocked{color:var(--warn)}.b-error,.b-skipped{color:var(--err)}");
        sb.Append(".banner{border:1px solid var(--fail);color:var(--fail);padding:8px 12px;border-radius:6px;margin:12px 0}</style></head><body>");
        sb.Append($"<h1>{E(m.Scenario.Name)} {Badge(m.Verdict)}</h1>");
        sb.Append($"<div class=\"mut\">run {E(m.RunId)} · {m.Started:yyyy-MM-dd HH:mm} → {m.Ended:HH:mm} · Revit {E(m.Scenario.RevitVersion?.ToString(CultureInfo.InvariantCulture) ?? "?")} pid {E(m.Pid?.ToString(CultureInfo.InvariantCulture) ?? "-")} · RevitAgent {E(Mcp.McpGatewayServer.Version)}</div>");
        if (!string.IsNullOrWhiteSpace(m.Scenario.Description))
        {
            sb.Append($"<p>{E(m.Scenario.Description)}</p>");
        }

        if (m.ModelChanged)
        {
            sb.Append("<div class=\"banner\">ERROR: the original model file changed during the run</div>");
        }

        if (m.EnvError is not null)
        {
            sb.Append($"<div class=\"banner\">Stopped early - {E(m.EnvError)}</div>");
        }

        sb.Append($"<p>Checks: {m.Checks.Count(r => r.Verdict == Verdicts.Pass)} PASS · {m.Checks.Count(r => r.Verdict == Verdicts.Fail)} FAIL · {m.Checks.Count(r => r.Verdict == Verdicts.NotMeasurable)} NOT_MEASURABLE · steps: {m.Steps.Count(s => s.Status == "SUCCEEDED")}/{m.Steps.Count} SUCCEEDED</p>");
        sb.Append("<h2>Checks</h2><table><tr><th>Check</th><th>Title</th><th>Severity</th><th>Verdict</th><th>Expect</th><th>Terms</th></tr>");
        foreach (var r in m.Checks)
        {
            var detail = string.Join("<br>", r.Terms.Select(c => (c.Text.Length > 0 ? $"<code>{E(c.Text)}</code> -> " : string.Empty) + $"{Badge(c.Verdict)} {E(c.Detail)}"));
            sb.Append($"<tr><td>{E(r.Id)}</td><td>{E(r.Title)}</td><td>{E(r.Severity)}</td><td>{Badge(r.Verdict)}</td><td><code>{E(r.Expect)}</code></td><td>{detail}</td></tr>");
        }

        if (m.Checks.Count == 0)
        {
            sb.Append("<tr><td colspan=\"6\" class=\"mut\">No checks in scenario.yaml - the verdict is at most SUSPECT.</td></tr>");
        }

        sb.Append("</table><h2>Steps</h2><table><tr><th>#</th><th>Step</th><th>Kind</th><th>Result</th><th>Duration</th><th>Data</th></tr>");
        foreach (var s in m.Steps)
        {
            var data = s.DataFile is null ? string.Empty : $"<a href=\"{E(s.DataFile.Replace('\\', '/'))}\">{E(s.DataFile)}</a>";
            sb.Append($"<tr><td>{s.Index + 1}</td><td>{E(s.Code)}{(s.Setup ? " <span class=\"mut\">(setup)</span>" : string.Empty)}</td><td>{E(s.Kind)} <code>{E(s.Target)}</code></td><td>{Badge(s.Status)} {E(s.Message)}</td><td>{s.DurationMs / 1000.0:0.0}s</td><td>{data}</td></tr>");
        }

        sb.Append("</table><h2>Cleanup</h2><ul>");
        foreach (var (k, v) in m.Cleanup)
        {
            sb.Append($"<li>{E(k)}: {E(Value(v))}</li>");
        }

        sb.Append("</ul><p class=\"mut\"><a href=\"report.md\">report.md</a> · <a href=\"result.json\">result.json</a> · <a href=\"data/checks.json\">data/checks.json</a> · <a href=\"data/step_status.json\">data/step_status.json</a> · <a href=\"run.log\">run.log</a> · <a href=\"run-info.json\">run-info.json</a></p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
