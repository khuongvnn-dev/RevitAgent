namespace RevitAgent.Contracts.Protocol
{
    using System.Text.Json.Serialization;

    public sealed class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public object? Params { get; set; }

        public JsonRpcRequest()
        {
        }

        public JsonRpcRequest(string method, object? @params = null, string? id = null)
        {
            JsonRpc = "2.0";
            Method = method;
            Params = @params;
            Id = id;
        }
    }
}
