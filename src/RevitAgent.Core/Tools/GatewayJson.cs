using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitAgent.Core.Tools;

/// <summary>Shared JSON settings for tool results (snake_case, no nulls, readable Unicode).</summary>
public static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static JsonElement ToElement(object? value) =>
        value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize(object? value) =>
        value is JsonElement e ? e.GetRawText() : JsonSerializer.Serialize(value, Options);
}
