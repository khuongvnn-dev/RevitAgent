using System.Globalization;
using System.Text.Json;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Tolerant readers for results of tools owned by other modules (sandbox, process, Bridge, UI), whose exact
/// field names may vary: each lookup tries several dotted paths in order (snake_case first, then camelCase aliases).
/// </summary>
public static class ResultFields
{
    public static readonly string[] DocumentPath =
    {
        "path", "path_name", "pathName", "file_path", "full_path", "document.path", "document.path_name", "document.pathName",
        "active_document.path", "active_document.path_name", "doc.path", "result.path", "result.pathName",
    };

    public static readonly string[] Pid = { "pid", "process_id", "processId", "instance.pid", "process.pid", "result.pid" };

    public static readonly string[] RunDir = { "path", "run_dir", "dir", "directory", "run.path" };

    public static readonly string[] RunId = { "run_id", "runId", "id", "run.id" };

    public static readonly string[] CopyPath = { "copy_path", "copyPath", "path", "destination", "model_copy" };

    public static string? FindString(JsonElement? root, params string[] paths)
    {
        if (root is not { } r)
        {
            return null;
        }

        foreach (var p in paths)
        {
            if (TryPath(r, p, out var v))
            {
                var s = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }

        return null;
    }

    public static int? FindInt(JsonElement? root, params string[] paths)
    {
        var s = FindString(root, paths);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    public static bool TryPath(JsonElement root, string dotted, out JsonElement value)
    {
        value = root;
        foreach (var seg in dotted.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(seg, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    /// <summary>True when any property named <paramref name="name"/> (at any depth) equals <paramref name="number"/>.</summary>
    public static bool ContainsNumber(JsonElement? root, string name, long number)
    {
        if (root is not { } r)
        {
            return false;
        }

        var stack = new Stack<JsonElement>();
        stack.Push(r);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        ((p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n) && n == number) ||
                         (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == number.ToString(CultureInfo.InvariantCulture))))
                    {
                        return true;
                    }

                    stack.Push(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray())
                {
                    stack.Push(x);
                }
            }
        }

        return false;
    }

    /// <summary>First line of an error result's message (or of the text), for logs and reports.</summary>
    public static string Message(ToolResult r)
    {
        if (r.Structured is { } s && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            var code = s.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() + ": " : string.Empty;
            return Clip(code + m.GetString(), 400);
        }

        var text = r.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
        return Clip(text.Split('\n')[0], 400);
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Does the tool's input schema declare <paramref name="property"/>?</summary>
    public static bool HasArgument(IGatewayTool tool, string property) =>
        tool.InputSchema.ValueKind == JsonValueKind.Object &&
        tool.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object &&
        props.TryGetProperty(property, out _);
}
