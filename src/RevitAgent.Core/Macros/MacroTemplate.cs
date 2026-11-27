using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Macros;

/// <summary>
/// Dot paths into JSON: <c>items[0].command_id</c>, <c>body.rows</c>. Segments are property names (string) or array
/// indexes (int). An empty path is the whole value.
/// </summary>
public static class MacroPath
{
    public static bool TryParse(string text, out List<object> segments, out string? error)
    {
        segments = new List<object>();
        error = null;
        var s = (text ?? string.Empty).Trim();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0 || !int.TryParse(s.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    error = $"bad index in path '{s}' (use [0], [1], ...)";
                    return false;
                }

                segments.Add(index);
                i = close + 1;
            }
            else
            {
                var start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[')
                {
                    i++;
                }

                if (i == start)
                {
                    error = $"empty segment in path '{s}'";
                    return false;
                }

                segments.Add(s[start..i]);
            }

            if (i < s.Length && s[i] == '.')
            {
                i++;
                if (i == s.Length)
                {
                    error = $"path '{s}' ends with '.'";
                    return false;
                }
            }
            else if (i < s.Length && s[i] != '[')
            {
                error = $"unexpected '{s[i]}' in path '{s}'";
                return false;
            }
        }

        return true;
    }

    /// <summary>Follows <paramref name="segments"/> from <paramref name="root"/>; false when any step is missing. A JSON null is found (value null).</summary>
    public static bool TryGet(JsonNode? root, IEnumerable<object> segments, out JsonNode? value)
    {
        value = root;
        foreach (var seg in segments)
        {
            switch (seg)
            {
                case string key when value is JsonObject o && o.TryGetPropertyValue(key, out var next):
                    value = next;
                    break;
                case int index when value is JsonArray a && index < a.Count:
                    value = a[index];
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        return true;
    }
}

/// <summary>
/// <c>${param}</c> / <c>${stepId.path[0].to.value}</c> templates. A string that is exactly one template keeps the JSON
/// type of the value (array, number, bool, object); otherwise each template is interpolated as text. Missing values
/// (parameter not given, step skipped, path absent) drop the property or array item that holds them; inside a longer
/// string they become empty text.
/// </summary>
public static class MacroTemplate
{
    private static readonly Regex Token = new(@"\$\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex Whole = new(@"^\$\{([^{}]*)\}\z", RegexOptions.Compiled);
    private static readonly Regex RootName = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Template expressions used in <paramref name="text"/> (without <c>${ }</c>).</summary>
    public static IEnumerable<string> Expressions(string text) => Token.Matches(text).Select(m => m.Groups[1].Value.Trim());

    /// <summary>True when <paramref name="text"/> has a <c>${</c> that is not a complete template.</summary>
    public static bool HasUnclosed(string text) => Token.Replace(text, string.Empty).Contains("${", StringComparison.Ordinal);

    /// <summary>Parses an expression into its root name (parameter or step id) and the path below it.</summary>
    public static bool TryParseExpression(string expression, out string root, out List<object> path, out string? error)
    {
        root = string.Empty;
        path = new List<object>();
        if (!MacroPath.TryParse(expression, out var segments, out error))
        {
            return false;
        }

        if (segments.Count == 0 || segments[0] is not string first || !RootName.IsMatch(first))
        {
            error = $"'${{{expression}}}' must start with a parameter name or a step id (lower snake_case)";
            return false;
        }

        root = first;
        path = segments.Skip(1).ToList();
        return true;
    }

    /// <summary>All template strings inside a JSON template (values only, not keys).</summary>
    public static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var s in o.SelectMany(p => Strings(p.Value)))
                {
                    yield return s;
                }

                break;
            case JsonArray a:
                foreach (var s in a.SelectMany(Strings))
                {
                    yield return s;
                }

                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                yield return v.GetValue<string>();
                break;
        }
    }

    /// <summary>Resolves a string: one whole template keeps its JSON type; otherwise interpolation.</summary>
    public static bool TryResolve(string text, MacroScope scope, out JsonNode? value)
    {
        var whole = Whole.Match(text);
        if (whole.Success)
        {
            if (scope.TryLookup(whole.Groups[1].Value.Trim(), out var found))
            {
                value = found?.DeepClone();
                return true;
            }

            value = null;
            return false;
        }

        if (!text.Contains("${", StringComparison.Ordinal))
        {
            value = JsonValue.Create(text);
            return true;
        }

        value = JsonValue.Create(Token.Replace(text, m => scope.TryLookup(m.Groups[1].Value.Trim(), out var v) ? AsText(v) : string.Empty));
        return true;
    }

    /// <summary>Deep-resolves a JSON template; false only when the node itself resolves to nothing.</summary>
    public static bool TryResolve(JsonNode? node, MacroScope scope, out JsonNode? value)
    {
        switch (node)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (key, child) in o)
                {
                    if (TryResolve(child, scope, out var resolved))
                    {
                        obj[key] = resolved;
                    }
                }

                value = obj;
                return true;
            case JsonArray a:
                var arr = new JsonArray();
                foreach (var child in a)
                {
                    if (TryResolve(child, scope, out var resolved))
                    {
                        arr.Add(resolved);
                    }
                }

                value = arr;
                return true;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return TryResolve(v.GetValue<string>(), scope, out value);
            default:
                value = node?.DeepClone();
                return true;
        }
    }

    /// <summary><c>when</c> semantics: missing, null, false, empty text, empty list/object → false.</summary>
    public static bool IsTruthy(bool present, JsonNode? value) => present && !IsEmpty(value) && !(value is JsonValue v && v.GetValueKind() == JsonValueKind.False);

    /// <summary>null, blank text, [] and {} are empty.</summary>
    public static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetValue<string>()),
            _ => false,
        },
        _ => false,
    };

    private static string AsText(JsonNode? v) => v switch
    {
        null => string.Empty,
        JsonValue s when s.GetValueKind() == JsonValueKind.String => s.GetValue<string>(),
        _ => v.ToJsonString(),
    };
}

/// <summary>Values templates can see while a macro runs: parameters (given or defaulted) and outputs of completed steps.</summary>
public sealed class MacroScope
{
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);

    public void Set(string root, JsonNode? value) => _values[root] = value;

    public bool Has(string root) => _values.ContainsKey(root);

    public bool TryLookup(string expression, out JsonNode? value)
    {
        value = null;
        return MacroTemplate.TryParseExpression(expression, out var root, out var path, out _) &&
               _values.TryGetValue(root, out var rootValue) &&
               MacroPath.TryGet(rootValue, path, out value);
    }
}
