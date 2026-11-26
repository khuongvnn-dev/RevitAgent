using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Placeholder substitution for scenario values: <c>{run_dir}</c>, <c>{pid}</c>, <c>{model}</c> (the opened sandbox copy),
/// <c>{task_dir}</c> (the scenario folder) and <c>{dataset}</c> (the <c>dataset</c> run argument).
/// Unknown placeholders are left untouched (scenario_validate warns about them).
/// Relative input paths: <c>input\X</c> → <c>&lt;task_dir&gt;\input\X</c>; <c>{dataset}\X</c> →
/// <c>&lt;task_dir&gt;\input\X</c> when the scenario overrides it, else <c>&lt;dataset&gt;\X</c>.
/// </summary>
public sealed class ScenarioVariables
{
    public static readonly IReadOnlyList<string> Known = new[] { "run_dir", "pid", "model", "task_dir", "dataset" };

    private static readonly Regex Placeholder = new(@"\{([a-z_]+)\}", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ScenarioVariables(string taskDir) => Set("task_dir", taskDir);

    public string TaskDir => _values["task_dir"];

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public ScenarioVariables Set(string name, string? value)
    {
        if (value is null)
        {
            _values.Remove(name);
        }
        else
        {
            _values[name] = value;
        }

        return this;
    }

    /// <summary>Placeholders used in <paramref name="text"/> that are not in <see cref="Known"/>.</summary>
    public static IEnumerable<string> UnknownPlaceholders(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups[1].Value).Where(n => !Known.Contains(n)).Distinct();

    /// <summary>Known placeholders used in <paramref name="text"/>.</summary>
    public static IEnumerable<string> UsedPlaceholders(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups[1].Value).Where(n => Known.Contains(n)).Distinct();

    /// <summary>Replaces known placeholders; throws <see cref="InvalidOperationException"/> for a known one without a value.</summary>
    public string Expand(string text) =>
        Placeholder.Replace(text, m =>
        {
            var n = m.Groups[1].Value;
            if (!Known.Contains(n))
            {
                return m.Value;
            }

            return _values.TryGetValue(n, out var v)
                ? v
                : throw new InvalidOperationException(n switch
                {
                    "dataset" => "The scenario uses {dataset} but no 'dataset' folder was given to scenario_run.",
                    "pid" => "The scenario uses {pid} but no Revit process is attached (pass pid or launch: true).",
                    "model" => "The scenario uses {model} but has no 'model' (nothing was opened).",
                    _ => $"Variable {{{n}}} has no value yet.",
                });
        });

    /// <summary>Expands a value and resolves relative input paths against the scenario folder.</summary>
    public string ExpandPath(string value)
    {
        var raw = value.Trim();
        var datasetRel = DatasetRelative(raw);
        var s = Expand(raw);
        if (datasetRel is not null)
        {
            var taskInput = Path.Combine(TaskDir, "input", datasetRel);
            if (File.Exists(taskInput) || Directory.Exists(taskInput))
            {
                return taskInput;
            }
        }

        if (IsInputRelative(s))
        {
            return Path.GetFullPath(Path.Combine(TaskDir, s));
        }

        return s;
    }

    /// <summary>Resolves a scenario file reference (model, ui script): absolute stays, relative is under the scenario folder.</summary>
    public string ResolveFile(string value)
    {
        var s = ExpandPath(value);
        return Path.IsPathRooted(s) ? Path.GetFullPath(s) : Path.GetFullPath(Path.Combine(TaskDir, s));
    }

    /// <summary>Deep copy of <paramref name="args"/> with every string expanded (relative input paths included).</summary>
    public JsonObject ExpandArgs(JsonObject args) => (JsonObject)ExpandNode(args)!;

    private JsonNode? ExpandNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, ExpandNode(p.Value)))),
        JsonArray a => new JsonArray(a.Select(ExpandNode).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(ExpandPath(s)),
        _ => node.DeepClone(),
    };

    private static bool IsInputRelative(string s) =>
        s.StartsWith(@"input\", StringComparison.OrdinalIgnoreCase) || s.StartsWith("input/", StringComparison.OrdinalIgnoreCase);

    private static string? DatasetRelative(string raw)
    {
        foreach (var prefix in new[] { @"{dataset}\", "{dataset}/" })
        {
            if (raw.StartsWith(prefix, StringComparison.Ordinal))
            {
                return raw[prefix.Length..];
            }
        }

        return null;
    }
}
