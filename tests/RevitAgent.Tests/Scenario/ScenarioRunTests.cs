using System.IO;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Scenario;

namespace RevitAgent.Tests.Scenarios;

public class ScenarioRunTests
{
    private const string LevelsSteps = """
        name: levels-inventory
        revit_version: 2024
        model: 'input\model.rvt'
        steps:
          - name: schedulable
            tool: schedule_list_schedulable_categories
            on_error: continue
          - name: levels
            tool: model_query_elements
            args: { category: Levels, fields: [Name, Elevation] }
          - name: make-schedule
            ui: 'ui\01-schedule.json'
            timeout_sec: 60
          - name: room-schedule
            tool: schedule_read
            args: { name: 'Room Schedule' }
        """;

    private const string LevelsChecks = """
        checks:
          - id: C1
            title: Has 3 levels
            expect: 'count(levels.json -> elements) == 3 and count(levels.json -> elements[name="Roof"]) == 1'
          - id: C2
            title: Levels can be scheduled (Revit 2024)
            expect: 'count(schedulable.json -> categories[name="Levels"]) == 1'
          - id: C3
            title: UI schedule step ran
            expect: 'step_ok(make-schedule) and count(room-schedule.json -> fields) == 3'
          - id: C4
            title: No STOP in run.log
            expect: 'no_line(run.log, "STOP ")'
        """;

    private const string UiScript = """{ "name": "schedule", "steps": [ { "action": "click", "selector": "id=ID_VIEW_TAB" }, { "action": "expect", "window": "Schedule Properties", "is": "gone" } ] }""";

    /// <summary>scenario.yaml = metadata + steps, then the checks.</summary>
    private static string LevelsScenario(string? steps = null, string? checks = null) =>
        ScenarioHost.NewScenario("levels-inventory",
            ("scenario.yaml", (steps ?? LevelsSteps) + "\n" + (checks ?? LevelsChecks)),
            ("ui/01-schedule.json", UiScript),
            ("input/model.rvt", "fake revit model bytes"));

    [Fact]
    public async Task FullRun_Launch_PassesAndCleansUp()
    {
        await using var host = new ScenarioHost();
        var dir = LevelsScenario();
        var originalHash = Hash(Path.Combine(dir, "input", "model.rvt"));

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, launch = true });

        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        Assert.Equal(4, s.GetProperty("checks").GetProperty("pass").GetInt32());
        Assert.Equal(4, s.GetProperty("steps").GetProperty("succeeded").GetInt32());
        Assert.Equal(FakeRevit.LaunchedPid, s.GetProperty("pid").GetInt32());
        var runDir = s.GetProperty("run_dir").GetString()!;
        Assert.StartsWith(host.Paths.RunsDir, runDir);

        // launch + wait, copy into RUN_DIR, open the copy, verify
        Assert.Equal(2024, host.Revit.Last("revit_launch").GetProperty("year").GetInt32());
        Assert.Equal(1, host.Revit.Count("revit_wait_ready"));
        var opened = host.Revit.Tools["doc_open"].Calls.Single().GetProperty("path").GetString()!;
        Assert.StartsWith(runDir, opened);
        Assert.Equal(FakeRevit.LaunchedPid, host.Revit.Last("doc_open").GetProperty("pid").GetInt32());
        Assert.Equal(FakeRevit.LaunchedPid, host.Revit.Last("model_query_elements").GetProperty("pid").GetInt32());
        Assert.Equal(4, host.Revit.Count("revit_get_state"));

        // ui step through ui_run_script with an out_dir inside RUN_DIR\data\ui
        var ui = host.Revit.Last("ui_run_script");
        Assert.EndsWith(Path.Combine("ui", "01-schedule.json"), ui.GetProperty("script_path").GetString());
        Assert.StartsWith(Path.Combine(runDir, "data", "ui", "03-make-schedule"), ui.GetProperty("out_dir").GetString());

        // cleanup: copy closed without saving, Revit launched by the run closed, copy kept (never deleted), original untouched
        Assert.False(host.Revit.Last("doc_close").GetProperty("save").GetBoolean());
        Assert.Equal(FakeRevit.LaunchedPid, host.Revit.Last("revit_close").GetProperty("pid").GetInt32());
        Assert.True(File.Exists(opened));
        Assert.Equal(originalHash, Hash(Path.Combine(dir, "input", "model.rvt")));
        Assert.True(s.GetProperty("cleanup").GetProperty("original_model_unchanged").GetBoolean());

        // outputs
        foreach (var f in new[] { "report.md", "report.html", "result.json", "run-info.json", "run.log", @"data\step_status.json", @"data\checks.json", @"data\levels.json", @"data\make-schedule.json", @"scenario\scenario.yaml" })
        {
            Assert.True(File.Exists(Path.Combine(runDir, f)), f);
        }

        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "data", "step_status.json")));
        Assert.Equal("", status.RootElement.GetProperty("externalRunId").GetString());
        Assert.Equal("steps", status.RootElement.GetProperty("source").GetString());
        Assert.True(status.RootElement.GetProperty("ok").GetBoolean());
        var steps = status.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(new[] { "schedulable", "levels", "make-schedule", "room-schedule" }, steps.Select(x => x.GetProperty("stepCode").GetString()));
        Assert.All(steps, x => Assert.Equal("SUCCEEDED", x.GetProperty("status").GetString()));
        Assert.Equal("ui", steps[2].GetProperty("kind").GetString());

        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "result.json")));
        var root = result.RootElement;
        Assert.Equal("levels-inventory", root.GetProperty("scenario").GetString());
        Assert.Equal("PASS", root.GetProperty("verdict").GetString());
        var r1 = root.GetProperty("checks")[0];
        Assert.Equal("C1", r1.GetProperty("id").GetString());
        foreach (var key in new[] { "title", "severity", "expect", "actual", "evidence", "verdict", "note", "key" })
        {
            Assert.True(r1.TryGetProperty(key, out _), key);
        }

        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "run-info.json")));
        Assert.Equal("scenario_run", info.RootElement.GetProperty("runner").GetString());
        Assert.Equal(Environment.MachineName, info.RootElement.GetProperty("machine").GetString());
        Assert.True(info.RootElement.GetProperty("revit").GetProperty("launched_by_run").GetBoolean());
        Assert.True(info.RootElement.TryGetProperty("ended", out _));

        var md = File.ReadAllText(Path.Combine(runDir, "report.md"));
        Assert.Contains("Result: **PASS**", md);
        Assert.Contains("## Step log", md);
        Assert.Contains("<html", File.ReadAllText(Path.Combine(runDir, "report.html")));

        var listed = await host.CallOkAsync("scenario_list_runs", new { scenario = "levels" });
        Assert.Equal(1, listed.GetProperty("total").GetInt32());
        Assert.Equal("PASS", listed.GetProperty("runs")[0].GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Cleanup_ClosesTheSandboxCopyExplicitly_NeverTheActiveDefault()
    {
        await using var host = new ScenarioHost();
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        var opened = host.Revit.Tools["doc_open"].Calls.Single().GetProperty("path").GetString()!;
        Assert.Equal(opened, host.Revit.Last("doc_close").GetProperty("path").GetString());
        Assert.True(s.GetProperty("cleanup").GetProperty("doc_closed").GetBoolean());
    }

    [Fact]
    public async Task Cleanup_CloseConflict_IsReportedLoudly_AndRevitClosedWithDiscard()
    {
        await using var host = new ScenarioHost();
        host.Revit.CloseConflict = true;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), launch = true });
        Assert.Contains("WARNING", s.GetProperty("error").GetString());
        Assert.Contains("WARNING", s.GetProperty("cleanup").GetProperty("WARNING").GetString());
        Assert.True(host.Revit.Last("revit_close").GetProperty("discard_changes").GetBoolean());
        Assert.Contains("WARNING", File.ReadAllText(Path.Combine(s.GetProperty("run_dir").GetString()!, "report.md")));
    }

    [Fact]
    public async Task Cleanup_CloseConflict_OnUserRevit_WarnsToCloseManually_AndClosesNothingElse()
    {
        await using var host = new ScenarioHost();
        host.Revit.CloseConflict = true;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Contains("close it manually", s.GetProperty("error").GetString());
        Assert.Contains("not launched by this run", s.GetProperty("error").GetString());
        Assert.DoesNotContain("user's Revit", s.GetProperty("error").GetString());
        Assert.Equal(0, host.Revit.Count("revit_close"));
        Assert.Equal(1, host.Revit.Count("doc_close"));
    }

    [Fact]
    public async Task Cleanup_CopyIsActiveInAnAttachedRevit_SwitchesBackToTheUsersDocument_ThenClosesTheCopy()
    {
        await using var host = new ScenarioHost();
        var user = @"C:\Projects\user-model.rvt";
        host.Revit.UserDocument = user;
        host.Revit.ActiveDoc = user;
        host.Revit.CloseConflict = true;

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });

        var close = host.Revit.Last("doc_close");
        Assert.True(close.GetProperty("activate_other").GetBoolean());
        Assert.Equal(user, close.GetProperty("activate_document").GetString());
        Assert.Equal(host.Revit.Tools["doc_open"].Calls.Single().GetProperty("path").GetString(), close.GetProperty("path").GetString());
        Assert.True(s.GetProperty("cleanup").GetProperty("doc_closed").GetBoolean());
        Assert.Contains(user, s.GetProperty("cleanup").GetProperty("doc_close_note").GetString());
        Assert.False(s.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && e.GetString()!.Contains("WARNING"));
        Assert.Equal(user, host.Revit.ActiveDoc);
        Assert.Equal(0, host.Revit.Count("revit_close"));
    }

    [Fact]
    public async Task DocOpenTimeoutAfterOpening_StillClosesTheCopy()
    {
        await using var host = new ScenarioHost();
        host.Revit.DocOpenFailsAfterOpen = true;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Equal(1, host.Revit.Count("doc_close"));
        Assert.True(s.GetProperty("cleanup").GetProperty("doc_closed").GetBoolean());
        Assert.Null(host.Revit.ActiveDoc);
    }

    [Fact]
    public async Task LaunchFailingAfterRevitStarted_ClosesTheNewGatewayLaunchedRevit()
    {
        await using var host = new ScenarioHost();
        host.Revit.LaunchFailsAfterStart = true;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), launch = true });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Equal(FakeRevit.LaunchedPid, host.Revit.Last("revit_close").GetProperty("pid").GetInt32());
        Assert.Equal(1, host.Revit.Count("revit_close"));
        Assert.Equal(0, host.Revit.Count("doc_open"));
    }

    [Theory]
    [InlineData(GatewayErrorCodes.PermissionDenied)] // the policy refused: never kill after that
    [InlineData(GatewayErrorCodes.Timeout)]          // the process is not in the launched registry: not ours to kill
    public async Task FailedRevitClose_NeverFallsBackToKillingAProcessTheGatewayDoesNotOwn(string code)
    {
        await using var host = new ScenarioHost();
        host.Revit.CloseError = code;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), launch = true });

        Assert.Equal(1, host.Revit.Count("revit_close"));
        var cleanup = s.GetProperty("cleanup");
        Assert.False(cleanup.GetProperty("revit_closed").GetBoolean());
        Assert.False(cleanup.TryGetProperty("revit_killed", out _));
        Assert.Contains(code, cleanup.GetProperty("revit_close_error").GetString());
    }

    [Fact]
    public async Task ExistingPid_IsNeverClosed()
    {
        await using var host = new ScenarioHost();
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        Assert.Equal(0, host.Revit.Count("revit_launch"));
        Assert.Equal(0, host.Revit.Count("revit_close"));
        Assert.Equal(1, host.Revit.Count("doc_close"));
        Assert.False(s.GetProperty("cleanup").GetProperty("revit_closed").GetBoolean());
    }

    [Fact]
    public async Task OnErrorStop_SkipsRest_Error_ButStillCleansUp()
    {
        await using var host = new ScenarioHost();
        var dir = LevelsScenario(steps: LevelsSteps.Replace("tool: schedule_list_schedulable_categories", "tool: fail_tool").Replace("on_error: continue", "on_error: stop"));
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, launch = true });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Equal(1, s.GetProperty("steps").GetProperty("failed").GetInt32());
        Assert.Equal(3, s.GetProperty("steps").GetProperty("skipped").GetInt32());
        Assert.Equal(0, host.Revit.Count("model_query_elements"));
        Assert.Equal(1, host.Revit.Count("doc_close"));
        Assert.Equal(1, host.Revit.Count("revit_close"));
        var runDir = s.GetProperty("run_dir").GetString()!;
        Assert.True(File.Exists(Path.Combine(runDir, "data", "schedulable.error.json")));
        Assert.False(File.Exists(Path.Combine(runDir, "data", "schedulable.json")));
        Assert.Contains("boom", File.ReadAllText(Path.Combine(runDir, "report.md")));
    }

    [Fact]
    public async Task OnErrorContinue_RunsNextSteps_ChecksDecide()
    {
        await using var host = new ScenarioHost();
        var steps = LevelsSteps.Replace("tool: schedule_list_schedulable_categories", "tool: crash_tool");
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(steps: steps), pid = FakeRevit.ExistingPid });
        Assert.Equal(1, s.GetProperty("steps").GetProperty("failed").GetInt32());
        Assert.Equal(3, s.GetProperty("steps").GetProperty("succeeded").GetInt32());
        // C2 reads schedulable.json which the failed step did not write → NOT_MEASURABLE → SUSPECT, never PASS
        Assert.Equal("SUSPECT", s.GetProperty("verdict").GetString());
        Assert.Equal(1, s.GetProperty("checks").GetProperty("not_measurable").GetInt32());
        var runDir = s.GetProperty("run_dir").GetString()!;
        Assert.DoesNotContain("secret internals", File.ReadAllText(Path.Combine(runDir, "data", "schedulable.error.json")));
    }

    [Fact]
    public async Task ModalDialog_IsEnvError_StopsAndCleansUp()
    {
        await using var host = new ScenarioHost();
        host.Revit.State = i => i >= 1 ? new { state = "modal", blocking_dialogs = new[] { new { title = "Security - Unsigned Add-In" } } } : null;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), launch = true });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.StartsWith("ENV:", s.GetProperty("error").GetString());
        Assert.Contains("Security - Unsigned Add-In", s.GetProperty("error").GetString());
        Assert.Equal(1, s.GetProperty("steps").GetProperty("succeeded").GetInt32());
        Assert.Equal(1, s.GetProperty("steps").GetProperty("error").GetInt32());
        Assert.Equal(2, s.GetProperty("steps").GetProperty("skipped").GetInt32());
        Assert.Equal(1, host.Revit.Count("doc_close"));
        Assert.Equal(1, host.Revit.Count("revit_close"));
    }

    [Fact]
    public async Task DocumentOutsideRunDir_IsRefused_BeforeAnyStep()
    {
        await using var host = new ScenarioHost();
        host.Revit.DocInfoOutside = true;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Contains("not the sandbox copy", s.GetProperty("error").GetString());
        Assert.Equal(0, host.Revit.Count("model_query_elements"));
        Assert.Equal(4, s.GetProperty("steps").GetProperty("skipped").GetInt32());
        Assert.Equal(1, host.Revit.Count("doc_close"));
    }

    [Fact]
    public async Task BusyRevit_IsWaitedFor_ThenEnvError()
    {
        await using var host = new ScenarioHost();
        var dir = LevelsScenario();
        host.Revit.State = i => i == 0 ? new { state = "busy" } : null;
        var ok = await host.Runner().RunAsync(new ScenarioRunOptions { Scenario = dir, Pid = FakeRevit.ExistingPid }, CancellationToken.None);
        Assert.Equal("PASS", ok.Verdict);

        host.Revit.State = _ => new { state = "busy" };
        var stuck = await host.Runner().RunAsync(new ScenarioRunOptions { Scenario = dir, Pid = FakeRevit.ExistingPid }, CancellationToken.None);
        Assert.Equal("ERROR", stuck.Verdict);
        Assert.Contains("busy", stuck.Error);
    }

    [Fact]
    public async Task UiBlocked_ContinuesAndIsNotMeasurable()
    {
        await using var host = new ScenarioHost();
        host.Revit.UiStatus = "blocked";
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("SUSPECT", s.GetProperty("verdict").GetString());
        Assert.Equal(1, s.GetProperty("steps").GetProperty("blocked").GetInt32());
        Assert.Equal(1, host.Revit.Count("schedule_read"));
        var runDir = s.GetProperty("run_dir").GetString()!;
        Assert.False(File.Exists(Path.Combine(runDir, "data", "make-schedule.json")));
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "result.json")));
        var r3 = result.RootElement.GetProperty("checks").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "C3");
        Assert.Equal("NOT_MEASURABLE", r3.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task UiStep_LeavingAWindowOpen_Fails()
    {
        await using var host = new ScenarioHost();
        host.Revit.LeaveWindowOpen = true;
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Contains("Schedule Properties", s.GetProperty("failed_steps")[0].GetProperty("message").GetString());

        var leaveOpen = LevelsSteps.Replace("timeout_sec: 60", "timeout_sec: 60\n    leave_open: true");
        var s2 = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(steps: leaveOpen), pid = FakeRevit.ExistingPid });
        Assert.Equal("PASS", s2.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Variables_AndPidInjection()
    {
        await using var host = new ScenarioHost();
        var ds = TestGateway.NewTempDir("revitagent_ds_");
        File.WriteAllText(Path.Combine(ds, "rooms.csv"), "x");
        var dir = ScenarioHost.NewScenario("vars",
            ("scenario.yaml", """
                name: vars
                model: 'input\model.rvt'
                steps:
                  - name: echo
                    tool: echo
                    args: { path: '{run_dir}\out.txt', model: '{model}', pid_text: 'pid={pid}', csv: 'input\a.csv', ds: '{dataset}\rooms.csv', list: ['{task_dir}', 'x'] }
                  - name: nopid
                    tool: no_pid_tool
                    args: { x: '1' }
                """),
            ("input/model.rvt", "m"),
            ("input/a.csv", "a"));
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, pid = FakeRevit.ExistingPid, dataset = ds });
        var runDir = s.GetProperty("run_dir").GetString()!;
        var a = host.Revit.Last("echo");
        Assert.Equal(Path.Combine(runDir, "out.txt"), a.GetProperty("path").GetString());
        Assert.Equal(Path.Combine(runDir, "model.rvt"), a.GetProperty("model").GetString());
        Assert.Equal($"pid={FakeRevit.ExistingPid}", a.GetProperty("pid_text").GetString());
        Assert.Equal(Path.Combine(dir, "input", "a.csv"), a.GetProperty("csv").GetString());
        Assert.Equal(Path.Combine(ds, "rooms.csv"), a.GetProperty("ds").GetString());
        Assert.Equal(dir, a.GetProperty("list")[0].GetString());
        Assert.Equal(FakeRevit.ExistingPid, a.GetProperty("pid").GetInt32());
        Assert.False(host.Revit.Last("no_pid_tool").TryGetProperty("pid", out _));
        Assert.Equal("SUSPECT", s.GetProperty("verdict").GetString()); // no checks → never PASS
    }

    [Fact]
    public async Task InvalidScenario_IsRefused_NothingRuns()
    {
        await using var host = new ScenarioHost();
        var dir = ScenarioHost.NewScenario("bad", ("scenario.yaml", "name: bad\nsteps:\n  - tool: nope\n"));
        var r = await host.CallAsync("scenario_run", new { scenario_dir = dir, pid = FakeRevit.ExistingPid });
        Assert.True(r.IsError);
        Assert.Equal("VALIDATION_FAILED", r.ErrorCode);
        Assert.Contains("tool-unknown", ((RevitAgent.Core.Tools.TextContent)r.Content[0]).Text);
        Assert.Empty(Directory.EnumerateDirectories(host.Paths.RunsDir));

        var needsPid = await host.CallAsync("scenario_run", new { scenario_dir = LevelsScenario() });
        Assert.Equal("VALIDATION_FAILED", needsPid.ErrorCode);
        var both = await host.CallAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = 1, launch = true });
        Assert.Equal("VALIDATION_FAILED", both.ErrorCode);
    }

    [Fact]
    public async Task WithoutSandboxTools_FallsBackToRunsFolder()
    {
        await using var host = new ScenarioHost(without: new[] { "sandbox_create_run", "sandbox_copy_model", "revit_get_state", "ui_list_windows" });
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        var runDir = s.GetProperty("run_dir").GetString()!;
        Assert.StartsWith(host.Paths.RunsDir, runDir);
        Assert.Contains("levels-inventory", Path.GetFileName(runDir));
    }

    [Fact]
    public async Task MissingBridge_IsEnvError()
    {
        await using var host = new ScenarioHost(without: new[] { "doc_open" });
        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = LevelsScenario(), pid = FakeRevit.ExistingPid });
        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Contains("doc_open", s.GetProperty("error").GetString());
    }

    [Fact]
    public void Verdict_Precedence_ErrorThenFailThenSuspectThenPass()
    {
        static CheckEvaluation C(string verdict, string severity = CheckSeverity.Fail) => new() { Id = "C-" + verdict, Verdict = verdict, Severity = severity };
        Assert.Equal("ERROR", ScenarioRunner.ComputeVerdict(true, false, false, 0, new[] { C("PASS") }));
        Assert.Equal("ERROR", ScenarioRunner.ComputeVerdict(false, false, true, 0, new[] { C("PASS") }));
        Assert.Equal("FAIL", ScenarioRunner.ComputeVerdict(false, false, false, 0, new[] { C("PASS"), C("FAIL"), C("NOT_MEASURABLE") }));
        Assert.Equal("SUSPECT", ScenarioRunner.ComputeVerdict(false, false, false, 0, new[] { C("PASS"), C("NOT_MEASURABLE") }));
        Assert.Equal("SUSPECT", ScenarioRunner.ComputeVerdict(false, false, false, 0, new[] { C("PASS"), C("FAIL", CheckSeverity.Warn) })); // a warn check cannot FAIL the run
        Assert.Equal("FAIL", ScenarioRunner.ComputeVerdict(false, false, false, 0, new[] { C("FAIL", CheckSeverity.Warn), C("FAIL") }));
        Assert.Equal("SUSPECT", ScenarioRunner.ComputeVerdict(false, false, false, 1, new[] { C("PASS") }));
        Assert.Equal("SUSPECT", ScenarioRunner.ComputeVerdict(false, false, false, 0, Array.Empty<CheckEvaluation>()));
        Assert.Equal("PASS", ScenarioRunner.ComputeVerdict(false, false, false, 0, new[] { C("PASS"), C("PASS", CheckSeverity.Warn) }));
    }

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
