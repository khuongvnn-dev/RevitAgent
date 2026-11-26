namespace RevitAgent.Contracts.Protocol
{
    public static class ErrorCodes
    {
        // Standard JSON-RPC 2.0 Error Codes
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        // Custom RevitAgent Error Codes (-32000 to -32099)
        public const int RevitNotReady = -32001;
        public const int ModelNotOpen = -32002;
        public const int CommandTimeout = -32003;
        public const int TransactionFailed = -32004;
        public const int DialogInterference = -32005;
        public const int BridgeDisconnected = -32006;
    }
}
