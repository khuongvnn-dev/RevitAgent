using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui.Script;

/// <summary>
/// A UI script: <c>{ name, timeout, screenshots, vars, dialogs, steps: [ { action, name, optional, window, ref | selector,
/// timeout_s, ...the arguments of the matching ui_* tool } ] }</c>. <c>action</c> is a ui_* tool name without the prefix
/// (plus <c>attach</c> and <c>pause</c>). Property names are case-insensitive.
/// </summary>
public sealed class UiScript
{
    private UiScript(JsonElement root, string? path)
    {
        Root = root;
        Path = path;
        Steps = Get(root, "steps") is { ValueKind: JsonValueKind.Array } s
            ? s.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select((e, i) => new UiScriptStep(e, i + 1)).ToList()
            : new List<UiScriptStep>();
    }

    public JsonElement Root { get; }

    /// <summary>Full path of the script file, or null for an inline script.</summary>
    public string? Path { get; }

    public string? Directory => Path is null ? null : System.IO.Path.GetDirectoryName(Path);

    public IReadOnlyList<UiScriptStep> Steps { get; }

    public string Name => Str(Root, "name") is { Length: > 0 } n ? n : Path is null ? "inline script" : System.IO.Path.GetFileNameWithoutExtension(Path);

    public int TimeoutSeconds => Int(Root, "timeout") is { } t && t > 0 ? t : 10;

    public string Screenshots => (Str(Root, "screenshots") ?? "fail").ToLowerInvariant();

    public JsonElement? Dialogs => Get(Root, "dialogs");

    public JsonElement? Vars => Get(Root, "vars") is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static UiScript Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw GatewayException.NotFound($"Cannot read script '{path}': {ex.Message}");
        }

        return Parse(text, System.IO.Path.GetFullPath(path));
    }

    public static UiScript Parse(string json, string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return FromElement(doc.RootElement.Clone(), path);
        }
        catch (JsonException ex)
        {
            throw GatewayException.Validation($"Script is not valid JSON: {ex.Message}");
        }
    }

    public static UiScript FromElement(JsonElement root, string? path = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw GatewayException.Validation("A UI script must be a JSON object with a 'steps' array.");
        }

        return new UiScript(root, path);
    }

    // ---------------------------------------------------------------- case-insensitive JSON helpers

    public static JsonElement? Get(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in o.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.Value;
            }
        }

        return null;
    }

    public static string? Str(JsonElement o, string name) => Get(o, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => v.GetRawText(),
        }
        : null;

    public static int? Int(JsonElement o, string name)
    {
        if (Get(o, name) is not { } v)
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
        {
            return (int)Math.Round(d);
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    public static bool Bool(JsonElement o, string name) => Get(o, name) is { } v && (v.ValueKind == JsonValueKind.True ||
        (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b) ||
        (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n != 0));
}

/// <summary>One step of a <see cref="UiScript"/>. Values are read raw; the runner expands <c>${var}</c> / <c>${env:X}</c>.</summary>
public sealed class UiScriptStep
{
    public UiScriptStep(JsonElement json, int number)
    {
        Json = json;
        Number = number;
    }

    public JsonElement Json { get; }

    public int Number { get; }

    /// <summary>The step action: a ui_* tool name without the prefix, <c>attach</c> or <c>pause</c>.</summary>
    public string Action => (UiScript.Str(Json, "action") ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>An optional step may fail (or be refused) without failing the run; the result is still recorded.</summary>
    public bool Optional => Flag("optional");

    public bool Has(string name) => UiScript.Get(Json, name) is not null;

    public string? Raw(string name) => UiScript.Str(Json, name);

    public bool Flag(string name) => UiScript.Bool(Json, name);

    public int? Int(string name) => UiScript.Int(Json, name);

    /// <summary>Label of a step: its name, else "action target [window 'w']".</summary>
    public string Label
    {
        get
        {
            if (Raw("name") is { Length: > 0 } n)
            {
                return n;
            }

            var parts = new List<string> { Action };
            if ((Raw("selector") ?? Raw("ref")) is { Length: > 0 } t)
            {
                parts.Add(t);
            }

            if (Raw("window") is { Length: > 0 } w)
            {
                parts.Add($"window '{w}'");
            }

            foreach (var k in new[] { "text", "item", "keys", "state", "is" })
            {
                if (Raw(k) is { Length: > 0 } v)
                {
                    parts.Add($"{k}='{v}'");
                }
            }

            if (Has("count"))
            {
                parts.Add("count=" + Raw("count"));
            }

            return string.Join(" ", parts);
        }
    }
}

/// <summary>Expands <c>${name}</c> (script vars + dir) and <c>${env:NAME}</c> (environment, with run overrides).</summary>
public sealed class UiScriptVars
{
    private static readonly Regex EnvPattern = new("\\$\\{env:([A-Za-z0-9_]+)\\}", RegexOptions.Compiled);
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) => _vars[name] = value;

    public void SetEnv(string name, string value) => _env[name] = value;

    public void Load(JsonElement? vars)
    {
        if (vars is not { ValueKind: JsonValueKind.Object } v)
        {
            return;
        }

        foreach (var p in v.EnumerateObject())
        {
            _vars[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText();
        }
    }

    public string? Expand(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains("${", StringComparison.Ordinal))
        {
            return s;
        }

        foreach (var kv in _vars)
        {
            s = s.Replace("${" + kv.Key + "}", kv.Value, StringComparison.Ordinal);
        }

        return EnvPattern.Replace(s, m => Env(m.Groups[1].Value));
    }

    /// <summary>
    /// Only REVITAGENT_UI_* variables (the built-in HOST_PID and OUT, or ones set by the caller) come from the environment; anything else expands to "" so a
    /// script cannot read the gateway's secrets (tokens, keys) and type or echo them.
    /// </summary>
    private string Env(string name)
    {
        if (_env.TryGetValue(name, out var o))
        {
            return o;
        }

        return name.StartsWith("REVITAGENT_UI_", StringComparison.OrdinalIgnoreCase) ? Environment.GetEnvironmentVariable(name) ?? string.Empty : string.Empty;
    }
}
