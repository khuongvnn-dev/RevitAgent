using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Core.Scenario;

namespace RevitAgent.Tests.Scenarios;

public class ScenarioLoaderTests
{
    [Fact]
    public void Parse_TypesPlainScalars_KeepsQuotedStrings_AndReadsSteps()
    {
        var r = ScenarioLoader.Parse("""
            name: demo
            revit_version: 2024
            model: '{dataset}\model.rvt'
            keep_model_copy: true
            timeouts: { startup_min: 2, open_model_min: 3, step_min: 0.5 }
            steps:
              - tool: model_query_elements
                name: levels
                args: { category: Levels, limit: 50, exact: true, code: "42", ratio: 1.5, none: ~, fields: [Name, 'Elevation'] }
                on_error: continue
              - name: ui-step
                ui: 'ui\a.json'
                timeout_sec: 120
                leave_open: true
              - tool: fix_data
                purpose: setup
                why: "K2 — element deleted"
            """, @"C:\tmp\demo");
        Assert.False(r.HasErrors, string.Join("; ", r.Issues));
        var s = r.Scenario!;
        Assert.Equal("demo", s.Name);
        Assert.Equal(2024, s.RevitVersion);
        Assert.Equal(@"{dataset}\model.rvt", s.Model);
        Assert.True(s.KeepModelCopy);
        Assert.Equal(0.5, s.Timeouts.StepMin);
        var a = s.Steps[0].Args;
        Assert.Equal(JsonValueKind.String, a["category"]!.GetValueKind());
        Assert.Equal(50, a["limit"]!.GetValue<long>());
        Assert.True(a["exact"]!.GetValue<bool>());
        Assert.Equal("42", a["code"]!.GetValue<string>());
        Assert.Equal(1.5, a["ratio"]!.GetValue<double>());
        Assert.Null(a["none"]);
        Assert.Equal(2, ((JsonArray)a["fields"]!).Count);
        Assert.True(s.Steps[0].ContinueOnError);
        Assert.Equal("levels", s.Steps[0].Code);
        Assert.True(s.Steps[1].IsUi);
        Assert.Equal(120, s.Steps[1].TimeoutSec);
        Assert.True(s.Steps[1].LeaveOpen);
        Assert.True(s.Steps[2].IsSetup);
        Assert.Equal("fix_data", s.Steps[2].Code);
    }

    [Fact]
    public void Parse_YamlError_ReportsLine()
    {
        var r = ScenarioLoader.Parse("name: demo\nsteps:\n  - tool: a\n    args: {dataset}\\x: [\n", @"C:\tmp\demo");
        var e = Assert.Single(r.Issues, i => i.Code == "yaml-syntax");
        Assert.Equal(IssueSeverity.Error, e.Severity);
        Assert.Equal(4, e.Line);
        Assert.Null(r.Scenario);
    }

    [Fact]
    public void Parse_StepErrors_AndUnknownKeys()
    {
        var r = ScenarioLoader.Parse("""
            name: demo
            mystery: 1
            steps:
              - name: both
                tool: a
                ui: 'ui\a.json'
              - tool: b
                on_error: maybe
                purpose: destroy
            """, @"C:\tmp\demo");
        Assert.Contains(r.Issues, i => i.Code == "step-kind" && i.Line == 4);
        Assert.Contains(r.Issues, i => i.Code == "step-on-error");
        Assert.Contains(r.Issues, i => i.Code == "step-purpose");
        Assert.Contains(r.Issues, i => i.Code == "unknown-key" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Variables_ExpandKnownPlaceholders_AndInputPaths()
    {
        var task = TestGateway.NewTempDir("revitagent_task_");
        var ds = TestGateway.NewTempDir("revitagent_ds_");
        Directory.CreateDirectory(Path.Combine(task, "input"));
        File.WriteAllText(Path.Combine(task, "input", "override.csv"), "x");
        var v = new ScenarioVariables(task).Set("dataset", ds).Set("pid", "12").Set("run_dir", @"C:\runs\r1");
        Assert.Equal(@"C:\runs\r1\out.txt", v.ExpandPath(@"{run_dir}\out.txt"));
        Assert.Equal(Path.Combine(task, "input", "override.csv"), v.ExpandPath(@"{dataset}\override.csv"));
        Assert.Equal(Path.Combine(ds, "base.csv"), v.ExpandPath(@"{dataset}\base.csv"));
        Assert.Equal(Path.Combine(task, "input", "a.csv"), v.ExpandPath(@"input\a.csv"));
        Assert.Equal("Levels", v.ExpandPath("Levels"));
        Assert.Equal("{unknown} pid 12", v.Expand("{unknown} pid {pid}"));
        Assert.Throws<InvalidOperationException>(() => v.Expand("{model}"));
        Assert.Equal(new[] { "nope" }, ScenarioVariables.UnknownPlaceholders("{nope} {pid} \\d{3}"));
    }
}

public class ScenarioValidatorTests
{
    private const string GoodChecks = """
        checks:
          - id: C1
            title: levels exist
            expect: 'count(levels.json -> elements) >= 1 and step_ok(levels)'
        """;

    private const string LevelsSteps = "name: demo\nsteps:\n  - name: levels\n    tool: model_query_elements\n    args: { category: Levels }\n";

    [Fact]
    public async Task ValidScenario_IsOk()
    {
        await using var host = new ScenarioHost();
        var dir = ScenarioHost.NewScenario("demo",
            ("scenario.yaml", LevelsSteps + GoodChecks));
        var v = ScenarioValidator.Validate(dir, host.Registry);
        Assert.True(v.Ok, string.Join("; ", v.Errors));
        Assert.Empty(v.Warnings);

        var tool = await host.CallOkAsync("scenario_validate", new { scenario_dir = dir });
        Assert.True(tool.GetProperty("ok").GetBoolean());
        Assert.Equal(1, tool.GetProperty("checks").GetInt32());
    }

    [Fact]
    public async Task Errors_UnknownForbiddenAdminSetupModelArgs()
    {
        await using var host = new ScenarioHost();
        var dir = ScenarioHost.NewScenario("demo",
            ("scenario.yaml", """
                name: demo
                model: 'input\missing.rvt'
                steps:
                  - tool: no_such_tool
                  - tool: revit_close
                  - tool: scenario_run
                  - tool: echo
                    purpose: setup
                  - tool: model_query_elements
                    args: { categoryy: Levels }
                  - tool: doc_close
                    args: { save: true }
                """ + "\n" + GoodChecks));
        var v = ScenarioValidator.Validate(dir, host.Registry);
        Assert.False(v.Ok);
        var codes = v.Errors.Select(e => e.Code).ToArray();
        Assert.Contains("tool-unknown", codes);
        Assert.Contains("model-missing", codes);
        Assert.Contains("setup-why", codes);
        Assert.Equal(3, v.Errors.Count(e => e.Code == "tool-forbidden"));
        Assert.Equal(2, v.Errors.Count(e => e.Code == "tool-arg"));
        Assert.Equal(4, v.Errors.First(e => e.Code == "tool-unknown").Line);
    }

    [Fact]
    public async Task UiScripts_ForbiddenContent()
    {
        await using var host = new ScenarioHost();
        var dir = ScenarioHost.NewScenario("demo",
            ("scenario.yaml", """
                name: demo
                steps:
                  - name: bad-ui
                    ui: 'ui\bad.json'
                  - name: gone
                    ui: 'ui\missing.json'
                  - name: setup-ui
                    ui: 'ui\ok.json'
                    purpose: setup
                    why: x
                """ + "\n" + GoodChecks.Replace("levels", "bad-ui")),
            ("ui/bad.json", """
                { "dialogs": "rules.json", "steps": [
                  { "action": "launch", "selector": "Revit.exe" },
                  { "action": "run", "selector": "other.json" },
                  { "action": "close" },
                  { "action": "keys", "keys": "%{F4}" },
                  { "action": "type", "selector": "id=txtPassword", "text": "x" },
                  { "action": "click", "window": "Security - Unsigned Add-In", "selector": "name=Always Load" },
                  { "action": "frobnicate" },
                  { "action": "keys", "keys": "^s" }
                ] }
                """),
            ("ui/rules.json", """[ { "name": "auto-recover", "title": "File Recovery", "click": "id=1" } ]"""),
            ("ui/ok.json", """{ "steps": [ { "action": "click", "selector": "id=ID_VIEW" } ] }"""));
        var v = ScenarioValidator.Validate(dir, host.Registry);
        var errs = v.Errors.ToArray();
        Assert.Equal(4, errs.Count(e => e.Code == "ui-forbidden"));
        Assert.Contains(errs, e => e.Code == "ui-secret");
        Assert.Contains(errs, e => e.Code == "ui-forbidden-dialog");
        Assert.Contains(errs, e => e.Code == "ui-dialog-rule");
        Assert.Contains(errs, e => e.Code == "ui-action");
        Assert.Contains(errs, e => e.Code == "ui-missing");
        Assert.Contains(errs, e => e.Code == "ui-setup");
        Assert.Contains(v.Warnings, w => w.Code == "ui-save");
    }

    [Fact]
    public async Task Checks_DuplicateIds_BadExpressions_UnknownRefs()
    {
        await using var host = new ScenarioHost();
        var dir = ScenarioHost.NewScenario("demo",
            ("scenario.yaml", LevelsSteps + """
                checks:
                  - id: C1
                    title: not an expression
                    expect: 'looks fine to a human'
                  - id: C1
                    title: dup
                    expect: 'step_ok(nope) and count(other.json -> x) == 1'
                  - id: C2
                    title: no expect
                  - id: C3
                    expect: 'step_ok(levels)'
                    severity: loud
                """));
        var v = ScenarioValidator.Validate(dir, host.Registry);
        Assert.Contains(v.Errors, e => e.Code == "check-duplicate" && e.Line == 10);
        Assert.Contains(v.Errors, e => e.Code == "check-expect" && e.Message.StartsWith("C1:"));
        Assert.Contains(v.Errors, e => e.Code == "check-expect" && e.Message.Contains("C2"));
        Assert.Contains(v.Errors, e => e.Code == "check-severity");
        Assert.Contains(v.Warnings, w => w.Code == "check-step");
        Assert.Contains(v.Warnings, w => w.Code == "check-file");
        Assert.Contains(v.Warnings, w => w.Code == "check-title");
        Assert.False(v.Ok);
    }

    [Fact]
    public void Checks_AreLoaded_WithSeverityAndNote()
    {
        var r = ScenarioLoader.Parse("""
            name: demo
            steps:
              - tool: doc_info
            checks:
              - id: C1
                title: Has levels
                expect: 'count(levels.json -> elements) >= 1'
              - id: C2
                title: Soft
                expect: step_ok(doc)
                severity: warn
                note: how it is measured
            """, @"C:\tmp\demo");
        Assert.False(r.HasErrors, string.Join("; ", r.Issues));
        var c = r.Scenario!.Checks;
        Assert.Equal(2, c.Count);
        Assert.Equal(CheckSeverity.Fail, c[0].Severity);
        Assert.Equal(CheckSeverity.Warn, c[1].Severity);
        Assert.Equal("how it is measured", c[1].Note);
        Assert.Equal("step_ok(doc)", c[1].Expect);
    }

    [Fact]
    public async Task MissingScenarioFile_AndMissingChecks()
    {
        await using var host = new ScenarioHost();
        var empty = ScenarioHost.NewScenario("empty");
        Assert.Contains(ScenarioValidator.Validate(empty, host.Registry).Errors, e => e.Code == "scenario-missing");

        var noChecks = ScenarioHost.NewScenario("nochecks", ("scenario.yaml", "name: nochecks\nsteps:\n  - tool: doc_info\n"));
        var v = ScenarioValidator.Validate(noChecks, host.Registry);
        Assert.True(v.Ok);
        Assert.Contains(v.Warnings, w => w.Code == "checks-missing");

        var r = await host.CallAsync("scenario_validate", new { scenario_dir = Path.Combine(empty, "nope") });
        Assert.Equal("NOT_FOUND", r.ErrorCode);
    }
}
