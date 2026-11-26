namespace RevitAgent.Contracts.Logging
{
    using System.Text.Json.Serialization;

    public sealed class IpcAuditEntry
    {
        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public string Payload { get; set; } = string.Empty;

        [JsonPropertyName("contract_status")]
        public string ContractStatus { get; set; } = string.Empty;
    }

    public static class IpcAuditDirections
    {
        public const string Request = "REQUEST";
        public const string Response = "RESPONSE";
    }

    public static class IpcContractStatuses
    {
        public const string Valid = "VALID";
        public const string Malformed = "MALFORMED";
    }
}
