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
            { "name": "Submit starts disabled", "do": "expect", "target": "id=btnSubmit", "is": "disabled" },
            { "do": "type", "target": "id=txtName", "text": "${customer}" },
            { "do": "select", "target": "id=cmbCity", "text": "Huế" },
            { "do": "click", "target": "id=chkAgree" },
            { "do": "expect", "target": "id=chkAgree", "is": "checked" },
            { "do": "click", "target": "id=btnSubmit" },
            { "do": "expect", "target": "id=lblStatus", "text": "Saved: ${customer} / Huế", "exact": true },
            { "do": "table", "target": "id=gridOrders", "out": "${env:REVITAGENT_UI_OUT}\\orders.csv" },
            { "do": "expect", "target": "id=gridOrders", "text": "Phú Quốc" },
            { "name": "MessageBox auto-confirmed by rule", "do": "click", "target": "id=btnMessage" },
            { "do": "expect", "target": "id=lblStatus", "text": "Message: Yes", "exact": true },
            { "do": "click", "target": "id=btnOpenChild" },
            { "do": "wait", "window": "Child Settings", "timeout": 10 },
            { "do": "type", "target": "id=txtChild", "text": "abc", "keyboard": true, "verify": true },
            { "do": "screenshot", "out": "${env:REVITAGENT_UI_OUT}\\child.png" },
            { "do": "click", "target": "id=btnChildOk" },
            { "do": "expect", "window": "Child Settings", "is": "gone" },
            { "do": "expect", "target": "id=lblStatus", "text": "Child: abc" },
            { "do": "snapshot", "interactive": true },
            { "do": "keys", "target": "{ESC}", "window": "pid:${env:REVITAGENT_UI_HOST_PID}" }
          ]
        }
        """);

        var r = await h.CallAsync("ui_run_script", new { pid = h.Pid, script_path = scriptPath, out_dir = "smoke" });
        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        var failed = steps.Where(s => s.GetProperty("status").GetString() != "PASS").Select(s => s.ToString()).ToArray();
        Assert.True(r.GetProperty("verdict").GetString() == "PASSED", string.Join("\n", failed));
        Assert.Equal("SUCCEEDED", r.GetProperty("measured").GetString());
        Assert.Equal("PASSED", r.GetProperty("status").GetString());
        Assert.Equal(0, r.GetProperty("failed").GetInt32());
        Assert.Equal(20, r.GetProperty("passed").GetInt32());
        Assert.True(File.Exists(r.GetProperty("report_path").GetString()));
        // The rule fires inside the auto-wait of the step that follows the click.
        Assert.Contains(steps.Skip(9).Take(2), s => s.TryGetProperty("output", out var o) && o.GetString()!.Contains("auto-handled dialog 'Test Message -> Yes'", StringComparison.Ordinal));
        Assert.Equal("check", steps[13].GetProperty("role").GetString()); // after 'wait window', steps act on the add-in window
        Assert.Equal("nav", steps[0].GetProperty("role").GetString());

        var outDir = r.GetProperty("out_dir").GetString()!;
        Assert.StartsWith(Path.GetFullPath(h.Paths.RunsDir), outDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(outDir, "orders.csv")));
        Assert.Contains("Phú Quốc", File.ReadAllText(Path.Combine(outDir, "orders.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "child.png")));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "report.json")));
        Assert.Equal("PASSED", report.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(20, report.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Contains("| 1 | PASS | Submit starts disabled |", File.ReadAllText(Path.Combine(outDir, "report.md")));
    }

    [DesktopFact]
    public async Task RunScript_FailingStep_CapturesEvidence_SkipsRest_AndMeasuresByRole()
    {
        using var h = UiHarness.Start();
        var script = new
        {
            name = "failing",
            steps = new object[]
            {
                new { @do = "expect", target = "id=lblStatus", text = "never shown", timeout = 1, role = "check" },
                new { @do = "click", target = "id=btnCount" },
            },
        };
        var r = await h.CallAsync("ui_run_script", new { pid = h.Pid, script });
        Assert.Equal("FAILED", r.GetProperty("verdict").GetString());
        Assert.Equal("FAILED", r.GetProperty("measured").GetString()); // a 'check' step failed = product failure
        Assert.Equal("FAILED", r.GetProperty("status").GetString());
        Assert.Equal(1, r.GetProperty("failed").GetInt32());
        Assert.Equal(0, r.GetProperty("passed").GetInt32());
        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("FAIL", steps[0].GetProperty("status").GetString());
        Assert.StartsWith("FAIL:", steps[0].GetProperty("error").GetString());
        Assert.Equal("SKIP", steps[1].GetProperty("status").GetString());
        var outDir = r.GetProperty("out_dir").GetString()!;
        Assert.True(File.Exists(Path.Combine(outDir, "01_fail.png")));
        Assert.Contains("id=lblStatus", File.ReadAllText(Path.Combine(outDir, "01_fail_snapshot.txt")));
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 0", exact = true }); // skipped step did not run

        // Navigation failure on the main window = BLOCKED (not measurable), optional -> WARN, continueOnError keeps going.
        var nav = await h.CallAsync("ui_run_script", new
        {
            pid = h.Pid,
            screenshots = "none",
            script = new
            {
                steps = new object[]
                {
                    new { @do = "click", target = "id=ID_NOT_A_RIBBON_BUTTON", timeout = 1, continueOnError = true },
                    new { @do = "click", target = "id=btnCount" },
                },
            },
        });
        Assert.Equal("FAILED", nav.GetProperty("verdict").GetString());
        Assert.Equal("SUCCEEDED", nav.GetProperty("measured").GetString());
        Assert.Equal("PASSED", nav.GetProperty("status").GetString());
        Assert.Equal(0, nav.GetProperty("failed").GetInt32());
        Assert.Equal(1, nav.GetProperty("warned").GetInt32());
        Assert.Equal("WARN", nav.GetProperty("steps")[0].GetProperty("outcome").GetString());
        Assert.Equal("PASS", nav.GetProperty("steps")[1].GetProperty("status").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 1", exact = true });

        // The same navigation failure without continueOnError: BLOCKED, not FAILED.
        var blocked = await h.CallAsync("ui_run_script", new
        {
            pid = h.Pid,
            screenshots = "none",
            script = new { steps = new object[] { new { @do = "click", target = "id=ID_NOT_A_RIBBON_BUTTON", timeout = 1 } } },
        });
        Assert.Equal("BLOCKED", blocked.GetProperty("status").GetString());
        Assert.Equal(0, blocked.GetProperty("failed").GetInt32());
        Assert.Equal(1, blocked.GetProperty("blocked").GetInt32());
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
                continueOnError = true,
                steps = new object[]
                {
                    new { @do = "click", window = "pid:4", target = "id=anything", timeout = 1 },
                    new { @do = "use", window = "Program Manager" },
                    new { @do = "screenshot", @out = Path.Combine(Path.GetTempPath(), "outside.png") },
                    new { @do = "expect", target = "id=lblStatus", text = "Ready", exact = true },
                },
            },
        });
        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("FAIL", steps[0].GetProperty("status").GetString());
        Assert.Contains("different process", steps[0].GetProperty("error").GetString());
        Assert.Equal("FAIL", steps[1].GetProperty("status").GetString()); // the shell's window is not part of pid
        Assert.Equal("FAIL", steps[2].GetProperty("status").GetString());
        Assert.Contains("outside the gateway evidence folders", steps[2].GetProperty("error").GetString());
        Assert.Equal("PASS", steps[3].GetProperty("status").GetString());

        var (code, _) = await h.CallErrorAsync("ui_run_script", new { pid = h.Pid, out_dir = Path.GetTempPath(), script = new { steps = new object[] { new { @do = "sleep" } } } });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, code);
    }
}
