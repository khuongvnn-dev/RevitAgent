using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;
using RevitAgent.Core.Ui.Script;

namespace RevitAgent.Tests.Ui;

/// <summary>Pure logic of the UI engine (no desktop needed): selector grammar, key parsing, script validation, catalog.</summary>
public class UiUnitTests
{
    private static readonly string[] ToolNames =
    {
        "ui_list_windows", "ui_snapshot", "ui_find", "ui_inspect", "ui_click", "ui_type", "ui_select", "ui_toggle", "ui_expand",
        "ui_keys", "ui_scroll", "ui_wait", "ui_expect", "ui_read_table", "ui_screenshot", "ui_run_script",
    };

    [Fact]
    public async Task Catalog_HasAllUiTools_WithHonestPermissions()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        foreach (var name in ToolNames)
        {
            Assert.True(registry.TryGet(name, out var tool), name);
            var expected = name is "ui_click" or "ui_type" or "ui_select" or "ui_toggle" or "ui_expand" or "ui_keys" or "ui_scroll" or "ui_run_script"
                ? ToolPermission.Interact
                : ToolPermission.Read;
            Assert.Equal(expected, tool.Permission);
            Assert.True(tool.Description.Length > 80, name);
            Assert.Equal(JsonValueKind.Object, tool.InputSchema.GetProperty("properties").ValueKind);
        }

        Assert.Equal(ToolNames.Length, registry.Tools.Count(t => t.Name.StartsWith("ui_", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("id=btnOk", "btnOk", null, null, 0)]
    [InlineData("name=\"OK\" type=Button", null, "OK", "Button", 0)]
    [InlineData("type=TabItem;name=Architecture", null, "Architecture", "TabItem", 0)]
    [InlineData("automationid=ID_OBJECTS_WALL;controltype=button", "ID_OBJECTS_WALL", null, "Button", 0)]
    [InlineData("name=Save as type=Button index=2", null, "Save as", "Button", 2)]
    [InlineData("id=CommandButton_1", "CommandButton_1", null, null, 0)]
    public void Selector_ParsesSelectorGrammar(string text, string? id, string? name, string? type, int index)
    {
        var s = Selector.Parse(text);
        Assert.Equal(id, s.Id);
        Assert.Equal(name, s.Name);
        Assert.Equal(type, s.TypeName);
        Assert.Equal(index, s.Index);
    }

    [Fact]
    public void Selector_ContainsRegexPlainTextAndContainer()
    {
        var plain = Selector.Parse("Save");
        Assert.Equal("Save", plain.NameContains);
        Assert.True(plain.Matches(null, "Save As...", null, ControlTypes.Button));

        var contains = Selector.Parse("name~=sav;type=Button");
        Assert.True(contains.Matches("x", "Quick SAVE", "", ControlTypes.Button));
        Assert.False(contains.Matches("x", "Quick SAVE", "", ControlTypes.Edit));

        var rx = Selector.Parse("id=/^ID_.*WALL$/");
        Assert.True(rx.Matches("ID_OBJECTS_WALL", null, null, ControlTypes.Button));
        Assert.False(rx.Matches("ID_OBJECTS_DOOR", null, null, ControlTypes.Button));

        var nested = Selector.Parse("type=DataItem;in=id=gridOrders;from=end");
        Assert.Equal("id=gridOrders", nested.In);
        Assert.True(nested.FromEnd);

        var quoted = Selector.Parse("name='A;B' ; type=Text");
        Assert.Equal("A;B", quoted.Name);

        // id is exact and case-sensitive; name is case-insensitive and whitespace-normalized.
        Assert.False(Selector.Parse("id=btnok").Matches("btnOk", null, null, 0));
        Assert.True(Selector.Parse("name=hà  nội").Matches(null, "Hà Nội", null, 0));
    }

    [Theory]
    [InlineData("bogus=1")]
    [InlineData("type=NoSuchType")]
    [InlineData("index=-1;id=a")]
    [InlineData("id=/[unclosed/")]
    [InlineData("")]
    public void Selector_RejectsInvalidInput(string text)
    {
        var ex = Assert.Throws<GatewayException>(() => Selector.Parse(text));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void Selector_BuildRoundTrips()
    {
        Assert.Equal("id=btnOk", Selector.Build("btnOk", "OK", "Button"));
        Assert.Equal("name=OK;type=Button", Selector.Build(null, "OK", "Button"));
        Assert.Equal("name=\"A;B\";type=Text;index=2", Selector.Build(null, "A;B", "Text", 2));
        var s = Selector.Parse(Selector.Build(null, "A;B", "Text", 2));
        Assert.Equal("A;B", s.Name);
        Assert.Equal(2, s.Index);
    }

    [Fact]
    public void SendKeys_ParsesModifiersSpecialKeysAndRepeats()
    {
        var ctrlA = Assert.Single(InputSender.ParseSendKeys("^a"));
        Assert.Equal(new ushort[] { 0x11 }, ctrlA.Modifiers);
        Assert.Equal(0x41, ctrlA.VirtualKey);
        Assert.Null(ctrlA.Unicode);

        var enters = InputSender.ParseSendKeys("{ENTER 3}");
        Assert.Equal(3, enters.Count);
        Assert.All(enters, k => Assert.Equal(0x0D, k.VirtualKey));

        var shiftTab = Assert.Single(InputSender.ParseSendKeys("+{TAB}"));
        Assert.Equal(new ushort[] { 0x10 }, shiftTab.Modifiers);
        Assert.Equal(0x09, shiftTab.VirtualKey);

        Assert.True(InputSender.ContainsAltF4(InputSender.ParseSendKeys("%{F4}")));
        Assert.False(InputSender.ContainsAltF4(InputSender.ParseSendKeys("{F4}")));

        var text = InputSender.ParseSendKeys("Hà~{+}");
        Assert.Equal(4, text.Count);
        Assert.Equal('H', text[0].Unicode);
        Assert.Equal('à', text[1].Unicode);
        Assert.Equal(0x0D, text[2].VirtualKey); // ~ = Enter
        Assert.Equal('+', text[3].Unicode);

        var group = InputSender.ParseSendKeys("^(ac)");
        Assert.Equal(2, group.Count);
        Assert.All(group, k => Assert.Contains((ushort)0x11, k.Modifiers));
        Assert.Equal(0x41, group[0].VirtualKey); // Ctrl+A as a virtual key, not Unicode 'a'
        Assert.Null(group[0].Unicode);

        Assert.Equal(0x71, Assert.Single(InputSender.ParseSendKeys("{F2}")).VirtualKey);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => InputSender.ParseSendKeys("{")).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => InputSender.ParseSendKeys("a{")).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => InputSender.ParseSendKeys("{NOPE}")).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => InputSender.ParseSendKeys("^")).Code);
    }

    /// <summary>
    /// Untargeted input (ui_keys / ui_type without ref, ui_click x/y) must reach the very window that passed the safety check:
    /// activating Revit's main window brings its modal "Security - Unsigned Add-In" dialog (same pid) to the front, and Enter
    /// would answer it (review finding 2026-11-27). Without a required root only the process is checked, as before.
    /// </summary>
    [Theory]
    [InlineData(100, 0L, 100, 0x10L, true)] // no required root: any window of the process
    [InlineData(100, 0L, 200, 0x10L, false)] // another process
    [InlineData(100, 0L, 100, 0L, false)] // no window in front / under the point
    [InlineData(100, 0x10L, 100, 0x10L, true)] // the target window itself
    [InlineData(100, 0x10L, 100, 0x20L, false)] // another window of the same process (its modal dialog)
    [InlineData(100, 0x10L, 200, 0x10L, false)]
    [InlineData(100, 0x10L, 100, 0L, false)]
    public void InputTarget_IsTheRequiredTopLevelWindow_NotJustTheSameProcess(int pid, long requireRoot, int actualPid, long actualRoot, bool allowed) =>
        Assert.Equal(allowed, InputSender.IsInputTarget(pid, new IntPtr(requireRoot), actualPid, new IntPtr(actualRoot)));

    [Fact]
    public async Task KeysDescription_SaysKeysGoToTheTargetWindow()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        Assert.True(sp.GetRequiredService<ToolRegistry>().TryGet("ui_keys", out var tool));
        Assert.Contains("only sent while the target window is in the foreground", tool.Description);
    }

    [Fact]
    public void ScriptValidator_AcceptsTheDocumentedExampleFormat()
    {
        var s = UiScript.Parse("""
        {
          "name": "Open settings, change Scale min",
          "timeout": 15,
          "screenshots": "fail",
          "steps": [
            { "name": "click ribbon button", "action": "click", "selector": "id=ID_SHEET_CONFIGURE" },
            { "action": "wait", "window": "Sheet Configure", "timeout_s": 30 },
            { "action": "select", "window": "Sheet Configure", "selector": "id=tabViewSplit" },
            { "action": "type", "selector": "id=txtScaleMin", "text": "45", "keyboard": true, "verify": true },
            { "action": "keys", "keys": "{TAB}" },
            { "action": "expect", "selector": "id=txtJsonPreview", "match": "\"ScaleMin\"\\s*:\\s*45" },
            { "action": "screenshot", "save_path": "${env:REVITAGENT_UI_OUT}\\settings.png" },
            { "action": "click", "window": "Sheet Configure", "selector": "id=7" },
            { "action": "expect", "window": "Sheet Configure", "is": "gone" },
            { "action": "keys", "keys": "^s" }
          ]
        }
        """);
        var v = UiScriptValidator.Validate(s);
        Assert.True(v.Ok, string.Join("; ", v.Errors.Select(e => e.Message)));
        Assert.Single(v.Warnings); // Ctrl+S warning
        Assert.Equal(10, s.Steps.Count);
        Assert.Equal("click", s.Steps[0].Action);
        Assert.Equal(15, s.TimeoutSeconds);
    }

    [Theory]
    [InlineData("""{ "steps": [ { "action": "launch", "selector": "Revit.exe" } ] }""", "launch")]
    [InlineData("""{ "steps": [ { "action": "run", "selector": "other.json" } ] }""", "run")]
    [InlineData("""{ "steps": [ { "action": "close" } ] }""", "close")]
    [InlineData("""{ "steps": [ { "action": "keys", "keys": "%{F4}" } ] }""", "Alt+F4")]
    [InlineData("""{ "steps": [ { "action": "type", "selector": "id=txtPassword", "text": "x" } ] }""", "password")]
    [InlineData("""{ "dialogs": [ { "title": "Security - Unsigned Add-In", "click": "id=CommandLink_1001" } ], "steps": [ { "action": "pause" } ] }""", "security")]
    [InlineData("""{ "dialogs": [ { "title": "Confirm", "text": "Delete the selected sheets?", "click": "name=Yes" } ], "steps": [ { "action": "pause" } ] }""", "delete")]
    [InlineData("""{ "steps": [ { "action": "click", "window": "Security - Unsigned Add-In", "selector": "name=\"Always Load\"" } ] }""", "security")]
    [InlineData("""{ "steps": [ { "action": "click", "window": "Đăng nhập", "selector": "id=ok" } ] }""", "sign in")]
    [InlineData("""{ "dialogs": [ { "title": "Autodesk Revit", "click": "name=\"Yes\"" } ], "steps": [ { "action": "pause" } ] }""", "generic title")]
    [InlineData("""{ "dialogs": [ { "title": "Revit 2024", "click": "name=\"Yes\"" } ], "steps": [ { "action": "pause" } ] }""", "generic title")]
    [InlineData("""{ "dialogs": [ { "click": "name=\"Yes\"" } ], "steps": [ { "action": "pause" } ] }""", "no title")]
    [InlineData("""{ "dialogs": [ { "title": "Ghi đè tệp", "click": "name=Có" } ], "steps": [ { "action": "pause" } ] }""", "ghi đè")]
    public void ScriptValidator_RejectsForbiddenContent(string json, string fragment)
    {
        var v = UiScriptValidator.Validate(UiScript.Parse(json));
        Assert.False(v.Ok);
        var issue = Assert.Single(v.Errors);
        Assert.True(issue.Forbidden, issue.Message);
        Assert.Contains(fragment, issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{ "steps": [] }""", "no steps")]
    [InlineData("""{ "steps": [ { "selector": "id=x" } ] }""", "no 'action'")]
    [InlineData("""{ "steps": [ { "action": "clickk" } ] }""", "not a UI script action")]
    [InlineData("""{ "steps": [ { "action": "dblclick", "selector": "id=a" } ] }""", "not a UI script action")]
    [InlineData("""{ "screenshots": "always", "steps": [ { "action": "pause" } ] }""", "screenshots")]
    public void ScriptValidator_ReportsFormatErrors(string json, string fragment)
    {
        var v = UiScriptValidator.Validate(UiScript.Parse(json));
        var issue = Assert.Single(v.Errors);
        Assert.False(issue.Forbidden);
        Assert.Contains(fragment, issue.Message);
    }

    [Fact]
    public void ScriptValidator_LoadsRulesFilesRelativeToTheScript()
    {
        var dir = TestGateway.NewTempDir("ui_script_");
        File.WriteAllText(Path.Combine(dir, "rules.json"), """[ { "name": "Copied Central Model -> Close", "proc": "Revit", "title": "Copied Central Model", "click": "id=CommandButton_8" } ]""");
        File.WriteAllText(Path.Combine(dir, "bad.json"), """{ "rules": [ { "title": "File Recovery", "click": "name=Yes" } ] }""");
        var script = Path.Combine(dir, "s.json");
        File.WriteAllText(script, """{ "dialogs": "rules.json", "steps": [ { "action": "pause", "ms": 1 } ] }""");
        var ok = UiScriptValidator.Validate(UiScript.Load(script));
        Assert.True(ok.Ok);
        Assert.Equal("id=CommandButton_8", Assert.Single(ok.Rules).Click);

        File.WriteAllText(script, """{ "dialogs": "rules.json, bad.json", "steps": [ { "action": "pause" } ] }""");
        var bad = UiScriptValidator.Validate(UiScript.Load(script));
        Assert.Contains(bad.Errors, e => e.Forbidden && e.Message.Contains("recover", StringComparison.OrdinalIgnoreCase));

        File.WriteAllText(script, """{ "dialogs": "missing.json", "steps": [ { "action": "pause" } ] }""");
        Assert.Contains(UiScriptValidator.Validate(UiScript.Load(script)).Errors, e => e.Message.Contains("not found"));
    }

    [Fact]
    public void ScriptVars_ExpandScriptVarsAndEnvironment()
    {
        var vars = new UiScriptVars();
        vars.Load(JsonDocument.Parse("""{ "customer": "Nguyễn Văn An", "n": 3 }""").RootElement);
        vars.SetEnv("REVITAGENT_UI_HOST_PID", "1234");
        Assert.Equal("Saved: Nguyễn Văn An x3", vars.Expand("Saved: ${customer} x${n}"));
        Assert.Equal("pid:1234", vars.Expand("pid:${env:REVITAGENT_UI_HOST_PID}"));
        Assert.Equal("[]", vars.Expand("[${env:PATH}]")); // only REVITAGENT_UI_* comes from the environment
        Environment.SetEnvironmentVariable("REVITAGENT_UI_TEST_VAR", "value");
        try
        {
            Assert.Equal("value", vars.Expand("${env:REVITAGENT_UI_TEST_VAR}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVITAGENT_UI_TEST_VAR", null);
        }
    }

    [Fact]
    public void EvidencePaths_OnlyRunsAndCaptures()
    {
        var paths = new RevitAgent.Core.AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        Assert.Equal(Path.Combine(paths.RunsDir, "r1", "a.png"), UiPaths.Evidence(paths, "a.png", Path.Combine(paths.RunsDir, "r1")));
        Assert.Equal(Path.Combine(paths.CapturesDir, "b.png"), UiPaths.Evidence(paths, Path.Combine(paths.CapturesDir, "b.png"), paths.RunsDir));
        foreach (var bad in new[] { Path.Combine(paths.AuditDir, "x.jsonl"), Path.Combine(paths.ConfigDir, "rules.json"), Path.Combine(paths.RunsDir, "..", "config", "x"), Path.Combine(Path.GetTempPath(), "x.png"), paths.RunsDir + "-evil\\x.png" })
        {
            Assert.Equal(GatewayErrorCodes.PermissionDenied, Assert.Throws<GatewayException>(() => UiPaths.Evidence(paths, bad, paths.RunsDir)).Code);
        }
    }

    [Fact]
    public async Task RunScript_RejectsForbiddenScriptsBeforeTouchingAnyUi()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var args = JsonSerializer.SerializeToElement(new
        {
            pid = Environment.ProcessId,
            script = new { steps = new object[] { new { action = "launch", selector = "Revit.exe" } } },
        });
        var r = await registry.InvokeAsync(new ToolCall("ui_run_script", args, "s"), CancellationToken.None);
        Assert.True(r.IsError);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
        Assert.Contains("launch", ((TextContent)r.Content[0]).Text);

        var valid = await registry.InvokeAsync(
            new ToolCall("ui_run_script", JsonSerializer.SerializeToElement(new { pid = Environment.ProcessId, validate_only = true, script = "{\"steps\":[{\"action\":\"pause\",\"ms\":1}]}" }), "s"),
            CancellationToken.None);
        Assert.False(valid.IsError, ((TextContent)valid.Content[0]).Text);
        Assert.True(valid.Structured!.Value.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Tools_ValidateArgumentsWithoutTouchingTheDesktop()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();

        async Task<string?> Code(string tool, object args) =>
            (await registry.InvokeAsync(new ToolCall(tool, JsonSerializer.SerializeToElement(args), "s"), CancellationToken.None)).ErrorCode;

        Assert.Equal(GatewayErrorCodes.NotFound, await Code("ui_list_windows", new { pid = int.MaxValue }));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, await Code("ui_find", new { pid = Environment.ProcessId }));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, await Code("ui_keys", new { pid = Environment.ProcessId, keys = "{BOGUS}" }));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, await Code("ui_run_script", new { pid = Environment.ProcessId }));
    }
}
