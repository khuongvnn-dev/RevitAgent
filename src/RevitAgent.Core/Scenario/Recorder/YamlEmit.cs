using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Scenario.Recorder;

/// <summary>
/// Minimal YAML emitter for generated scenarios: block mappings for keys, flow style for values, every string
/// single-quoted (safe for Windows paths, <c>{placeholders}</c> and expressions).
/// </summary>
public static class YamlEmit
{
    private static readonly Regex PlainKey = new(@"^[A-Za-z_][A-Za-z0-9_\-]*$", RegexOptions.Compiled);

    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    public static string Key(string k) => PlainKey.IsMatch(k) ? k : Quote(k);

    /// <summary>A JSON value in YAML flow style: <c>'text'</c>, <c>42</c>, <c>true</c>, <c>[a, b]</c>, <c>{k: v}</c>.</summary>
    public static string Flow(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => o.Count == 0 ? "{}" : "{ " + string.Join(", ", o.Select(p => $"{Key(p.Key)}: {Flow(p.Value)}")) + " }",
        JsonArray a => "[" + string.Join(", ", a.Select(Flow)) + "]",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => Quote(v.GetValue<string>()),
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "null",
        },
        _ => "null",
    };

    /// <summary>Appends <c>key:</c> + one indented line per entry (values in flow style).</summary>
    public static void Mapping(StringBuilder sb, string indent, string key, JsonObject o)
    {
        if (o.Count == 0)
        {
            sb.Append(indent).Append(key).AppendLine(": {}");
            return;
        }

        sb.Append(indent).Append(key).AppendLine(":");
        foreach (var (k, v) in o)
        {
            sb.Append(indent).Append("  ").Append(Key(k)).Append(": ").AppendLine(Flow(v));
        }
    }

    public static string Number(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}
