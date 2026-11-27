using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Macros;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;
using RevitAgent.Tests.Scenarios;

namespace RevitAgent.Tests.Macros;

/// <summary>Built-in macro files: they load, their tools exist in the real gateway and they are documented.</summary>
public class BuiltinMacroTests
{
    [Fact]
    public void BuiltinMacros_LoadWithoutIssues()
    {
        var library = new MacroLibrary();
        Assert.True(library.Issues.Count == 0, string.Join("\n", library.Issues));
        Assert.Contains("create-schedule", library.Names);
        Assert.Contains("run-addin-command", library.Names);
        Assert.Contains("run-revit-command", library.Names);
        Assert.Contains("read-schedule", library.Names);
        Assert.Equal(MacroLibrary.ReadBuiltins().Count, library.Macros.Count);
    }

    /// <summary>Every step tool is registered in the gateway (built like the Host/Shim build it) and every step arg is in that tool's schema.</summary>
    [Fact]
    public async Task BuiltinMacros_StepToolsExistInGatewayRegistry_AndArgsMatchTheirSchemas()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var problems = new List<string>();
        foreach (var macro in sp.GetRequiredService<MacroLibrary>().Macros)
        {
            foreach (var step in macro.Steps)
            {
                if (!registry.TryGet(step.Tool, out var tool))
                {
                    problems.Add($"{macro.Name} step '{step.Id}': tool '{step.Tool}' is not in the gateway registry.");
                    continue;
                }

                var props = tool.InputSchema.GetProperty("properties");
                foreach (var arg in step.Args.Select(a => a.Key))
                {
                    if (!props.TryGetProperty(arg, out _))
                    {
                        problems.Add($"{macro.Name} step '{step.Id}': {step.Tool} has no argument '{arg}'.");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void BuiltinMacroNames_AreDocumentedInMacrosMd()
    {
        var doc = MacrosDoc();
        foreach (var name in new MacroLibrary().Names)
        {
            Assert.True(doc.Contains($"`{name}`", StringComparison.Ordinal), $"docs/agent-guide/macros.md does not mention `{name}`.");
        }
    }

    [Fact]
    public void MacrosMd_YamlExample_IsAValidMacro()
    {
        var doc = MacrosDoc();
        var start = doc.IndexOf("```yaml", StringComparison.Ordinal);
        Assert.True(start >= 0, "macros.md has no ```yaml example");
        start = doc.IndexOf('\n', start) + 1;
        var yaml = doc[start..doc.IndexOf("```", start, StringComparison.Ordinal)];
        var (macro, errors) = MacroLoader.Parse(yaml, "macros.md");
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.NotNull(macro);
    }

    private static string MacrosDoc()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "RevitAgent.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!, "docs", "agent-guide", "macros.md"));
    }

    [Fact]
    public async Task Gateway_ExposesExactlyTwoMacroTools_AndGatewayInfoListsMacros()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        Assert.Equal(new[] { "macro_list", "macro_run" }, registry.Tools.Select(t => t.Name).Where(n => n.StartsWith("macro_", StringComparison.Ordinal)).OrderBy(n => n));
        Assert.Equal(ToolPermission.Write, registry.Tools.Single(t => t.Name == "macro_run").Permission);

        var info = await registry.InvokeAsync("gateway_get_info", null, "s1", CancellationToken.None);
        Assert.False(info.IsError);
        var macros = info.Structured!.Value.GetProperty("macros").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("create-schedule", macros);
        Assert.Contains("run-addin-command", macros);
    }

    [Theory]
    [InlineData("macro_list")]
    [InlineData("macro_run")]
    public async Task MacroToolDescriptions_AreConcise(string name)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        Assert.True(sp.GetRequiredService<ToolRegistry>().TryGet(name, out var tool));
        var paramText = tool.InputSchema.GetProperty("properties").EnumerateObject()
            .Sum(p => p.Name.Length + (p.Value.TryGetProperty("description", out var d) ? d.GetString()!.Length : 0));
        var total = tool.Description.Length + paramText;
        Assert.True(total <= 600, $"{name}: description + params = {total} chars (max 600).");
        Assert.Contains(name == "macro_list" ? "call this first" : "macro_list", tool.Description);
    }
}

/// <summary>Loader validation and templating.</summary>
public class MacroLoaderTests
{
    private const string Valid = """
        name: demo
        summary: Demo macro.
        params:
          pid: {type: integer, required: true}
          fields: {type: array}
          limit: {type: integer, default: 5}
        steps:
          - id: first
            tool: echo_tool
            args:
              pid: ${pid}
              fields: ${fields}
          - id: second
            tool: echo_tool
            args:
              pid: ${pid}
              label: "n=${first.count} of ${limit}"
        returns:
          count: ${first.count}
        """;

    [Fact]
    public void ValidMacro_Parses_WithTypedDefaults()
    {
        var (macro, errors) = MacroLoader.Parse(Valid);
        Assert.Empty(errors);
        Assert.NotNull(macro);
        Assert.Equal(2, macro!.Steps.Count);
        Assert.Equal(5, macro.Param("limit")!.Default!.GetValue<long>());
        Assert.Equal("pid:int*, fields:string[], limit:int=5", MacroListTool.ParamLine(macro));
    }

    [Theory]
    [InlineData("name: Bad_Name\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool}", "kebab-case")]
    [InlineData("name: x\nsteps:\n  - {id: a, tool: echo_tool}", "Missing 'summary'")]
    [InlineData("name: x\nsummary: s\nsteps: []", "non-empty list")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool, args: {v: '${nope}'}}", "neither a declared param nor an earlier step id")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool, args: {v: '${b.id}'}}\n  - {id: b, tool: echo_tool}", "earlier step id")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool}\n  - {id: a, tool: echo_tool}", "duplicate step id")]
    [InlineData("name: x\nsummary: s\nparams:\n  p: {type: integer, required: true, default: 3}\nsteps:\n  - {id: a, tool: echo_tool}", "required and also has a default")]
    [InlineData("name: x\nsummary: s\nparams:\n  p: {type: integer, default: abc}\nsteps:\n  - {id: a, tool: echo_tool}", "default does not match")]
    [InlineData("name: x\nsummary: s\nparams:\n  p: {type: text}\nsteps:\n  - {id: a, tool: echo_tool}", "type must be one of")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: macro_run}", "no nested macros")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool, expect: {path: items}}", "not_empty: true or count")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool, args: {v: '${a'}}", "unclosed template")]
    [InlineData("name: x\nsummary: s\nrequires: [cloud]\nsteps:\n  - {id: a, tool: echo_tool}", "'requires' value")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool, retry: 2}", "unknown key 'retry'")]
    [InlineData("name: x\nsummary: s\nsteps:\n  - {id: a, tool: echo_tool}\nreturns: {v: '${zzz}'}", "neither a declared param")]
    public void InvalidMacro_IsReported(string yaml, string expected)
    {
        var (_, errors) = MacroLoader.Parse(yaml);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void SummaryOver120Chars_IsReported()
    {
        var (_, errors) = MacroLoader.Parse($"name: x\nsummary: {new string('a', 121)}\nsteps:\n  - {{id: a, tool: echo_tool}}");
        Assert.Contains(errors, e => e.Contains("≤ 120", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateNamesAcrossFiles_KeepTheFirst()
    {
        var library = MacroLibrary.FromYaml(("a.yaml", Valid), ("b.yaml", Valid));
        Assert.Single(library.Macros);
        Assert.Contains(library.Issues, i => i.Message.Contains("Duplicate macro name", StringComparison.Ordinal) && i.Source == "b.yaml");
    }

    [Fact]
    public void Templates_KeepJsonTypeWhenWhole_InterpolateOtherwise_AndDropMissing()
    {
        var scope = new MacroScope();
        scope.Set("pid", JsonValue.Create(42L));
        scope.Set("fields", new JsonArray("Level", "Name"));
        scope.Set("find", JsonNode.Parse("""{"items":[{"command_id":"CustomCtrl_%A%B","n":1}],"ok":true}"""));

        var template = JsonNode.Parse("""
            {"pid":"${pid}","fields":"${fields}","cmd":"${find.items[0].command_id}","flag":"${find.ok}",
             "label":"pid ${pid}: ${fields}","gone":"${tab}","deep":"${find.items[3].command_id}","list":["${tab}","x"],"lit":7}
            """);
        Assert.True(MacroTemplate.TryResolve(template, scope, out var resolved));
        var o = resolved!.AsObject();
        Assert.Equal(JsonValueKind.Number, o["pid"]!.GetValueKind());
        Assert.Equal(2, o["fields"]!.AsArray().Count);
        Assert.Equal("CustomCtrl_%A%B", o["cmd"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.True, o["flag"]!.GetValueKind());
        Assert.Equal("pid 42: [\"Level\",\"Name\"]", o["label"]!.GetValue<string>());
        Assert.False(o.ContainsKey("gone"));
        Assert.False(o.ContainsKey("deep"));
        Assert.Equal("[\"x\"]", o["list"]!.ToJsonString());
        Assert.Equal(7, o["lit"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("\"\"", false)]
    [InlineData("\"  \"", false)]
    [InlineData("false", false)]
    [InlineData("[]", false)]
    [InlineData("null", false)]
    [InlineData("\"Schedule\"", true)]
    [InlineData("0", true)]
    [InlineData("[1]", true)]
    public void When_IsFalseForMissingEmptyFalseOrNull(string? json, bool expected)
    {
        var scope = new MacroScope();
        if (json is not null)
        {
            scope.Set("v", JsonNode.Parse(json));
        }

        var present = MacroTemplate.TryResolve("${v}", scope, out var value);
        Assert.Equal(expected, MacroTemplate.IsTruthy(present, value));
    }

    [Fact]
    public void WholeTemplate_DoesNotMatchBeforeATrailingNewline()
    {
        var scope = new MacroScope();
        scope.Set("n", JsonValue.Create(7L));
        Assert.True(MacroTemplate.TryResolve("${n}\n", scope, out var withNewline));
        Assert.Equal(JsonValueKind.String, withNewline!.GetValueKind());
        Assert.True(MacroTemplate.TryResolve("${n}", scope, out var whole));
        Assert.Equal(JsonValueKind.Number, whole!.GetValueKind());
    }

    [Theory]
    [InlineData("integer", "\"12\"", "12")]
    [InlineData("integer", "12.0", "12")]
    [InlineData("number", "\"1.5\"", "1.5")]
    [InlineData("boolean", "\"true\"", "true")]
    [InlineData("string", "5", "\"5\"")]
    [InlineData("array", "\"Name\"", "[\"Name\"]")]
    [InlineData("array", "[\"A\",2]", "[\"A\",\"2\"]")]
    public void Coerce_ConvertsFriendlyInput(string type, string input, string expected)
    {
        var p = new MacroParam("p", type, false, null, null);
        Assert.Equal(expected, MacroLoader.Coerce(p, JsonNode.Parse(input)!).ToJsonString());
    }

    [Theory]
    [InlineData("integer", "\"abc\"")]
    [InlineData("integer", "1.5")]
    [InlineData("number", "\"NaN\"")]
    [InlineData("number", "\"Infinity\"")]
    [InlineData("number", "\"-Infinity\"")]
    [InlineData("boolean", "\"maybe\"")]
    [InlineData("array", "{\"a\":1}")]
    [InlineData("string", "{\"a\":1}")]
    public void Coerce_RejectsWrongTypes(string type, string input)
    {
        var p = new MacroParam("p", type, false, null, null);
        var ex = Assert.Throws<GatewayException>(() => MacroLoader.Coerce(p, JsonNode.Parse(input)!));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex.Code);
    }
}

/// <summary>macro_run / macro_list against fake Bridge/UI tools, through the real registry, policy and audit.</summary>
public class MacroRunTests
{
    [Fact]
    public async Task RunAddinCommand_PostsTheSingleMatch_SkipsWaitWithoutWindow_AndIsCompact()
    {
        await using var host = new MacroHost();
        var r = await host.CallOkAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Export" } });

        Assert.True(r.GetProperty("ok").GetBoolean());
        var find = host.Last("ribbon_list_items");
        Assert.Equal("Export", find.GetProperty("text_contains").GetString());
        Assert.True(find.GetProperty("postable_only").GetBoolean());
        Assert.False(find.TryGetProperty("tab", out _));
        Assert.Equal(999, find.GetProperty("pid").GetInt32());
        Assert.Equal("CustomCtrl_%Acme%Tools%Export", host.Last("command_post").GetProperty("command").GetString());
        Assert.Equal(0, host.Count("ui_wait"));
        Assert.Equal(1, host.Count("revit_list_dialogs"));

        var result = r.GetProperty("result");
        Assert.Equal("Export sheets", result.GetProperty("item").GetProperty("text").GetString());
        Assert.Equal(1, result.GetProperty("dialog_count").GetInt32());
        Assert.Equal("Acme Export", result.GetProperty("dialogs")[0].GetProperty("title").GetString());
        Assert.False(result.TryGetProperty("window", out _));

        var steps = r.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(new[] { "find", "post", "wait", "dialogs" }, steps.Select(s => s.GetProperty("id").GetString()));
        Assert.True(steps[2].GetProperty("skipped").GetBoolean());
        Assert.All(steps, s => Assert.False(s.TryGetProperty("output", out _)));
        Assert.All(steps, s => Assert.True(s.TryGetProperty("ms", out _)));
    }

    [Fact]
    public async Task RunAddinCommand_WaitsForTheWindow_AndVerboseAddsOutputs()
    {
        await using var host = new MacroHost();
        var r = await host.CallOkAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Export", tab = "Acme", wait_window = "Acme Export" }, verbose = true });

        var wait = host.Last("ui_wait");
        Assert.Equal("Acme Export", wait.GetProperty("window").GetString());
        Assert.Equal(20, wait.GetProperty("timeout_s").GetInt32());
        Assert.Equal("Acme", host.Last("ribbon_list_items").GetProperty("tab").GetString());
        Assert.Equal("Acme Export", r.GetProperty("result").GetProperty("window").GetProperty("title").GetString());
        Assert.All(r.GetProperty("steps").EnumerateArray(), s => Assert.True(s.TryGetProperty("output", out _)));
    }

    [Fact]
    public async Task RunAddinCommand_SeveralMatches_StopsWithExpectationFailed_BeforePosting()
    {
        await using var host = new MacroHost();
        var r = await host.CallAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Acme" } });

        Assert.True(r.IsError);
        Assert.Equal(MacroErrorCodes.ExpectationFailed, r.ErrorCode);
        var body = r.Structured!.Value;
        Assert.Contains("got 2", body.GetProperty("message").GetString());
        var details = body.GetProperty("details");
        Assert.Equal("find", details.GetProperty("failed_step").GetString());
        Assert.False(details.GetProperty("ok").GetBoolean());
        Assert.Equal(2, details.GetProperty("step_details").GetProperty("sample").GetArrayLength());
        Assert.Single(details.GetProperty("steps").EnumerateArray());
        Assert.Equal(0, host.Count("command_post"));
    }

    [Fact]
    public async Task FailedStep_StopsTheMacro_WithTheStepsErrorCode()
    {
        await using var host = new MacroHost();
        host.PostError = new GatewayException(GatewayErrorCodes.Conflict, "Revit cannot post it right now.");
        var r = await host.CallAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Export", wait_window = "X" } });

        Assert.True(r.IsError);
        Assert.Equal(GatewayErrorCodes.Conflict, r.ErrorCode);
        var details = r.Structured!.Value.GetProperty("details");
        Assert.Equal("post", details.GetProperty("failed_step").GetString());
        Assert.Equal(GatewayErrorCodes.Conflict, details.GetProperty("error_code").GetString());
        Assert.Contains("Revit cannot post it right now.", r.Structured!.Value.GetProperty("message").GetString());
        Assert.Equal(0, host.Count("ui_wait"));
        Assert.Equal(0, host.Count("revit_list_dialogs"));
    }

    [Fact]
    public async Task RunRevitCommand_PostsTheNameAsGiven_WaitsWhenAsked_AndListsDialogs()
    {
        await using var host = new MacroHost();
        var r = await host.CallOkAsync("macro_run", new { name = "run-revit-command", args = new { pid = 999, command = "VisibilityOrGraphics", wait_window = "Visibility/Graphics" } });

        var post = host.Last("command_post");
        Assert.Equal(999, post.GetProperty("pid").GetInt32());
        Assert.Equal("VisibilityOrGraphics", post.GetProperty("command").GetString());
        var wait = host.Last("ui_wait");
        Assert.Equal("Visibility/Graphics", wait.GetProperty("window").GetString());
        Assert.Equal(20, wait.GetProperty("timeout_s").GetInt32());
        Assert.Equal(1, host.Count("revit_list_dialogs"));

        var result = r.GetProperty("result");
        Assert.Equal("VisibilityOrGraphics", result.GetProperty("command_id").GetString());
        Assert.Equal(1, result.GetProperty("dialog_count").GetInt32());
    }

    [Fact]
    public async Task RunRevitCommand_SkipsWaitWithoutWindow()
    {
        await using var host = new MacroHost();
        await host.CallOkAsync("macro_run", new { name = "run-revit-command", args = new { pid = 999, command = "ReviewWarnings" } });
        Assert.Equal(0, host.Count("ui_wait"));
        Assert.Equal(1, host.Count("command_post"));
    }

    [Fact]
    public async Task ReadSchedule_ForwardsOffsetAndLimit_AndReturnsThePage()
    {
        await using var host = new MacroHost();
        var r = await host.CallOkAsync("macro_run", new { name = "read-schedule", args = new { pid = 999, view_id = 777, offset = 1000, limit = 500 } });

        var read = host.Last("schedule_read");
        Assert.Equal(777, read.GetProperty("view_id").GetInt64());
        Assert.Equal(1000, read.GetProperty("offset").GetInt32());
        Assert.Equal(500, read.GetProperty("limit").GetInt32());
        Assert.False(read.TryGetProperty("name", out _));

        var result = r.GetProperty("result");
        Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task CreateSchedule_ForwardsTypedArgs_OmitsUnsetOnes_AndReturnsRows()
    {
        await using var host = new MacroHost();
        var r = await host.CallOkAsync("macro_run", new { name = "create-schedule", args = new { pid = "999", category = "OST_Rooms", fields = new[] { "Level", "Number", "Name" }, sort_by = "Level" } });

        var create = host.Last("schedule_create");
        Assert.Equal(999, create.GetProperty("pid").GetInt32());
        Assert.Equal(3, create.GetProperty("fields").GetArrayLength());
        Assert.Equal(JsonValueKind.Array, create.GetProperty("sort_by").ValueKind);
        Assert.False(create.TryGetProperty("name", out _));
        Assert.False(create.TryGetProperty("sort_descending", out _));

        var read = host.Last("schedule_read");
        Assert.Equal(777, read.GetProperty("view_id").GetInt64());
        Assert.Equal(100, read.GetProperty("limit").GetInt32());

        var result = r.GetProperty("result");
        Assert.Equal(777, result.GetProperty("view_id").GetInt64());
        Assert.Equal("Room Schedule", result.GetProperty("name").GetString());
        Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
        Assert.Equal("L1", result.GetProperty("rows")[0][0].GetString()); // data rows only: no heading row
        Assert.Equal(2, result.GetProperty("data_rows").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.False(result.TryGetProperty("total_rows", out _));
    }

    [Fact]
    public async Task EveryStep_GoesThroughTheRegistry_WithTheCallersSession_AndPolicy()
    {
        await using var host = new MacroHost();
        await host.CallOkAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Export" } }, session: "agent-7");
        var audited = host.Services.GetRequiredService<AuditLog>().Recent();
        Assert.Equal(new[] { "ribbon_list_items", "command_post", "revit_list_dialogs", "macro_run" }, audited.Select(a => a.Tool));
        Assert.All(audited, a => Assert.Equal("agent-7", a.SessionId));

        // macro_run is Write: a lower permission ceiling refuses it before any step runs.
        host.Services.GetRequiredService<GatewayPolicy>().MaxPermission = ToolPermission.Interact;
        var denied = await host.CallAsync("macro_run", new { name = "create-schedule", args = new { pid = 999, category = "OST_Rooms", fields = new[] { "Name" } } });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, denied.ErrorCode);
        Assert.Equal(0, host.Count("schedule_create"));
        Assert.Equal(0, host.Count("schedule_read"));

        // A tool disabled by policy is refused as a step too.
        host.Services.GetRequiredService<GatewayPolicy>().MaxPermission = ToolPermission.Admin;
        host.Services.GetRequiredService<GatewayPolicy>().Deny("command_post");
        var disabled = await host.CallAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Export" } });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, disabled.ErrorCode);
        Assert.Equal("post", disabled.Structured!.Value.GetProperty("details").GetProperty("failed_step").GetString());
    }

    [Fact]
    public async Task ActionGrant_IsRequired_ForInteractStepsOnAUserStartedRevit()
    {
        await using var host = new MacroHost(userRevitPid: 999);
        var r = await host.CallAsync("macro_run", new { name = "run-addin-command", args = new { pid = 999, text = "Export" } });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
        Assert.Equal("post", r.Structured!.Value.GetProperty("details").GetProperty("failed_step").GetString());
        Assert.Equal(1, host.Count("ribbon_list_items"));
    }

    [Theory]
    [InlineData("{\"name\":\"nope\"}", "NOT_FOUND")]
    [InlineData("{\"name\":\"create-schedule\",\"args\":{\"pid\":1}}", "VALIDATION_FAILED")]
    [InlineData("{\"name\":\"create-schedule\",\"args\":{\"pid\":1,\"category\":\"OST_Rooms\",\"fields\":[\"Name\"],\"colour\":\"red\"}}", "VALIDATION_FAILED")]
    [InlineData("{\"name\":\"create-schedule\",\"args\":{\"pid\":\"abc\",\"category\":\"OST_Rooms\",\"fields\":[\"Name\"]}}", "VALIDATION_FAILED")]
    public async Task BadCalls_AreRejectedBeforeAnyStep(string json, string code)
    {
        await using var host = new MacroHost();
        var r = await host.Registry.InvokeAsync("macro_run", JsonDocument.Parse(json).RootElement, "s1", CancellationToken.None);
        Assert.Equal(code, r.ErrorCode);
        Assert.Equal(0, host.Count("schedule_create"));
    }

    [Fact]
    public async Task MissingStepTool_IsDependencyFailed_AndNothingRuns()
    {
        await using var host = new MacroHost(without: "schedule_create");
        var r = await host.CallAsync("macro_run", new { name = "create-schedule", args = new { pid = 999, category = "OST_Rooms", fields = new[] { "Name" } } });
        Assert.Equal(GatewayErrorCodes.DependencyFailed, r.ErrorCode);
        Assert.Contains("schedule_create", r.Structured!.Value.GetProperty("message").GetString());

        var list = await host.CallOkAsync("macro_list");
        var cs = list.GetProperty("macros").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "create-schedule");
        Assert.Equal("schedule_create", cs.GetProperty("unavailable_tools")[0].GetString());
    }

    [Fact]
    public async Task MacroList_IsCompact_AndDetailShowsParamsAndStepTools()
    {
        await using var host = new MacroHost();
        var list = await host.CallOkAsync("macro_list");
        var cs = list.GetProperty("macros").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "create-schedule");
        Assert.Equal(
            "pid:int*, category:string*, fields:string[]*, sort_by:string[], sort_descending:bool, name:string, limit:int=100",
            cs.GetProperty("params").GetString());
        Assert.Equal("bridge", cs.GetProperty("requires")[0].GetString());
        Assert.False(cs.TryGetProperty("description", out _));
        Assert.False(cs.TryGetProperty("unavailable_tools", out _));

        var detail = await host.CallOkAsync("macro_list", new { name = "run-addin-command" });
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("description").GetString()));
        Assert.Contains(detail.GetProperty("params").EnumerateArray(), p => p.GetProperty("name").GetString() == "text" && p.GetProperty("required").GetBoolean());
        var steps = detail.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(new[] { "ribbon_list_items", "command_post", "ui_wait", "revit_list_dialogs" }, steps.Select(s => s.GetProperty("tool").GetString()));
        Assert.Equal("Interact", steps[1].GetProperty("permission").GetString());

        Assert.Equal(GatewayErrorCodes.NotFound, (await host.CallAsync("macro_list", new { name = "nope" })).ErrorCode);
    }
}

/// <summary>Registry + policy + audit + the real macro tools, with fake Bridge/UI tools named like the real ones.</summary>
internal sealed class MacroHost : IAsyncDisposable
{
    private readonly Dictionary<string, FakeTool> _fakes;

    public MacroHost(int? userRevitPid = null, string? without = null)
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var probe = new RevitAgent.Tests.Bridge.FakeRevitProbe();
        if (userRevitPid is { } pid)
        {
            probe.Add(pid);
        }

        _fakes = Build().Where(f => f.Name != without).ToDictionary(f => f.Name);
        var sc = new ServiceCollection();
        sc.AddSingleton(paths);
        sc.AddSingleton<AuditLog>();
        sc.AddSingleton<GatewayPolicy>();
        sc.AddSingleton(new ActionGuard(new ActionGrantStore(), probe, new NoOwned(), new SystemWindowProcessResolver()));
        sc.AddSingleton<ToolRegistry>();
        foreach (var fake in _fakes.Values)
        {
            sc.AddSingleton<IGatewayTool>(fake);
        }

        sc.AddSingleton<MacroLibrary>();
        sc.AddSingleton<MacroListTool>();
        sc.AddSingleton<IGatewayTool>(sp => sp.GetRequiredService<MacroListTool>());
        sc.AddSingleton<MacroRunTool>();
        sc.AddSingleton<IGatewayTool>(sp => sp.GetRequiredService<MacroRunTool>());
        Services = sc.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Registry = Services.GetRequiredService<ToolRegistry>();
    }

    public ServiceProvider Services { get; }

    public ToolRegistry Registry { get; }

    public GatewayException? PostError { get; set; }

    public int Count(string tool) => _fakes.TryGetValue(tool, out var f) ? f.Calls.Count : 0;

    public JsonElement Last(string tool) => _fakes[tool].Calls[^1];

    public Task<ToolResult> CallAsync(string tool, object? args = null, string session = "s1") =>
        Registry.InvokeAsync(tool, args is null ? null : JsonSerializer.SerializeToElement(args), session, CancellationToken.None);

    public async Task<JsonElement> CallOkAsync(string tool, object? args = null, string session = "s1")
    {
        var r = await CallAsync(tool, args, session);
        Assert.False(r.IsError, $"{tool} failed: {((TextContent)r.Content[0]).Text}");
        return r.Structured!.Value;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    private IEnumerable<FakeTool> Build()
    {
        var items = new[]
        {
            new { tab = "Acme", panel = "Tools", id = "Export", text = "Export sheets", kind = "RibbonButton", command_id = "CustomCtrl_%Acme%Tools%Export", postable = true },
            new { tab = "Acme", panel = "Tools", id = "Import", text = "Import Acme data", kind = "RibbonButton", command_id = "CustomCtrl_%Acme%Tools%Import", postable = true },
        };
        yield return new FakeTool("ribbon_list_items", ToolPermission.Read, c =>
        {
            var text = c.GetString("text_contains") ?? string.Empty;
            var hits = items.Where(i => i.text.Contains(text, StringComparison.OrdinalIgnoreCase) || i.tab.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new { tabs = Array.Empty<object>(), total = hits.Length, offset = 0, returned = hits.Length, truncated = false, items = hits };
        }, () => Schema.Object().Pid().String("tab", "t").String("panel", "p").String("text_contains", "x").Boolean("include_hidden", "h").Boolean("postable_only", "o").Integer("offset", "o").Integer("limit", "l"));
        yield return new FakeTool("command_post", ToolPermission.Interact, c =>
        {
            if (PostError is { } e)
            {
                throw e;
            }

            return new { posted = true, command_id = c.RequireString("command"), resolved_as = "command_id", revit_activated = true };
        }, () => Schema.Object().Pid().String("command", "c", required: true));
        yield return new FakeTool("ui_wait", ToolPermission.Read, c => new { state = "appears", satisfied = true, waited_ms = 120, window = new { hwnd = "0x1A2B", title = c.GetString("window") } },
            () => Schema.Object().Pid(required: false).String("window", "w").Number("timeout_s", "t").String("state", "s"));
        yield return new FakeTool("revit_list_dialogs", ToolPermission.Read, c => new { pid = c.GetInt("pid"), count = 1, truncated = false, state = "blocked", dialogs = new[] { new { hwnd = "0x1A2B", title = "Acme Export", category = "unknown" } } },
            () => Schema.Object().Pid(required: false).Boolean("include_screenshot", "s").Integer("limit", "l"));
        yield return new FakeTool("schedule_create", ToolPermission.Write, c => new
        {
            view_id = 777,
            name = c.GetString("name") ?? "Room Schedule",
            category = c.GetString("category"),
            fields = c.GetStringList("fields").Select(f => new { name = f, heading = f }).ToArray(),
            sort = c.GetStringList("sort_by").Select(f => new { field = f, descending = c.GetBool("sort_descending") }).ToArray(),
            activated = true,
        }, () => Schema.Object().Pid().String("document", "d").String("category", "c").StringArray("fields", "f").StringArray("sort_by", "s").Boolean("sort_descending", "d").String("name", "n").Boolean("activate", "a"));
        yield return new FakeTool("schedule_read", ToolPermission.Read, c => new
        {
            id = c.GetLong("view_id"),
            name = "Room Schedule",
            fields = new[] { new { name = "Level", heading = "Level", hidden = false } },
            body = new
            {
                columns = 3,
                header_rows = 2,
                header_cells = new[] { new[] { "Level", "Number", "Name" }, new[] { string.Empty, string.Empty, string.Empty } },
                data_rows = 2,
                offset = c.GetInt("offset"),
                returned_rows = 2,
                truncated = false,
                rows = new[] { new[] { "L1", "101", "Office" }, new[] { "L1", "102", "Hall" } },
            },
        }, () => Schema.Object().Pid().String("document", "d").Integer("view_id", "v").String("name", "n").Integer("offset", "o").Integer("limit", "l"));
    }
}
