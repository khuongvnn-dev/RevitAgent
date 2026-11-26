using System.Globalization;
using System.Text.Json;

namespace RevitAgent.Core.Tools;

/// <summary>
/// One invocation of a gateway tool: name + JSON-object arguments + session info.
/// Argument helpers throw <see cref="GatewayException"/> (VALIDATION_FAILED) with a message
/// the agent can act on, so tools never have to hand-roll argument parsing.
/// </summary>
public sealed class ToolCall
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public ToolCall(string name, JsonElement? arguments, string sessionId = "local")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Arguments = arguments is { ValueKind: JsonValueKind.Object } a ? a : EmptyObject;
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? "local" : sessionId;
    }

    public string Name { get; }

    /// <summary>Always a JSON object (empty when the client sent none).</summary>
    public JsonElement Arguments { get; }

    /// <summary>Identifies the MCP client session (for recorder/audit correlation).</summary>
    public string SessionId { get; }

    /// <summary>Correlation id for audit/log lines of this call.</summary>
    public string CallId { get; } = Guid.NewGuid().ToString("N")[..12];

    public bool Has(string name) =>
        Arguments.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public JsonElement? Raw(string name) =>
        Arguments.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public string RequireString(string name)
    {
        var s = GetString(name);
        if (string.IsNullOrWhiteSpace(s))
        {
            throw GatewayException.Validation($"Missing required argument '{name}' (string).");
        }

        return s;
    }

    public string? GetString(string name, string? defaultValue = null)
    {
        if (Raw(name) is not { } v)
        {
            return defaultValue;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => throw GatewayException.Validation($"Argument '{name}' must be a string."),
        };
    }

    public int RequireInt(string name) =>
        GetInt(name) ?? throw GatewayException.Validation($"Missing required argument '{name}' (integer).");

    public int? GetInt(string name, int? defaultValue = null)
    {
        if (Raw(name) is not { } v)
        {
            return defaultValue;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) && l is >= int.MinValue and <= int.MaxValue)
        {
            return (int)l;
        }

        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            return i;
        }

        throw GatewayException.Validation($"Argument '{name}' must be an integer.");
    }

    public long? GetLong(string name, long? defaultValue = null)
    {
        if (Raw(name) is not { } v)
        {
            return defaultValue;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
        {
            return l;
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString() ?? string.Empty;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                return hex;
            }

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            {
                return dec;
            }
        }

        throw GatewayException.Validation($"Argument '{name}' must be an integer (decimal or 0x-hex string).");
    }

    public double? GetDouble(string name, double? defaultValue = null)
    {
        if (Raw(name) is not { } v)
        {
            return defaultValue;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.GetDouble();
        }

        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        throw GatewayException.Validation($"Argument '{name}' must be a number.");
    }

    public bool GetBool(string name, bool defaultValue = false)
    {
        if (Raw(name) is not { } v)
        {
            return defaultValue;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => throw GatewayException.Validation($"Argument '{name}' must be a boolean."),
        };
    }

    public IReadOnlyList<string> GetStringList(string name)
    {
        if (Raw(name) is not { } v)
        {
            return Array.Empty<string>();
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return new[] { v.GetString() ?? string.Empty };
        }

        if (v.ValueKind != JsonValueKind.Array)
        {
            throw GatewayException.Validation($"Argument '{name}' must be an array of strings.");
        }

        return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.GetRawText()).ToArray();
    }

    /// <summary>Returns a positive integer limit, defaulting to <paramref name="defaultValue"/> and capped at <paramref name="max"/>.</summary>
    public int GetLimit(string name = "limit", int defaultValue = 100, int max = 1000)
    {
        var v = GetInt(name, defaultValue) ?? defaultValue;
        if (v <= 0)
        {
            throw GatewayException.Validation($"Argument '{name}' must be > 0.");
        }

        return Math.Min(v, max);
    }

    /// <summary>Deserializes the argument object (or one property) into <typeparamref name="T"/> using snake_case.</summary>
    public T Bind<T>(string? property = null)
    {
        var src = property is null ? Arguments : Raw(property) ?? EmptyObject;
        try
        {
            return src.Deserialize<T>(GatewayJson.Options)
                ?? throw GatewayException.Validation($"Arguments{(property is null ? string.Empty : $" '{property}'")} could not be read.");
        }
        catch (JsonException ex)
        {
            throw GatewayException.Validation($"Invalid arguments{(property is null ? string.Empty : $" '{property}'")}: {ex.Message}");
        }
    }

    public ToolCall WithArguments(JsonElement arguments) => new(Name, arguments, SessionId);
}
