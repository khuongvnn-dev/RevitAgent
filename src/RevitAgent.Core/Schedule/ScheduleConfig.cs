using System.Text.Json.Serialization;

namespace RevitAgent.Core.Schedule;

/// <summary>
/// Nightly schedule configuration (%REVITAGENT_HOME%\config\schedule.json).
/// </summary>
public sealed class ScheduleConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("time")]
    public string Time { get; set; } = "22:00";

    [JsonPropertyName("days")]
    public List<string> Days { get; set; } = new() { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    [JsonPropertyName("scenarios")]
    public List<string> Scenarios { get; set; } = new();

    [JsonPropertyName("revit_year")]
    public int RevitYear { get; set; } = 2024;

    [JsonPropertyName("last_fired")]
    public DateTimeOffset? LastFired { get; set; }

    [JsonPropertyName("last_missed_notified")]
    public DateTimeOffset? LastMissedNotified { get; set; }
}
