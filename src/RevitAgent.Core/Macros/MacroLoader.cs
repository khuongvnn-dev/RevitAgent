using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Tools;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RevitAgent.Core.Macros;

/// <summary>Macros read from a set of YAML files, plus the problems that kept some of them out.</summary>
public sealed class MacroLoadResult
{
    public List<MacroDefinition> Macros { get; } = new();

    public List<MacroIssue> Issues { get; } = new();
}

/// <summary>
/// Reads macro YAML (one macro per file) and validates it: kebab-case unique names, summary ≤ 120 chars, typed params
/// (a required param has no default), steps with unique ids, and every <c>${...}</c> template pointing at a declared
/// param or an earlier step id (any step id in <c>returns</c>). Plain YAML scalars become JSON numbers/booleans like
/// in scenarios (<see cref="ScenarioLoader.ToJson"/>). Whether each step tool exists is checked at run time
/// (the registry is not known here) and by a unit test.
/// </summary>
public static class MacroLoader
{
    public const int MaxSummaryChars = 120;

    public static readonly IReadOnlySet<string> KnownRequirements = new HashSet<string>(StringComparer.Ordinal) { "bridge", "ui" };

    private static readonly Regex MacroName = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex Identifier = new("^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex ToolName = new("^[a-z][a-z0-9_]{2,63}$", RegexOptions.Compiled);

    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal) { "name", "summary", "description", "requires", "params", "steps", "returns" };
    private static readonly HashSet<string> ParamKeys = new(StringComparer.Ordinal) { "type", "required", "default", "description", "items" };
    private static readonly HashSet<string> StepKeys = new(StringComparer.Ordinal) { "id", "tool", "args", "when", "expect" };
    private static readonly HashSet<string> ExpectKeys = new(StringComparer.Ordinal) { "path", "not_empty", "count", "message" };

    /// <summary>Loads several files; a name used twice keeps the first one.</summary>
    public static MacroLoadResult Load(IEnumerable<(string Source, string Yaml)> files)
    {
        var result = new MacroLoadResult();
        foreach (var (source, yaml) in files)
        {
            var (macro, errors) = Parse(yaml, source);
            if (macro is null)
            {
                result.Issues.AddRange(errors.Select(e => new MacroIssue(source, null, e)));
                continue;
            }

            if (errors.Count > 0)
            {
                result.Issues.AddRange(errors.Select(e => new MacroIssue(source, macro.Name, e)));
                continue;
            }

            if (result.Macros.Any(m => m.Name == macro.Name))
            {
                result.Issues.Add(new MacroIssue(source, macro.Name, $"Duplicate macro name '{macro.Name}' (already loaded from {result.Macros.First(m => m.Name == macro.Name).Source})."));
                continue;
            }

            result.Macros.Add(macro);
        }

        return result;
    }

    /// <summary>Parses one macro. Returns the definition (null when unusable) and every validation error.</summary>
    public static (MacroDefinition? Macro, List<string> Errors) Parse(string yaml, string source = "macro.yaml")
    {
        var errors = new List<string>();
        JsonObject root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || ScenarioLoader.ToJson(stream.Documents[0].RootNode) is not JsonObject o)
            {
                errors.Add("A macro file must be a YAML mapping (name:, summary:, steps:, ...).");
                return (null, errors);
            }

            root = o;
        }
        catch (YamlException ex)
        {
            errors.Add($"YAML parse error at line {ex.Start.Line}, column {ex.Start.Column}: {ex.InnerException?.Message ?? ex.Message}");
            return (null, errors);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"YAML error: {ex.Message}");
            return (null, errors);
        }

        foreach (var key in root.Select(p => p.Key).Where(k => !RootKeys.Contains(k)))
        {
            errors.Add($"Unknown key '{key}'. Allowed: {string.Join(", ", RootKeys)}.");
        }

        var name = Text(root["name"]);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Missing 'name' (kebab-case, e.g. create-schedule).");
            return (null, errors);
        }

        if (!MacroName.IsMatch(name) || name.Length > 64)
        {
            errors.Add($"Name '{name}' must be kebab-case (lowercase letters, digits and single dashes, ≤ 64 chars).");
        }

        var summary = Text(root["summary"])?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            errors.Add("Missing 'summary' (one line shown by macro_list).");
        }
        else if (summary.Length > MaxSummaryChars)
        {
            errors.Add($"'summary' is {summary.Length} chars; keep it ≤ {MaxSummaryChars} (put details in 'description').");
        }

        var requires = ReadRequires(root["requires"], errors);
        var parameters = ReadParams(root["params"], errors);
        var steps = ReadSteps(root["steps"], parameters, errors);
        var returns = root["returns"]?.DeepClone();
        CheckTemplates("returns", returns, parameters, steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal), errors);

        var macro = new MacroDefinition
        {
            Name = name,
            Summary = summary ?? string.Empty,
            Description = Text(root["description"])?.Trim(),
            Requires = requires,
            Params = parameters,
            Steps = steps,
            Returns = returns,
            Source = source,
        };
        return (macro, errors);
    }

    /// <summary>
    /// Coerces a caller value (or a default) to the parameter type: numeric strings → numbers, "true"/"false" → bool,
    /// numbers/bools → text for string params, a single value → one-item array. Throws VALIDATION_FAILED otherwise.
    /// </summary>
    public static JsonNode Coerce(MacroParam p, JsonNode value) => Coerce(p.Name, p.Type, p.ItemType, value);

    private static JsonNode Coerce(string name, string type, string itemType, JsonNode value)
    {
        var kind = value.GetValueKind();
        switch (type)
        {
            case MacroTypes.Array:
                return value is JsonArray a
                    ? new JsonArray(a.Select(x => x is null ? null : Coerce(name, itemType, MacroTypes.String, x)).ToArray())
                    : value is JsonObject
                        ? throw Bad(name, type, itemType, kind)
                        : new JsonArray(Coerce(name, itemType, MacroTypes.String, value));
            case MacroTypes.String when kind is JsonValueKind.String:
                return value.DeepClone();
            case MacroTypes.String when kind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return JsonValue.Create(value.ToJsonString());
            case MacroTypes.Integer:
                var integerText = kind switch { JsonValueKind.Number => value.ToJsonString(), JsonValueKind.String => value.GetValue<string>().Trim(), _ => null };
                if (long.TryParse(integerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    return JsonValue.Create(l);
                }

                if (double.TryParse(integerText, NumberStyles.Float, CultureInfo.InvariantCulture, out var whole) && whole == Math.Floor(whole) && Math.Abs(whole) < 9e15)
                {
                    return JsonValue.Create((long)whole);
                }

                break;
            case MacroTypes.Number:
                var numberText = kind switch { JsonValueKind.Number => value.ToJsonString(), JsonValueKind.String => value.GetValue<string>().Trim(), _ => null };
                if (double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return kind == JsonValueKind.Number ? value.DeepClone() : JsonValue.Create(d);
                }

                break;
            case MacroTypes.Boolean:
                if (kind is JsonValueKind.True or JsonValueKind.False)
                {
                    return JsonValue.Create(kind == JsonValueKind.True);
                }

                if (kind == JsonValueKind.String && bool.TryParse(value.GetValue<string>().Trim(), out var b))
                {
                    return JsonValue.Create(b);
                }

                break;
        }

        throw Bad(name, type, itemType, kind);
    }

    private static GatewayException Bad(string name, string type, string itemType, JsonValueKind kind) =>
        GatewayException.Validation($"Macro parameter '{name}' must be {(type == MacroTypes.Array ? $"an array of {itemType}" : type)} (got {kind.ToString().ToLowerInvariant()}).");

    private static IReadOnlyList<string> ReadRequires(JsonNode? node, List<string> errors)
    {
        if (node is null)
        {
            return Array.Empty<string>();
        }

        var items = node is JsonArray a ? a.Select(Text).ToList() : new List<string?> { Text(node) };
        var list = new List<string>();
        foreach (var r in items)
        {
            if (r is null || !KnownRequirements.Contains(r))
            {
                errors.Add($"'requires' value '{r}' is not known. Use: {string.Join(", ", KnownRequirements)}.");
            }
            else if (!list.Contains(r))
            {
                list.Add(r);
            }
        }

        return list;
    }

    private static List<MacroParam> ReadParams(JsonNode? node, List<string> errors)
    {
        var list = new List<MacroParam>();
        if (node is null)
        {
            return list;
        }

        if (node is not JsonObject map)
        {
            errors.Add("'params' must be a mapping: name → {type, required, default, description}.");
            return list;
        }

        foreach (var (name, value) in map)
        {
            if (!Identifier.IsMatch(name))
            {
                errors.Add($"Param name '{name}' must be lower snake_case.");
                continue;
            }

            if (value is not JsonObject spec)
            {
                errors.Add($"Param '{name}' must be a mapping {{type, required, default, description}}.");
                continue;
            }

            foreach (var key in spec.Select(p => p.Key).Where(k => !ParamKeys.Contains(k)))
            {
                errors.Add($"Param '{name}': unknown key '{key}'. Allowed: {string.Join(", ", ParamKeys)}.");
            }

            var type = Text(spec["type"]) ?? string.Empty;
            if (!MacroTypes.All.Contains(type))
            {
                errors.Add($"Param '{name}': type must be one of {string.Join(", ", MacroTypes.All)} (got '{type}').");
                continue;
            }

            var itemType = Text(spec["items"]) ?? MacroTypes.String;
            if (spec["items"] is not null && (type != MacroTypes.Array || !MacroTypes.ItemTypes.Contains(itemType)))
            {
                errors.Add($"Param '{name}': 'items' is only for type array and must be one of {string.Join(", ", MacroTypes.ItemTypes)}.");
                itemType = MacroTypes.String;
            }

            var required = false;
            if (spec["required"] is { } req)
            {
                var kind = req.GetValueKind();
                if (kind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add($"Param '{name}': 'required' must be true or false.");
                }

                required = kind == JsonValueKind.True;
            }

            JsonNode? defaultValue = null;
            if (spec["default"] is { } def)
            {
                if (required)
                {
                    errors.Add($"Param '{name}' is required and also has a default; drop one of them.");
                }

                try
                {
                    defaultValue = Coerce(name, type, itemType, def);
                }
                catch (GatewayException ex)
                {
                    errors.Add($"Param '{name}': default does not match its type ({ex.Message}).");
                }
            }

            list.Add(new MacroParam(name, type, required, defaultValue, Text(spec["description"])?.Trim(), itemType));
        }

        return list;
    }

    private static List<MacroStep> ReadSteps(JsonNode? node, List<MacroParam> parameters, List<string> errors)
    {
        var steps = new List<MacroStep>();
        if (node is not JsonArray seq || seq.Count == 0)
        {
            errors.Add("'steps' must be a non-empty list of {id, tool, args, when?, expect?}.");
            return steps;
        }

        var paramNames = parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var earlier = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < seq.Count; i++)
        {
            var where = $"steps[{i}]";
            if (seq[i] is not JsonObject s)
            {
                errors.Add($"{where} must be a mapping {{id, tool, args}}.");
                continue;
            }

            foreach (var key in s.Select(p => p.Key).Where(k => !StepKeys.Contains(k)))
            {
                errors.Add($"{where}: unknown key '{key}'. Allowed: {string.Join(", ", StepKeys)}.");
            }

            var id = Text(s["id"]) ?? string.Empty;
            if (!Identifier.IsMatch(id))
            {
                errors.Add($"{where}: 'id' must be lower snake_case (got '{id}').");
                continue;
            }

            where = $"step '{id}'";
            if (earlier.Contains(id))
            {
                errors.Add($"{where}: duplicate step id.");
            }

            if (paramNames.Contains(id))
            {
                errors.Add($"{where}: the id is also a param name; templates would be ambiguous.");
            }

            var tool = Text(s["tool"]) ?? string.Empty;
            if (!ToolName.IsMatch(tool))
            {
                errors.Add($"{where}: 'tool' must be a gateway tool name (got '{tool}').");
            }
            else if (tool.StartsWith("macro_", StringComparison.Ordinal))
            {
                errors.Add($"{where}: a macro step cannot call {tool} (no nested macros).");
            }

            var args = new JsonObject();
            if (s["args"] is JsonObject a)
            {
                args = (JsonObject)a.DeepClone();
            }
            else if (s["args"] is not null)
            {
                errors.Add($"{where}: 'args' must be a mapping.");
            }

            CheckTemplates($"{where} args", args, parameters, earlier, errors);

            string? when = null;
            if (s["when"] is { } w)
            {
                when = w.GetValueKind() == JsonValueKind.String ? w.GetValue<string>() : null;
                if (when is null)
                {
                    errors.Add($"{where}: 'when' must be a template string like ${{param}}.");
                }
                else
                {
                    CheckTemplates($"{where} when", JsonValue.Create(when), parameters, earlier, errors);
                }
            }

            var expect = ReadExpect(s["expect"], where, errors);
            steps.Add(new MacroStep(id, tool, args, when, expect));
            earlier.Add(id);
        }

        return steps;
    }

    private static List<MacroExpect> ReadExpect(JsonNode? node, string where, List<string> errors)
    {
        var list = new List<MacroExpect>();
        if (node is null)
        {
            return list;
        }

        var items = node is JsonArray a ? a.ToList() : new List<JsonNode?> { node };
        foreach (var item in items)
        {
            if (item is not JsonObject e)
            {
                errors.Add($"{where}: each 'expect' must be a mapping {{path, not_empty: true}} or {{path, count: N}}.");
                continue;
            }

            foreach (var key in e.Select(p => p.Key).Where(k => !ExpectKeys.Contains(k)))
            {
                errors.Add($"{where}: unknown expect key '{key}'. Allowed: {string.Join(", ", ExpectKeys)}.");
            }

            var path = Text(e["path"]) ?? string.Empty;
            if (!MacroPath.TryParse(path, out var segments, out var pathError))
            {
                errors.Add($"{where}: expect {pathError}.");
                continue;
            }

            var notEmpty = e["not_empty"]?.GetValueKind() == JsonValueKind.True;
            int? count = null;
            if (e["count"] is { } c)
            {
                if (c.GetValueKind() == JsonValueKind.Number && int.TryParse(c.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    count = n;
                }
                else
                {
                    errors.Add($"{where}: expect count must be a whole number ≥ 0.");
                }
            }

            if (!notEmpty && count is null)
            {
                errors.Add($"{where}: an expect needs not_empty: true or count: N.");
            }

            list.Add(new MacroExpect(path, segments, notEmpty, count, Text(e["message"])?.Trim()));
        }

        return list;
    }

    private static void CheckTemplates(string where, JsonNode? node, IReadOnlyList<MacroParam> parameters, IReadOnlySet<string> steps, List<string> errors)
    {
        foreach (var text in MacroTemplate.Strings(node))
        {
            if (MacroTemplate.HasUnclosed(text))
            {
                errors.Add($"{where}: unclosed template in '{text}'.");
            }

            foreach (var expression in MacroTemplate.Expressions(text))
            {
                if (!MacroTemplate.TryParseExpression(expression, out var root, out _, out var error))
                {
                    errors.Add($"{where}: {error}.");
                }
                else if (!parameters.Any(p => p.Name == root) && !steps.Contains(root))
                {
                    errors.Add($"{where}: '${{{expression}}}' refers to '{root}', which is neither a declared param nor an earlier step id.");
                }
            }
        }
    }

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
}
