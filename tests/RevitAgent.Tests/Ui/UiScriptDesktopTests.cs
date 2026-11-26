using System.IO;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;

namespace RevitAgent.Tests.Ui;

/// <summary>ui_run_script end-to-end against the test app (UI script format, reports, dialog rules, pid boundary).</summary>
[Collection(UiDesktopCollection.Name)]
public class UiScriptDesktopTests
{
    [DesktopFact]
    public async Task RunScript_FormDialogsAndChildWindow_PassesAndWritesReports()
    {
        using var h = UiHarness.Start();
        var dir = TestGateway.NewTempDir("ui_script_");
        File.WriteAllText(Path.Combine(dir, "rules.json"), """[ { "name": "Test Message -> Yes", "title": "Test Message", "click": "name=Yes" } ]""");
        var scriptPath = Path.Combine(dir, "smoke.json");
        File.WriteAllText(scriptPath, $$"""
        {
          "name": "Test app smoke",
          "timeout": 10,
          "screenshots": "fail",
          "dialogs": "rules.json",
          "vars": { "customer": "Nguyễn Văn An" },
          "steps": [
            { "name": "Submit starts disabled", "action": "expect", "selector": "id=btnSubmit", "is": "disabled" },
            { "action": "type", "selector": "id=txtName", "text": "${customer}" },
            { "action": "select", "selector": "id=cmbCity", "item": "Huế" },
            { "action": "click", "selector": "id=chkAgree" },
            { "action": "expect", "selector": "id=chkAgree", "is": "checked" },
            { "action": "click", "selector": "id=btnSubmit" },
            { "action": "expect", "selector": "id=lblStatus", "text": "Saved: ${customer} / Huế", "exact": true },
            { "action": "read_table", "selector": "id=gridOrders", "save_path": "${env:REVITAGENT_UI_OUT}\\orders.csv" },
            { "action": "expect", "selector": "id=gridOrders", "text": "Phú Quốc" },
            { "name": "MessageBox auto-confirmed by rule", "action": "click", "selector": "id=btnMessage" },
            { "action": "expect", "selector": "id=lblStatus", "text": "Message: Yes", "exact": true },
            { "action": "click", "selector": "id=btnOpenChild" },
            { "action": "wait", "window": "Child Settings", "timeout_s": 10 },
            { "action": "type", "selector": "id=txtChild", "text": "abc", "keyboard": true, "verify": true },
            { "action": "screenshot", "save_path": "${env:REVITAGENT_UI_OUT}\\child.png" },
            { "action": "click", "selector": "id=btnChildOk" },
            { "action": "expect", "window": "Child Settings", "is": "gone" },
            { "action": "expect", "selector": "id=lblStatus", "text": "Child: abc" },
            { "action": "snapshot", "interactive_only": true },
            { "action": "keys", "keys": "{ESC}", "window": "pid:${env:REVITAGENT_UI_HOST_PID}" }
          ]
        }
        """);

        var r = await h.CallAsync("ui_run_script", new { pid = h.Pid, script_path = scriptPath, out_dir = "smoke" });
        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        var failed = steps.Where(s => s.GetProperty("status").GetString() != "passed").Select(s => s.ToString()).ToArray();
        Assert.True(r.GetProperty("verdict").GetString() == "passed", string.Join("\n", failed));
        Assert.Equal("passed", r.GetProperty("status").GetString());
        Assert.Equal(0, r.GetProperty("failed").GetInt32());
        Assert.Equal(20, r.GetProperty("passed").GetInt32());
        Assert.True(File.Exists(r.GetProperty("report_path").GetString()));
        // The rule fires inside the auto-wait of the step that follows the click.
        Assert.Contains(steps.Skip(9).Take(2), s => s.TryGetProperty("observed", out var o) && o.GetString()!.Contains("auto-handled dialog 'Test Message -> Yes'", StringComparison.Ordinal));
        Assert.Equal("passed", steps[13].GetProperty("status").GetString());

        var outDir = r.GetProperty("out_dir").GetString()!;
        Assert.StartsWith(Path.GetFullPath(h.Paths.RunsDir), outDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(outDir, "orders.csv")));
        Assert.Contains("Phú Quốc", File.ReadAllText(Path.Combine(outDir, "orders.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "child.png")));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "report.json")));
        Assert.Equal("passed", report.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(20, report.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Contains("| 1 | passed | Submit starts disabled |", File.ReadAllText(Path.Combine(outDir, "report.md")));
    }

    [DesktopFact]
    public async Task RunScript_FailingStep_CapturesEvidence_SkipsRest_AndHonoursOptional()
    {
        using var h = UiHarness.Start();
        var script = new
        {
            name = "failing",
            steps = new object[]
            {
                new { action = "expect", selector = "id=lblStatus", text = "never shown", timeout_s = 1 },
                new { action = "click", selector = "id=btnCount" },
            },
        };
        var r = await h.CallAsync("ui_run_script", new { pid = h.Pid, script });
        Assert.Equal("failed", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("failed").GetInt32());
        Assert.Equal(0, r.GetProperty("passed").GetInt32());
        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("failed", steps[0].GetProperty("status").GetString());
        Assert.Equal("text contains 'never shown'", steps[0].GetProperty("expected").GetString());
        Assert.False(string.IsNullOrEmpty(steps[0].GetProperty("observed").GetString()));
        Assert.Equal("skipped", steps[1].GetProperty("status").GetString());
        var outDir = r.GetProperty("out_dir").GetString()!;
        Assert.True(File.Exists(Path.Combine(outDir, "01_failed.png")));
        Assert.Contains("id=lblStatus", File.ReadAllText(Path.Combine(outDir, "01_failed_snapshot.txt")));
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 0", exact = true }); // skipped step did not run

        // An optional step that fails is recorded but does not fail the run, and the run goes on.
        var optional = await h.CallAsync("ui_run_script", new
        {
            pid = h.Pid,
            screenshots = "none",
            script = new
            {
                steps = new object[]
                {
                    new { action = "click", selector = "id=ID_NOT_A_RIBBON_BUTTON", timeout_s = 1, optional = true },
                    new { action = "click", selector = "id=btnCount" },
                },
            },
        });
        Assert.Equal("passed", optional.GetProperty("status").GetString());
        Assert.Equal(0, optional.GetProperty("failed").GetInt32());
        Assert.Equal(1, optional.GetProperty("optional_failed").GetInt32());
        Assert.Equal("failed", optional.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal("passed", optional.GetProperty("steps")[1].GetProperty("status").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 1", exact = true });

        // The same step without optional fails the run.
        var required = await h.CallAsync("ui_run_script", new
        {
            pid = h.Pid,
            screenshots = "none",
            script = new { steps = new object[] { new { action = "click", selector = "id=ID_NOT_A_RIBBON_BUTTON", timeout_s = 1 } } },
        });
        Assert.Equal("failed", required.GetProperty("status").GetString());
        Assert.Equal(1, required.GetProperty("failed").GetInt32());
        Assert.Equal(1, required.GetProperty("failed_steps").GetArrayLength());
    }

    [DesktopFact]
    public async Task RunScript_StaysInsideTheTargetProcess()
    {
        using var h = UiHarness.Start();
        var r = await h.CallAsync("ui_run_script", new
        {
            pid = h.Pid,
            screenshots = "none",
            script = new
            {
                steps = new object[]
                {
                    new { action = "click", window = "pid:4", selector = "id=anything", timeout_s = 1, optional = true },
                    new { action = "attach", window = "Program Manager", optional = true },
                    new { action = "screenshot", save_path = Path.Combine(Path.GetTempPath(), "outside.png"), optional = true },
                    new { action = "expect", selector = "id=lblStatus", text = "Ready", exact = true },
                },
            },
        });
        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        string[] refused = { "failed", "blocked" };
        Assert.Contains(steps[0].GetProperty("status").GetString(), refused);
        Assert.Contains("different process", steps[0].GetProperty("error").GetString());
        Assert.Contains(steps[1].GetProperty("status").GetString(), refused); // the shell's window is not part of pid
        Assert.Contains(steps[2].GetProperty("status").GetString(), refused);
        Assert.Contains("outside the gateway evidence folders", steps[2].GetProperty("error").GetString());
        Assert.Equal("passed", steps[3].GetProperty("status").GetString());

        var (code, _) = await h.CallErrorAsync("ui_run_script", new { pid = h.Pid, out_dir = Path.GetTempPath(), script = new { steps = new object[] { new { action = "pause" } } } });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, code);
    }
}
