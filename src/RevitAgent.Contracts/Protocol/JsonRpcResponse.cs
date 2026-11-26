namespace RevitAgent.Contracts.Protocol
{
    using System.Text.Json.Serialization;

    public sealed class JsonRpcResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("result")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Result { get; set; }

        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonRpcError? Error { get; set; }

        public JsonRpcResponse()
        {
        }

        public static JsonRpcResponse CreateSuccess(string? id, object? result)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Result = result
            };
        }

        public static JsonRpcResponse CreateError(string? id, int code, string message, object? data = null)
        {
            return new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError(code, message, data)
            };
        }
    }
}
