namespace RevitAgent.Contracts.Bridge
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// One request from the Host to the in-Revit Bridge. Framed with
    /// <see cref="Framing.LengthPrefixedPipeFramer"/> (4-byte little-endian length + UTF-8 JSON).
    /// This is an internal protocol: it is NOT JSON-RPC and never reaches an AI client directly.
    /// </summary>
    public sealed class BridgeRequest
    {
        /// <summary>Correlation id chosen by the Host (opaque string).</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>Bridge tool name, e.g. "doc_info", "model_query_elements".</summary>
        [JsonPropertyName("tool")]
        public string Tool { get; set; } = string.Empty;

        /// <summary>Tool arguments as a JSON object (may be null/undefined for no arguments).</summary>
        [JsonPropertyName("args")]
        public JsonElement? Args { get; set; }

        /// <summary>Execution budget the Host grants for this call, in seconds (0 = Bridge default).</summary>
        [JsonPropertyName("timeout_sec")]
        public int TimeoutSec { get; set; }
    }

    /// <summary>Response from the Bridge for one <see cref="BridgeRequest"/>.</summary>
    public sealed class BridgeResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("error")]
        public BridgeError? Error { get; set; }

        public static BridgeResponse Success(string id, JsonElement result) =>
            new BridgeResponse { Id = id, Ok = true, Result = result };

        public static BridgeResponse Failure(string id, string code, string message, JsonElement? details = null) =>
            new BridgeResponse { Id = id, Ok = false, Error = new BridgeError { Code = code, Message = message, Details = details } };
    }

    public sealed class BridgeError
    {
        /// <summary>One of <see cref="Protocol.GatewayErrorCodes"/>.</summary>
        [JsonPropertyName("code")]
        public string Code { get; set; } = Protocol.GatewayErrorCodes.Internal;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public JsonElement? Details { get; set; }
    }
}
