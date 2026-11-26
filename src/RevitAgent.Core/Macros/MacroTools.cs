using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Macros;

/// <summary>macro_list — the verified macros (compact list, or one macro in full).</summary>
public sealed class MacroListTool : GatewayTool
{
    private readonly MacroLibrary _library;
    private readonly IServiceProvider _services;

    public MacroListTool(MacroLibrary library, IServiceProvider services)
    {
        _library = library;
        _services = services;
    }

    public override string Name => "macro_list";

    public override string Description =>
        "Verified macros: fixed chains of gateway tools for common tasks (e.g. create-schedule, run-addin-command). " +
        "For a common task call this first, then macro_run. No args: [{name, summary, requires, params}] where params reads " +
        "'pid:int*, fields:string[]' (* = required). With name: that macro in full (description, params, steps and their tools).";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("name", "Macro name: return its full detail.");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var registry = _services.GetService(typeof(ToolRegistry)) as ToolRegistry;
        var name = call.GetString("name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var macro = _library.Find(name) ?? throw MacroRunTool.UnknownMacro(name, _library);
            return Task.FromResult(ToolResult.Json(Detail(macro, registry)));
        }

        var list = new JsonArray();
        foreach (var m in _library.Macros)
        {
            var item = new JsonObject
            {
                ["name"] = m.Name,
                ["summary"] = m.Summary,
                ["requires"] = new JsonArray(m.Requires.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                ["params"] = ParamLine(m),
            };
            var missing = MissingTools(m, registry);
            if (missing.Count > 0)
            {
                item["unavailable_tools"] = new JsonArray(missing.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
            }

            list.Add(item);
        }

        var result = new JsonObject { ["count"] = list.Count, ["macros"] = list };
        if (_library.Issues.Count > 0)
        {
            result["load_errors"] = new JsonArray(_library.Issues.Select(i => (JsonNode)JsonValue.Create(i.ToString())!).ToArray());
        }

        return Task.FromResult(ToolResult.Json(result));
    }

    /// <summary>"pid:int*, category:string*, fields:string[]*, limit:int=100".</summary>
    public static string ParamLine(MacroDefinition m) =>
        string.Join(", ", m.Params.Select(p =>
            $"{p.Name}:{MacroTypes.Short(p.Type, p.ItemType)}{(p.Required ? "*" : string.Empty)}" +
            (p.Default is JsonValue d ? "=" + (d.GetValueKind() == JsonValueKind.String ? d.GetValue<string>() : d.ToJsonString()) : string.Empty)));

    internal static IReadOnlyList<string> MissingTools(MacroDefinition m, ToolRegistry? registry) =>
        registry is null
            ? Array.Empty<string>()
            : m.Steps.Select(s => s.Tool).Distinct(StringComparer.Ordinal).Where(t => !registry.TryGet(t, out _)).ToArray();

    private static JsonObject Detail(MacroDefinition m, ToolRegistry? registry)
    {
        var parameters = new JsonArray();
        foreach (var p in m.Params)
        {
            var o = new JsonObject { ["name"] = p.Name, ["type"] = MacroTypes.Short(p.Type, p.ItemType) };
            if (p.Required)
            {
                o["required"] = true;
            }

            if (p.Default is not null)
            {
                o["default"] = p.Default.DeepClone();
            }

            if (!string.IsNullOrWhiteSpace(p.Description))
            {
                o["description"] = p.Description;
            }

            parameters.Add(o);
        }

        var steps = new JsonArray();
        foreach (var s in m.Steps)
        {
            var o = new JsonObject { ["id"] = s.Id, ["tool"] = s.Tool };
            o["permission"] = registry is not null && registry.TryGet(s.Tool, out var tool) ? tool.Permission.ToString() : "unavailable";
            if (s.When is not null)
            {
                o["when"] = s.When;
            }

            steps.Add(o);
        }

        var detail = new JsonObject
        {
            ["name"] = m.Name,
            ["summary"] = m.Summary,
        };
        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            detail["description"] = m.Description;
        }

        detail["requires"] = new JsonArray(m.Requires.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        detail["params"] = parameters;
        detail["steps"] = steps;
        if (m.Returns is JsonObject r)
        {
            detail["returns"] = new JsonArray(r.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
        }

        return detail;
    }
}

/// <summary>
/// macro_run — runs one macro: every step is an ordinary <see cref="ToolRegistry.InvokeAsync(ToolCall, CancellationToken)"/>
/// call with the caller's session, so policy, permission ceiling, action grants, sandbox checks, per-tool timeouts and
/// audit apply to each step exactly as if the agent had called it. Declared Write (the highest permission among the
/// built-in macros' steps) so that clients never file it with auto-approved read-only tools.
/// </summary>
public sealed class MacroRunTool : GatewayTool
{
    private readonly MacroLibrary _library;
    private readonly IServiceProvider _services;

    public MacroRunTool(MacroLibrary library, IServiceProvider services)
    {
        _library = library;
        _services = services;
    }

    public override string Name => "macro_run";

    public override string Description =>
        "Run a macro from macro_list. Each step is a normal tool call (same policy, permission, grants, sandbox, timeouts, audit), " +
        "so it changes Revit only where a step does (e.g. create-schedule creates a view). Stops at the first failed step: " +
        "error {code, message, details.failed_step}. Returns {macro, ok, result, steps:[{id, tool, ok, ms, skipped?}]}. Up to 15 min.";

    /// <summary>Write, like its most powerful step (schedule_create); each step is still checked on its own.</summary>
    public override ToolPermission Permission => ToolPermission.Write;

    /// <summary>Whole-macro budget; each step keeps its own tool timeout inside it.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(15);

    protected override Schema BuildSchema() => Schema.Object()
        .String("name", "Macro name.", required: true)
        .AnyObject("args", "Macro params, e.g. {\"pid\": 1234}.")
        .Boolean("verbose", "Add each step's raw output (large).", defaultValue: false);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var name = call.RequireString("name").Trim();
        var macro = _library.Find(name) ?? throw UnknownMacro(name, _library);
        var verbose = call.GetBool("verbose");
        var registry = (ToolRegistry?)_services.GetService(typeof(ToolRegistry))
            ?? throw new GatewayException(GatewayErrorCodes.Internal, "Tool registry is not available.");

        var missing = MacroListTool.MissingTools(macro, registry);
        if (missing.Count > 0)
        {
            throw GatewayException.Dependency(
                $"Macro '{macro.Name}' needs {string.Join(", ", missing)}, which this gateway does not have (update RevitAgent / the Bridge tool catalog). Nothing was run.");
        }

        var scope = BindParams(macro, call);
        var steps = new JsonArray();
        foreach (var step in macro.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new JsonObject { ["id"] = step.Id, ["tool"] = step.Tool };
            steps.Add(entry);
            if (step.When is not null && !MacroTemplate.IsTruthy(MacroTemplate.TryResolve(step.When, scope, out var cond), cond))
            {
                entry["ok"] = true;
                entry["ms"] = 0;
                entry["skipped"] = true;
                continue;
            }

            MacroTemplate.TryResolve(step.Args, scope, out var args);
            var sw = Stopwatch.StartNew();
            var r = await registry.InvokeAsync(new ToolCall(step.Tool, JsonSerializer.SerializeToElement(args), call.SessionId), cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var output = OutputOf(r);
            entry["ok"] = !r.IsError;
            entry["ms"] = sw.ElapsedMilliseconds;
            if (verbose)
            {
                entry["output"] = output?.DeepClone();
            }

            if (r.IsError)
            {
                var (message, details) = ErrorOf(r, output);
                throw Failure(macro, step, r.ErrorCode ?? GatewayErrorCodes.Internal, message, steps, details);
            }

            foreach (var expect in step.Expect)
            {
                if (Check(expect, output) is { } problem)
                {
                    entry["ok"] = false;
                    throw Failure(macro, step, MacroErrorCodes.ExpectationFailed, problem.Message, steps, problem.Details);
                }
            }

            scope.Set(step.Id, output);
        }

        var result = new JsonObject { ["macro"] = macro.Name, ["ok"] = true };
        if (macro.Returns is not null && MacroTemplate.TryResolve(macro.Returns, scope, out var resolved))
        {
            result["result"] = resolved;
        }

        result["steps"] = steps;
        return ToolResult.Json(result);
    }

    internal static GatewayException UnknownMacro(string name, MacroLibrary library) =>
        GatewayException.NotFound($"Unknown macro '{name}'. Available: {string.Join(", ", library.Names)} (macro_list).");

    /// <summary>Caller args → scope: unknown names and missing required params are VALIDATION_FAILED; defaults fill the rest.</summary>
    internal static MacroScope BindParams(MacroDefinition macro, ToolCall call)
    {
        JsonObject given = call.Raw("args") switch
        {
            null => new JsonObject(),
            { ValueKind: JsonValueKind.Object } o => JsonNode.Parse(o.GetRawText())!.AsObject(),
            { ValueKind: JsonValueKind.String } s when string.IsNullOrWhiteSpace(s.GetString()) => new JsonObject(),
            { ValueKind: JsonValueKind.String } s => ParseArgsString(s.GetString()!),
            _ => throw GatewayException.Validation("'args' must be a JSON object of macro params."),
        };

        var unknown = given.Select(p => p.Key).Where(k => macro.Param(k) is null).ToArray();
        if (unknown.Length > 0)
        {
            throw GatewayException.Validation(
                $"Macro '{macro.Name}' has no param {string.Join(", ", unknown.Select(u => $"'{u}'"))}. Params: {MacroListTool.ParamLine(macro)}.");
        }

        var scope = new MacroScope();
        var missing = new List<string>();
        foreach (var p in macro.Params)
        {
            if (given[p.Name] is { } value && value.GetValueKind() != JsonValueKind.Null)
            {
                scope.Set(p.Name, MacroLoader.Coerce(p, value));
            }
            else if (p.Default is not null)
            {
                scope.Set(p.Name, p.Default.DeepClone());
            }
            else if (p.Required)
            {
                missing.Add(p.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw GatewayException.Validation(
                $"Macro '{macro.Name}' needs {string.Join(", ", missing.Select(m => $"'{m}'"))}. Params: {MacroListTool.ParamLine(macro)}.");
        }

        return scope;
    }

    private static JsonObject ParseArgsString(string s)
    {
        try
        {
            return JsonNode.Parse(s) as JsonObject ?? throw GatewayException.Validation("'args' string must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw GatewayException.Validation($"'args' is not valid JSON: {ex.Message}");
        }
    }

    private static JsonNode? OutputOf(ToolResult r)
    {
        if (r.Structured is { } s)
        {
            return JsonNode.Parse(s.GetRawText());
        }

        var text = r.Content.OfType<TextContent>().FirstOrDefault()?.Text;
        return text is null ? null : JsonValue.Create(text);
    }

    private static (string Message, JsonNode? Details) ErrorOf(ToolResult r, JsonNode? output)
    {
        if (output is JsonObject o)
        {
            var message = o["message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : null;
            return (message ?? "the step failed", o["details"]?.DeepClone());
        }

        return (output is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "the step failed", null);
    }

    private static (string Message, JsonNode? Details)? Check(MacroExpect expect, JsonNode? output)
    {
        var found = MacroPath.TryGet(output, expect.Segments, out var value);
        var path = expect.Path.Length == 0 ? "the output" : $"'{expect.Path}'";
        var hint = string.IsNullOrWhiteSpace(expect.Message) ? string.Empty : " " + expect.Message;
        if (expect.NotEmpty && (!found || MacroTemplate.IsEmpty(value)))
        {
            return ($"Expected {path} to be non-empty, but it is {(found ? "empty" : "missing")}.{hint}", new JsonObject { ["path"] = expect.Path, ["not_empty"] = true });
        }

        if (expect.Count is { } n)
        {
            int? actual = value switch { JsonArray a => a.Count, JsonObject o => o.Count, _ => null };
            if (actual != n)
            {
                var details = new JsonObject { ["path"] = expect.Path, ["count"] = n, ["actual"] = actual };
                if (value is JsonArray a && a.Count > 0)
                {
                    var sample = new JsonArray(a.Take(5).Select(x => x?.DeepClone()).ToArray());
                    if (sample.ToJsonString().Length <= 4000)
                    {
                        details["sample"] = sample;
                    }
                }

                var got = actual is null ? (found ? "not a list" : "missing") : $"{actual}";
                return ($"Expected {path} to have {n} item(s), got {got}.{hint}", details);
            }
        }

        return null;
    }

    private static GatewayException Failure(MacroDefinition macro, MacroStep step, string code, string message, JsonArray steps, JsonNode? stepDetails)
    {
        var details = new JsonObject
        {
            ["macro"] = macro.Name,
            ["ok"] = false,
            ["failed_step"] = step.Id,
            ["error_code"] = code,
            ["steps"] = steps.DeepClone(),
        };
        if (stepDetails is not null)
        {
            details["step_details"] = stepDetails;
        }

        return new GatewayException(code, $"Macro '{macro.Name}' stopped at step '{step.Id}' ({step.Tool}): {message}", details);
    }
}
