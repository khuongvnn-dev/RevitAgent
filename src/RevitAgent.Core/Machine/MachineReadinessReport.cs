using System.Text.Json.Serialization;

namespace RevitAgent.Core.Machine;

/// <summary>Overall report of machine readiness for unattended runs.</summary>
public sealed record MachineReadinessReport
{
    [JsonPropertyName("ready_for_unattended")]
    public required bool ReadyForUnattended { get; init; }

    [JsonPropertyName("checked_at")]
    public required DateTimeOffset CheckedAt { get; init; }

    [JsonPropertyName("year")]
    public required int Year { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<MachineCheckItem> Items { get; init; }
}
