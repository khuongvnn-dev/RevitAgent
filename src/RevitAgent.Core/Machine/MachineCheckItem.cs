using System.Text.Json.Serialization;

namespace RevitAgent.Core.Machine;

/// <summary>Result of an individual machine readiness check item.</summary>
public sealed record MachineCheckItem
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }

    [JsonPropertyName("fix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Fix { get; init; }
}
