using System.IO;
using System.Text.Json;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Scenario.Recorder;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Scenarios;

public class RecorderTests
{
    private static async Task RecordRoomScheduleExploration(ScenarioHost host, string session = "s1")
    {
        var pid = FakeRevit.ExistingPid;
        await host.CallOkAsync("ui_snapshot", new { pid }, session);                                   // Read → not recorded
        await host.CallOkAsync("ui_click", new { pid, selector = "id=ID_VIEW_TAB" }, session);
        await host.CallOkAsync("ui_click", new { pid, @ref = "e7" }, session);                         // ref resolved by the result
        await host.CallOkAsync("ui_wait", new { pid, window = "New Schedule", timeout_sec = 30 }, session);
        await host.CallOkAsync("ui_select", new { pid, window = "New Schedule", selector = "id=lstCategories", item = "Rooms" }, session);
        await host.CallOkAsync("ui_click", new { pid, window = "New Schedule", @ref = "e99" }, session); // unresolved ref → TODO
        await host.CallOkAsync("ui_expect", new { pid, window = "Schedule Properties", @is = "gone" }, session);
        var failed = await host.CallAsync("fail_tool", new { pid }, session);                        // failed → not recorded
        Assert.True(failed.IsError);
        await host.CallOkAsync("schedule_read", new { pid, name = "Room Schedule" }, session);
        await host.CallOkAsync("model_query_elements", new { pid, category = "Levels", fields = new[] { "Name" } }, session);
    }

    [Fact]
    public async Task Records_OnlyRelevantSuccessfulCalls_OfTheRecordingSession()
    {
        await using var host = new ScenarioHost();
        await host.CallOkAsync("recorder_start", new { name = "room schedule" });
        await RecordRoomScheduleExploration(host);
        await host.CallOkAsync("ui_click", new { pid = 1, selector = "id=OTHER" }, session: "s2");    // other session
        var stop = await host.CallOkAsync("recorder_stop");

        var tools = host.Recorder.Get("s1")!.Calls.Select(c => c.Tool).ToArray();
        Assert.Equal(new[] { "ui_click", "ui_click", "ui_wait", "ui_select", "ui_click", "ui_expect", "schedule_read", "model_query_elements" }, tools);
        Assert.Equal(8, stop.GetProperty("calls").GetInt32());
        Assert.Null(host.Recorder.Get("s2"));

        // after stop nothing more is recorded
        await host.CallOkAsync("ui_click", new { pid = 1, selector = "id=LATE" });
        Assert.Equal(8, host.Recorder.Get("s1")!.Calls.Count);
    }

    [Theory]
    [InlineData("ui_click", ToolPermission.Interact, true)]
    [InlineData("doc_open", ToolPermission.Write, true)]
    [InlineData("revit_launch", ToolPermission.Admin, true)]
    [InlineData("ui_snapshot", ToolPermission.Read, false)]
    [InlineData("doc_info", ToolPermission.Read, true)]
    [InlineData("schedule_read", ToolPermission.Read, true)]
    [InlineData("recorder_export", ToolPermission.Write, false)]
    [InlineData("scenario_run", ToolPermission.Write, false)]
    [InlineData("gateway_set_policy", ToolPermission.Admin, false)]
    [InlineData("call_tool", ToolPermission.Write, false)]
    public void ShouldRecord(string tool, ToolPermission permission, bool expected) =>
        Assert.Equal(expected, ScenarioRecorder.ShouldRecord(tool, permission));

    [Fact]
    public async Task Start_Twice_IsConflict_UnlessRestart()
    {
        await using var host = new ScenarioHost();
        await host.CallOkAsync("recorder_start");
        Assert.Equal("CONFLICT", (await host.CallAsync("recorder_start")).ErrorCode);
        await host.CallOkAsync("recorder_start", new { restart = true });
        Assert.Equal("NOT_FOUND", (await host.CallAsync("recorder_stop", session: "nobody")).ErrorCode);
    }

    [Fact]
    public async Task Export_WritesScenario_UiScript_AndChecksSkeleton()
    {
        await using var host = new ScenarioHost();
        await host.CallOkAsync("recorder_start", new { name = "Create a Room schedule through the UI" });
        await host.CallOkAsync("revit_launch", new { year = 2024 });
        var run = await host.CallOkAsync("sandbox_create_run", new { name = "explore" });
        var original = Path.Combine(TestGateway.NewTempDir("revitagent_src_"), "model.rvt");
        File.WriteAllText(original, "m");
        var copy = await host.CallOkAsync("sandbox_copy_model", new { source_path = original, run_id = run.GetProperty("run_id").GetString() });
        var copyPath = copy.GetProperty("copy_path").GetString()!;
        await host.CallOkAsync("doc_open", new { pid = FakeRevit.LaunchedPid, path = copyPath });
        await RecordRoomScheduleExploration(host);
        await host.CallOkAsync("echo", new { pid = FakeRevit.LaunchedPid, model = copyPath, @out = Path.Combine(run.GetProperty("path").GetString()!, "out.txt") });
        await host.CallOkAsync("recorder_stop");

        var r = await host.CallOkAsync("recorder_export", new { name = "room-schedule-ui" });
        var dir = r.GetProperty("scenario_dir").GetString()!;
        Assert.Equal(Path.Combine(host.Paths.Root, "scenarios", "room-schedule-ui"), dir);
        Assert.Equal(1, r.GetProperty("ui_steps").GetInt32());
        Assert.Equal(3, r.GetProperty("tool_steps").GetInt32());
        Assert.Contains(r.GetProperty("todos").EnumerateArray(), t => t.GetString()!.Contains("e99"));
        Assert.Contains(r.GetProperty("not_exported").EnumerateArray(), t => t.GetString()!.Contains("doc_open"));

        // scenario.yaml loads back with the expected steps
        var load = ScenarioLoader.Load(dir);
        Assert.False(load.HasErrors, string.Join("; ", load.Issues));
        var s = load.Scenario!;
        Assert.Equal("room-schedule-ui", s.Name);
        Assert.Equal(2024, s.RevitVersion);
        Assert.Equal(original, s.Model);
        Assert.Equal(new[] { "01-ui", "02-schedule_read", "03-model_query_elements", "04-echo" }, s.Steps.Select(x => x.Code));
        Assert.Equal(@"ui\01-ui.json", s.Steps[0].Ui);
        Assert.Equal("Room Schedule", s.Steps[1].Args["name"]!.GetValue<string>());
        Assert.False(s.Steps[1].Args.ContainsKey("pid"));
        Assert.Equal("{model}", s.Steps[3].Args["model"]!.GetValue<string>());
        Assert.Equal(@"{run_dir}\out.txt", s.Steps[3].Args["out"]!.GetValue<string>());

        // UI script: selectors preferred, ref resolved from the result, unresolved ref marked TODO
        using var ui = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "ui", "01-ui.json")));
        var steps = ui.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.False(ui.RootElement.TryGetProperty("window", out _));
        Assert.Equal(new[] { "click", "click", "wait", "select", "click", "expect" }, steps.Select(x => x.GetProperty("do").GetString()));
        Assert.Equal("id=ID_VIEW_TAB", steps[0].GetProperty("target").GetString());
        Assert.Equal("id=ID_SCHEDULES", steps[1].GetProperty("target").GetString());
        Assert.Equal("New Schedule", steps[2].GetProperty("window").GetString());
        Assert.Equal(30, steps[2].GetProperty("timeout").GetInt32());
        Assert.Equal("Rooms", steps[3].GetProperty("text").GetString());
        Assert.Equal("TODO-ref:e99", steps[4].GetProperty("target").GetString());
        Assert.StartsWith("TODO", steps[4].GetProperty("name").GetString());
        Assert.Equal("gone", steps[5].GetProperty("is").GetString());
        Assert.Single(ui.RootElement.GetProperty("todo").EnumerateArray());
        Assert.DoesNotContain(UiScriptLint.Lint(Path.Combine(dir, "ui", "01-ui.json"), "ui"), i => i.Severity == IssueSeverity.Error);

        // checks skeleton: one check per verification read, expectations = recorded values
        var checks = s.Checks;
        Assert.Equal(new[] { "C01", "C02", "C03" }, checks.Select(x => x.Id));
        Assert.Equal("step_ok(01-ui)", checks[0].Expect);
        Assert.Equal("value(02-schedule_read.json -> body.data_rows) == 2", checks[1].Expect); // data rows, not the headings or fields
        Assert.Contains("count(03-model_query_elements.json -> elements[name=\"Roof\"]) == 1", checks[2].Expect);
        Assert.All(checks, x => Assert.Null(CheckExpression.SyntaxProblem(x.Expect)));
        Assert.All(checks, x => Assert.Contains("Confirm the expected value", x.Note));
    }

    [Fact]
    public async Task RecordExportReplay_WithoutAi_Passes()
    {
        await using var host = new ScenarioHost();
        await host.CallOkAsync("recorder_start");
        var pid = FakeRevit.ExistingPid;
        await host.CallOkAsync("ui_click", new { pid, selector = "id=ID_VIEW_TAB" });
        await host.CallOkAsync("ui_type", new { pid, window = "Schedule Properties", selector = "id=txtName", text = "Room Schedule" });
        await host.CallOkAsync("ui_keys", new { pid, keys = "{ENTER}" });
        await host.CallOkAsync("schedule_read", new { pid, name = "Room Schedule" });
        await host.CallOkAsync("model_query_elements", new { pid, category = "Levels" });
        await host.CallOkAsync("recorder_stop");
        var export = await host.CallOkAsync("recorder_export", new { name = "replay-me" });
        var dir = export.GetProperty("scenario_dir").GetString()!;
        Assert.Empty(export.GetProperty("todos").EnumerateArray());

        var validation = await host.CallOkAsync("scenario_validate", new { scenario_dir = dir });
        Assert.True(validation.GetProperty("ok").GetBoolean(), validation.GetRawText());

        var replay = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, pid });
        Assert.Equal("PASS", replay.GetProperty("verdict").GetString());
        Assert.Equal(3, replay.GetProperty("checks").GetProperty("pass").GetInt32());

        // the replay's own calls ran under the run's session, not the caller's (nothing new recorded)
        Assert.Equal(5, host.Recorder.Get("s1")!.Calls.Count);
    }

    [Fact]
    public async Task Export_Guards_NameFolderAndEmptyRecording()
    {
        await using var host = new ScenarioHost();
        await host.CallOkAsync("recorder_start");
        Assert.Equal("VALIDATION_FAILED", (await host.CallAsync("recorder_export", new { name = "empty" })).ErrorCode);
        await host.CallOkAsync("ui_click", new { pid = 1, selector = "id=X" });
        Assert.Equal("VALIDATION_FAILED", (await host.CallAsync("recorder_export", new { name = "Bad Name!" })).ErrorCode);
        Assert.Equal("PERMISSION_DENIED", (await host.CallAsync("recorder_export", new { name = "x", out_dir = Path.Combine(Path.GetTempPath(), "elsewhere") })).ErrorCode);
        await host.CallOkAsync("recorder_export", new { name = "once" });
        Assert.Equal("CONFLICT", (await host.CallAsync("recorder_export", new { name = "once" })).ErrorCode);
        await host.CallOkAsync("recorder_export", new { name = "once", overwrite = true });
        // overwrite never deletes files in a foreign folder under the gateway home
        var foreign = Path.Combine(host.Paths.Root, "my", "precious");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "scenario.yaml"), "keep me");
        var denied = await host.CallAsync("recorder_export", new { name = "x", out_dir = foreign, overwrite = true });
        Assert.Equal("PERMISSION_DENIED", denied.ErrorCode);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(foreign, "scenario.yaml")));
        var custom = Path.Combine(host.Paths.Root, "my", "scn");
        var r = await host.CallOkAsync("recorder_export", new { name = "scn", out_dir = custom });
        Assert.True(File.Exists(Path.Combine(custom, "scenario.yaml")));
        Assert.True(r.GetProperty("recording_active").GetBoolean());
        // a folder the recorder created (marker) can be overwritten again
        await host.CallOkAsync("recorder_export", new { name = "scn", out_dir = custom, overwrite = true });
    }
}
