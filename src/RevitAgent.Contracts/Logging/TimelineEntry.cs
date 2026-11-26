namespace RevitAgent.Contracts.Logging
{
    using System.Text.Json.Serialization;

    public sealed class TimelineEntry
    {
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("automation_id")]
        public string AutomationId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("duration_ms")]
        public long DurationMs { get; set; }

        [JsonPropertyName("details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Details { get; set; }
    }
}
