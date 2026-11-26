namespace RevitAgent.Contracts.Protocol
{
    using System.Text.Json.Serialization;

    public sealed class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Data { get; set; }

        public JsonRpcError()
        {
        }

        public JsonRpcError(int code, string message, object? data = null)
        {
            Code = code;
            Message = message;
            Data = data;
        }
    }
}
