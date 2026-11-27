using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Ui;

/// <summary>
/// Drives tests/RevitAgent.UiTestApp out-of-process through the ui_* tools (ToolRegistry), like an agent would.
/// Every test starts its own app instance, targets only that pid, and kills it at the end.
/// </summary>
[Collection(UiDesktopCollection.Name)]
public class UiToolsDesktopTests
{
    [DesktopFact]
    public async Task ListWindows_ReportsTheMainWindowOfThePid()
    {
        using var h = UiHarness.Start();
        var r = await h.CallAsync("ui_list_windows", new { pid = h.Pid });
        var windows = r.GetProperty("windows").EnumerateArray().ToArray();
        var main = Assert.Single(windows, w => w.GetProperty("is_main").GetBoolean());
        Assert.Equal(UiTestApp.MainTitle, main.GetProperty("title").GetString());
        Assert.Equal(h.Pid, main.GetProperty("pid").GetInt32());
        Assert.StartsWith("0x", main.GetProperty("hwnd").GetString());
        Assert.False(main.GetProperty("is_modal_dialog").GetBoolean());
        Assert.True(r.GetProperty("desktop").GetProperty("available").GetBoolean());
    }

    [DesktopFact]
    public async Task ForbiddenDialogTitle_RefusesEveryStateChangingAction_ButReadsStayAllowed()
    {
        const string title = "Security - Unsigned Add-In";
        using var h = UiHarness.Start(title);
        foreach (var (tool, args) in new (string, object)[]
        {
            ("ui_click", new { pid = h.Pid, window = title, selector = "id=btnCount" }),
            ("ui_click", new { pid = h.Pid, window = title, x = 10, y = 10 }),
            ("ui_type", new { pid = h.Pid, window = title, selector = "id=txtName", text = "x" }),
            ("ui_type", new { pid = h.Pid, window = title, text = "x" }),
            ("ui_keys", new { pid = h.Pid, window = title, keys = "{TAB}" }),
            ("ui_toggle", new { pid = h.Pid, window = title, selector = "id=chkAgree" }),
        })
        {
            var r = await h.CallRawAsync(tool, args);
            Assert.True(r.IsError, tool);
            Assert.Equal("PERMISSION_DENIED", r.ErrorCode);
            Assert.Contains("human", UiHarness.Text(r), StringComparison.OrdinalIgnoreCase);
        }

        // read-only tools still work on such a window
        Assert.False((await h.CallRawAsync("ui_snapshot", new { pid = h.Pid })).IsError);
        Assert.False((await h.CallRawAsync("ui_list_windows", new { pid = h.Pid })).IsError);
    }

    [DesktopFact]
    public async Task ProcessNotAllowedForInput_IsRefused_EvenThroughARefWithoutPid_ButReadsStayAllowed()
    {
        using var h = UiHarness.Start();
        var snap = await h.SnapshotTextAsync(); // the app becomes the session's current window
        var countRef = UiHarness.RefOf(snap, "id=btnCount");
        h.Services.GetRequiredService<RevitAgent.Core.Policy.GatewayPolicy>().SetAllowedInputProcesses(Array.Empty<string>()); // owner default: Revit only

        foreach (var (tool, args) in new (string, object)[]
        {
            ("ui_click", new { @ref = countRef }), // no pid: resolved from the session's current window
            ("ui_click", new { pid = h.Pid, selector = "id=btnCount" }),
            ("ui_keys", new { keys = "{TAB}" }),
            ("ui_type", new { pid = h.Pid, selector = "id=txtName", text = "x" }),
            ("ui_run_script", new { pid = h.Pid, script = new { steps = new object[] { new { action = "keys", keys = "{TAB}" } } } }),
        })
        {
            var r = await h.CallRawAsync(tool, args);
            Assert.True(r.IsError, $"{tool}: {UiHarness.Text(r)}");
            Assert.Equal("PERMISSION_DENIED", r.ErrorCode);
        }

        Assert.False((await h.CallRawAsync("ui_snapshot", new { pid = h.Pid })).IsError);
        Assert.False((await h.CallRawAsync("ui_find", new { pid = h.Pid, selector = "id=btnCount" })).IsError);
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 0", exact = true }); // nothing was clicked
    }

    [DesktopFact]
    public async Task Script_ClickInForbiddenWindow_IsRejected()
    {
        const string title = "Security - Unsigned Add-In";
        using var h = UiHarness.Start(title);
        var r = await h.CallRawAsync("ui_run_script", new
        {
            pid = h.Pid,
            script = new { steps = new object[] { new { action = "click", window = title, selector = "id=btnCount" } } },
        });
        Assert.True(r.IsError, UiHarness.Text(r));
        Assert.Equal("PERMISSION_DENIED", r.ErrorCode);
    }

    [DesktopFact]
    public async Task Snapshot_ListsKnownControlsWithRefsAndStates()
    {
        using var h = UiHarness.Start();
        var raw = await h.CallRawAsync("ui_snapshot", new { pid = h.Pid });
        Assert.False(raw.IsError, UiHarness.Text(raw));
        var header = raw.Structured!.Value;
        Assert.True(header.GetProperty("refs").GetInt32() > 10);
        Assert.Equal(UiTestApp.MainTitle, header.GetProperty("window").GetProperty("title").GetString());
        var text = ((TextContent)raw.Content[1]).Text;
        Assert.Contains("- Window \"RevitAgent UI Test App\" [ref=e1]", text);
        Assert.Contains("id=btnSubmit", text);
        Assert.Matches("Button \"Never enabled\" \\[ref=e\\d+\\] id=btnDisabled disabled", text);
        Assert.Contains("id=txtName", text);
        Assert.Matches("Edit \\[ref=e\\d+\\] id=txtPassword password", text);
        Assert.Contains("id=chkAgree unchecked", text);
        Assert.Contains("id=cmbCity", text);
        Assert.Contains("TabItem \"General\"", text);
        Assert.DoesNotContain("Late button", text); // collapsed => not in the tree
    }

    [DesktopFact]
    public async Task Snapshot_LabelsATypeNamedButtonFromItsTextBlock_AndReportsRoundTrips()
    {
        using var h = UiHarness.Start();
        var raw = await h.CallRawAsync("ui_snapshot", new { pid = h.Pid });
        Assert.False(raw.IsError, UiHarness.Text(raw));
        var text = ((TextContent)raw.Content[1]).Text;
        Assert.Matches("Button[^\\n]*label=\"Ribbon Label\"[^\\n]*id=btnRibbonLike", text);

        var header = raw.Structured!.Value;
        var perf = header.GetProperty("perf");
        Assert.True(perf.GetProperty("label_lookups").GetInt32() >= 1);
        Assert.Equal(0, perf.GetProperty("labels_skipped").GetInt32());
        Assert.True(perf.GetProperty("round_trips").GetInt32() > header.GetProperty("refs").GetInt32());

        // ui_find labels it the same way.
        var found = await h.CallAsync("ui_find", new { pid = h.Pid, selector = "id=btnRibbonLike" });
        Assert.Contains("label=\"Ribbon Label\"", Assert.Single(found.GetProperty("matches").EnumerateArray()).GetProperty("line").GetString());
    }

    [DesktopFact]
    public async Task Click_ByRef_InvokesButton_AndReportsResolvedSelector()
    {
        using var h = UiHarness.Start();
        var snap = await h.SnapshotTextAsync();
        var r = await h.CallAsync("ui_click", new { pid = h.Pid, @ref = UiHarness.RefOf(snap, "id=btnCount") });
        Assert.Equal("invoked", r.GetProperty("action").GetString());
        var target = r.GetProperty("target");
        Assert.Equal("id=btnCount", target.GetProperty("selector").GetString());
        Assert.Equal(UiTestApp.MainTitle, target.GetProperty("window").GetString());
        Assert.True(target.GetProperty("is_main_window").GetBoolean());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 1", exact = true });
    }

    [DesktopFact]
    public async Task Type_Toggle_Select_Submit_FormFlow()
    {
        using var h = UiHarness.Start();
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=btnSubmit", @is = "disabled" });

        var typed = await h.CallAsync("ui_type", new { pid = h.Pid, selector = "id=txtName", text = "Nguyễn Văn An", verify = true });
        Assert.Equal("set_value", typed.GetProperty("action").GetString());
        Assert.Equal("Nguyễn Văn An", typed.GetProperty("value").GetString());

        var sel = await h.CallAsync("ui_select", new { pid = h.Pid, selector = "id=cmbCity", item = "Phú Quốc" });
        Assert.Equal("Phú Quốc", sel.GetProperty("value").GetString());

        var tg = await h.CallAsync("ui_toggle", new { pid = h.Pid, selector = "id=chkAgree", state = "on" });
        Assert.Equal("on", tg.GetProperty("state").GetString());
        var again = await h.CallAsync("ui_toggle", new { pid = h.Pid, selector = "id=chkAgree", state = "on" });
        Assert.Equal("on", again.GetProperty("state").GetString()); // idempotent

        await h.ExpectAsync(new { pid = h.Pid, selector = "id=chkAgree", @is = "checked" });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=btnSubmit", @is = "enabled" });
        await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnSubmit" });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblStatus", text = "Saved: Nguyễn Văn An / Phú Quốc", exact = true });
    }

    [DesktopFact]
    public async Task Type_Keyboard_Verify_And_Keys()
    {
        using var h = UiHarness.Start();
        var r = await h.CallAsync("ui_type", new { pid = h.Pid, selector = "id=txtKeys", text = "héllo 123", keyboard = true, verify = true });
        Assert.Equal("keyboard", r.GetProperty("action").GetString());
        Assert.Equal("héllo 123", r.GetProperty("value").GetString());

        await h.CallAsync("ui_keys", new { pid = h.Pid, selector = "id=txtKeys", keys = "{END}{BACKSPACE}{BACKSPACE}4" });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=txtKeys", text = "héllo 14", exact = true });

        var cleared = await h.CallAsync("ui_type", new { pid = h.Pid, selector = "id=txtKeys", clear = true, keyboard = true });
        Assert.Equal("cleared", cleared.GetProperty("action").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=txtKeys", text = string.Empty, exact = true });
    }

    [DesktopFact]
    public async Task Select_ListItemByIndex_AndTab_AndExpandTree()
    {
        using var h = UiHarness.Start();
        var r = await h.CallAsync("ui_select", new { pid = h.Pid, selector = "id=lstItems", index = 2 });
        Assert.Contains("Gamma", r.GetProperty("item").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "type=ListItem;name=Gamma", @is = "selected" });

        await h.CallAsync("ui_select", new { pid = h.Pid, selector = "type=TabItem;name=Advanced" });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=txtAdvanced", text = "advanced value" });

        var ex = await h.CallAsync("ui_expand", new { pid = h.Pid, selector = "type=TreeItem;name=Root" });
        Assert.Equal("expanded", ex.GetProperty("state").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "type=TreeItem;name=Child B", @is = "exists" });
        await h.ExpectAsync(new { pid = h.Pid, selector = "type=TreeItem", count = 3 });
    }

    [DesktopFact]
    public async Task ReadTable_ReturnsHeadersAndRows()
    {
        using var h = UiHarness.Start();
        var r = await h.CallAsync("ui_read_table", new { pid = h.Pid, selector = "id=gridOrders" });
        var headers = r.GetProperty("headers").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "Id", "Customer", "City", "Amount" }, headers);
        var rows = r.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(c => c.GetString()).ToArray()).ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal(new[] { "1", "Alice", "Hà Nội", "120.5" }, rows[0]);
        Assert.Equal("Phú Quốc", rows[2][2]);

        var limited = await h.CallAsync("ui_read_table", new { pid = h.Pid, selector = "id=gridOrders", limit = 2 });
        Assert.Equal(2, limited.GetProperty("rows").GetArrayLength());
        Assert.True(limited.GetProperty("more").GetBoolean());
    }

    [DesktopFact]
    public async Task ModalChildWindow_IsSeparateTopLevelWindow_DrivenByWindowTitle()
    {
        using var h = UiHarness.Start();
        var click = await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnOpenChild" });
        Assert.Equal("invoked", click.GetProperty("action").GetString());
        await h.CallAsync("ui_wait", new { pid = h.Pid, window = "Child Settings", timeout_s = 10 });

        var list = await h.CallAsync("ui_list_windows", new { pid = h.Pid });
        var child = list.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("title").GetString() == "Child Settings");
        Assert.True(child.GetProperty("is_modal_dialog").GetBoolean()); // owner is disabled while ShowDialog runs
        Assert.False(child.GetProperty("is_main").GetBoolean());

        var snapRaw = await h.CallRawAsync("ui_snapshot", new { pid = h.Pid });
        Assert.Contains("Child Settings", UiHarness.Text(snapRaw)); // "also_open" in the main window header

        var typed = await h.CallAsync("ui_type", new { pid = h.Pid, window = "Child Settings", selector = "id=txtChild", text = "from agent" });
        Assert.Equal("Child Settings", typed.GetProperty("target").GetProperty("window").GetString());
        Assert.False(typed.GetProperty("target").GetProperty("is_main_window").GetBoolean());
        await h.CallAsync("ui_click", new { pid = h.Pid, window = "Child Settings", selector = "id=btnChildOk" });
        await h.CallAsync("ui_wait", new { pid = h.Pid, window = "Child Settings", state = "gone", timeout_s = 10 });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblStatus", text = "Child: from agent", exact = true });
    }

    /// <summary>
    /// Input aimed at the main window (ui_keys / ui_type without window, ui_click x/y) passed the safety check of the main
    /// window only, so it must never land in a modal window of the same process that activation brings to the front
    /// (in Revit: Enter on the "Security - Unsigned Add-In" prompt; review finding 2026-11-27).
    /// </summary>
    [DesktopFact]
    public async Task UntargetedInput_ForTheMainWindow_NeverLandsInItsModalChild()
    {
        using var h = UiHarness.Start();
        await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnOpenChild" });
        await h.CallAsync("ui_wait", new { pid = h.Pid, window = "Child Settings", timeout_s = 10 });
        await h.CallAsync("ui_click", new { pid = h.Pid, window = "Child Settings", selector = "id=txtChild", mouse = true }); // focus in the child's field

        var windows = (await h.CallAsync("ui_list_windows", new { pid = h.Pid })).GetProperty("windows").EnumerateArray().ToArray();
        var main = windows.Single(w => w.GetProperty("is_main").GetBoolean()).GetProperty("bounds");
        var child = windows.Single(w => w.GetProperty("title").GetString() == "Child Settings").GetProperty("bounds");
        var x = child.GetProperty("x").GetInt32() + (child.GetProperty("width").GetInt32() / 2) - main.GetProperty("x").GetInt32();
        var y = child.GetProperty("y").GetInt32() + (child.GetProperty("height").GetInt32() / 2) - main.GetProperty("y").GetInt32();
        Assert.InRange(x, 0, main.GetProperty("width").GetInt32() - 1); // the child is centered on its owner
        Assert.InRange(y, 0, main.GetProperty("height").GetInt32() - 1);

        var (code, msg) = await h.CallErrorAsync("ui_click", new { pid = h.Pid, x, y });
        Assert.Equal(GatewayErrorCodes.TargetNotReady, code);
        Assert.Contains("Child Settings", msg); // names the window in front, to be targeted explicitly

        foreach (var (tool, args) in new (string, object)[] { ("ui_keys", new { pid = h.Pid, keys = "zz" }), ("ui_type", new { pid = h.Pid, text = "zz" }) })
        {
            var r = await h.CallRawAsync(tool, args);
            if (r.IsError)
            {
                Assert.Equal(GatewayErrorCodes.TargetNotReady, r.ErrorCode);
            }
        }

        var field = await h.CallAsync("ui_inspect", new { pid = h.Pid, window = "Child Settings", selector = "id=txtChild" });
        Assert.DoesNotContain("zz", field.GetProperty("value").GetString() ?? string.Empty);

        // Named explicitly, the child gets the keys (its own safety check applies).
        await h.CallAsync("ui_keys", new { pid = h.Pid, window = "Child Settings", selector = "id=txtChild", keys = "ok" });
        await h.CallAsync("ui_click", new { pid = h.Pid, window = "Child Settings", selector = "id=btnChildOk" });
        await h.CallAsync("ui_wait", new { pid = h.Pid, window = "Child Settings", state = "gone", timeout_s = 10 });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblStatus", text = "Child: ok", exact = true });
    }

    [DesktopFact]
    public async Task Win32MessageBox_IsDialogClass_AndCanBeAnswered()
    {
        using var h = UiHarness.Start();
        await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnMessage" });
        var w = await h.CallAsync("ui_wait", new { pid = h.Pid, window = "Test Message", timeout_s = 10 });
        Assert.Equal("Test Message", w.GetProperty("window").GetProperty("title").GetString());

        var list = await h.CallAsync("ui_list_windows", new { pid = h.Pid, title = "Test Message" });
        var box = Assert.Single(list.GetProperty("windows").EnumerateArray());
        Assert.Equal("#32770", box.GetProperty("class").GetString());
        Assert.True(box.GetProperty("is_modal_dialog").GetBoolean());

        var snap = await h.SnapshotTextAsync("Test Message");
        Assert.Contains("Button \"Yes\"", snap);
        Assert.Contains("Proceed with the test step?", snap);

        var yes = await h.CallAsync("ui_click", new { pid = h.Pid, window = "Test Message", selector = "type=Button;name=Yes" });
        Assert.Equal("Test Message", yes.GetProperty("target").GetProperty("window").GetString());
        await h.CallAsync("ui_wait", new { pid = h.Pid, window = "Test Message", state = "gone", timeout_s = 10 });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblStatus", text = "Message: Yes", exact = true });
    }

    [DesktopFact]
    public async Task Actions_AutoWaitForLateElements()
    {
        using var h = UiHarness.Start();
        await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnDelayed" });
        var r = await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnLate", timeout_s = 8 });
        Assert.Equal("invoked", r.GetProperty("action").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblStatus", text = "Late clicked" });
    }

    [DesktopFact]
    public async Task Expect_Failure_IsData_NotError()
    {
        using var h = UiHarness.Start();
        var r = await h.CallAsync("ui_expect", new { pid = h.Pid, selector = "id=lblStatus", text = "definitely not there", timeout_s = 0.5 });
        Assert.False(r.GetProperty("pass").GetBoolean());
        Assert.StartsWith("FAIL:", r.GetProperty("message").GetString());
        Assert.Equal("Ready", r.GetProperty("actual").GetString());

        var gone = await h.CallAsync("ui_expect", new { pid = h.Pid, selector = "id=noSuchThing", @is = "gone", timeout_s = 0.5 });
        Assert.True(gone.GetProperty("pass").GetBoolean());

        var regex = await h.CallAsync("ui_expect", new { pid = h.Pid, selector = "id=lblCount", match = "^Clicks: \\d+$" });
        Assert.True(regex.GetProperty("pass").GetBoolean());
    }

    [DesktopFact]
    public async Task ExpectedFailures_MapToErrorCodes()
    {
        using var h = UiHarness.Start();
        var (code, msg) = await h.CallErrorAsync("ui_click", new { pid = h.Pid, selector = "id=doesNotExist", timeout_s = 0.5 });
        Assert.Equal(GatewayErrorCodes.NotFound, code);
        Assert.Contains("auto-waited", msg);

        (code, _) = await h.CallErrorAsync("ui_click", new { pid = h.Pid, selector = "id=btnDisabled", timeout_s = 0.5 });
        Assert.Equal(GatewayErrorCodes.TargetNotReady, code);

        (code, msg) = await h.CallErrorAsync("ui_type", new { pid = h.Pid, selector = "id=txtPassword", text = "secret" });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, code);
        Assert.Contains("password", msg, StringComparison.OrdinalIgnoreCase);

        (code, _) = await h.CallErrorAsync("ui_keys", new { pid = h.Pid, keys = "%{F4}" });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, code);

        (code, _) = await h.CallErrorAsync("ui_click", new { pid = h.Pid, @ref = "e9999" });
        Assert.Equal(GatewayErrorCodes.NotFound, code);

        (code, _) = await h.CallErrorAsync("ui_click", new { pid = h.Pid, selector = "bogus=1" });
        Assert.Equal(GatewayErrorCodes.ValidationFailed, code);

        (code, _) = await h.CallErrorAsync("ui_snapshot", new { pid = h.Pid, window = "No Such Window Title 12345" });
        Assert.Equal(GatewayErrorCodes.NotFound, code);

        // The app is still alive and unchanged.
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblStatus", text = "Ready", exact = true });
    }

    [DesktopFact]
    public async Task Screenshot_WindowAndElement_SavedInsideHome()
    {
        using var h = UiHarness.Start();
        var raw = await h.CallRawAsync("ui_screenshot", new { pid = h.Pid, max_width = 800 });
        Assert.False(raw.IsError, UiHarness.Text(raw));
        var info = raw.Structured!.Value;
        var path = info.GetProperty("path").GetString()!;
        Assert.True(File.Exists(path));
        Assert.StartsWith(Path.GetFullPath(h.Paths.Root), path, StringComparison.OrdinalIgnoreCase);
        Assert.True(info.GetProperty("width").GetInt32() >= 900);
        Assert.Equal(800, info.GetProperty("image_width").GetInt32());
        var img = Assert.IsType<ImageContent>(raw.Content.Last());
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, img.Data[..4]);

        var el = await h.CallAsync("ui_screenshot", new { pid = h.Pid, selector = "id=gridOrders", include_image = false, save_path = Path.Combine(h.Paths.CapturesDir, "grid.png") });
        Assert.True(el.GetProperty("width").GetInt32() < info.GetProperty("width").GetInt32());
        Assert.True(File.Exists(Path.Combine(h.Paths.CapturesDir, "grid.png")));

        var (code, _) = await h.CallErrorAsync("ui_screenshot", new { pid = h.Pid, save_path = Path.Combine(Path.GetTempPath(), "outside.png") });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, code);
        (code, _) = await h.CallErrorAsync("ui_screenshot", new { pid = h.Pid, save_path = Path.Combine(h.Paths.AuditDir, "x.png") }); // inside home, not evidence
        Assert.Equal(GatewayErrorCodes.PermissionDenied, code);
    }

    [DesktopFact]
    public async Task MouseFallbacks_PointClickOnCanvas_DoubleClick_Scroll()
    {
        using var h = UiHarness.Start();
        var label = await h.CallAsync("ui_inspect", new { pid = h.Pid, selector = "id=lblCanvas" });
        var b = label.GetProperty("window_relative_bounds");
        int x = b.GetProperty("x").GetInt32() + 100, y = b.GetProperty("y").GetInt32() + 60;
        var click = await h.CallAsync("ui_click", new { pid = h.Pid, x, y });
        Assert.Equal("mouse", click.GetProperty("action").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCanvas", match = "^Canvas click at \\d+,\\d+$" });

        var dbl = await h.CallAsync("ui_click", new { pid = h.Pid, selector = "id=btnCount", @double = true });
        Assert.Equal("mouse", dbl.GetProperty("action").GetString());
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 2", exact = true });

        var sc = await h.CallAsync("ui_scroll", new { pid = h.Pid, selector = "id=lstLong", direction = "pagedown", amount = 2 });
        Assert.Equal("scrolled", sc.GetProperty("action").GetString());
        Assert.True(sc.GetProperty("vertical_percent").GetDouble() > 0);
    }

    [DesktopFact]
    public async Task Results_EchoTargetSelectorWindowAndHwnd_EvenForRefs()
    {
        using var h = UiHarness.Start();
        var snap = await h.SnapshotTextAsync();
        var r = UiHarness.RefOf(snap, "id=txtName");
        var main = (await h.CallAsync("ui_list_windows", new { pid = h.Pid })).GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("is_main").GetBoolean());
        var hwnd = main.GetProperty("hwnd").GetString();

        void AssertTarget(JsonElement result, string? selector)
        {
            var t = result.GetProperty("target");
            Assert.Equal(selector, t.TryGetProperty("selector", out var s) ? s.GetString() : null);
            Assert.Equal(UiTestApp.MainTitle, t.GetProperty("window").GetString());
            Assert.Equal(hwnd, t.GetProperty("hwnd").GetString());
        }

        AssertTarget(await h.CallAsync("ui_type", new { pid = h.Pid, @ref = r, text = "x" }), "id=txtName");
        AssertTarget(await h.CallAsync("ui_wait", new { pid = h.Pid, @ref = r }), "id=txtName");
        AssertTarget(await h.CallAsync("ui_expect", new { pid = h.Pid, @ref = r, text = "x" }), "id=txtName");
        AssertTarget(await h.CallAsync("ui_select", new { pid = h.Pid, selector = "id=cmbCity", index = 1 }), "id=cmbCity");
        AssertTarget(await h.CallAsync("ui_toggle", new { pid = h.Pid, selector = "id=chkAgree" }), "id=chkAgree");
        AssertTarget(await h.CallAsync("ui_expand", new { pid = h.Pid, selector = "type=TreeItem;name=Root" }), "type=TreeItem;name=Root");
        AssertTarget(await h.CallAsync("ui_scroll", new { pid = h.Pid, selector = "id=lstLong" }), "id=lstLong");
        AssertTarget(await h.CallAsync("ui_keys", new { pid = h.Pid, keys = "{ESC}" }), null);
        AssertTarget(await h.CallAsync("ui_wait", new { pid = h.Pid }), null);
        AssertTarget(await h.CallAsync("ui_expect", new { pid = h.Pid, selector = "id=gone123", @is = "gone", timeout_s = 0 }), "id=gone123");
    }

    [DesktopFact]
    public async Task Find_AssignsRefs_UsableForActions()
    {
        using var h = UiHarness.Start();
        var f = await h.CallAsync("ui_find", new { pid = h.Pid, selector = "type=Button;name~=Count" });
        Assert.Equal(1, f.GetProperty("count").GetInt32());
        var m = f.GetProperty("matches")[0];
        Assert.Equal("btnCount", m.GetProperty("id").GetString());
        var r = m.GetProperty("ref").GetString();
        await h.CallAsync("ui_click", new { pid = h.Pid, @ref = r });
        await h.ExpectAsync(new { pid = h.Pid, selector = "id=lblCount", text = "Clicks: 1", exact = true });

        var inspect = await h.CallAsync("ui_inspect", new { pid = h.Pid, @ref = r });
        Assert.Equal("Button", inspect.GetProperty("type").GetString());
        Assert.Contains("Invoke", inspect.GetProperty("patterns").EnumerateArray().Select(p => p.GetString()));
    }
}
