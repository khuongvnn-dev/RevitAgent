namespace RevitAgent.Contracts.Protocol
{
    /// <summary>
    /// Centralized Single Source of Truth for Named Pipe IPC channel identifiers.
    /// </summary>
    public static class IpcConstants
    {
        /// <summary>
        /// Dedicated Named Pipe name for Anthropic MCP AI clients (Antigravity, Claude Desktop).
        /// </summary>
        public const string DefaultMcpPipeName = "revitagent-mcp";

        /// <summary>
        /// Dedicated internal Named Pipe name for the in-process Autodesk Revit Bridge Add-in.
        /// </summary>
        public const string DefaultBridgePipeName = "revitagent-bridge";

        /// <summary>
        /// Standard prefix for dynamic Named Pipe server instances.
        /// </summary>
        public const string DefaultPipePrefix = "revitagent-";

        /// <summary>
        /// Default execution timeout for in-process Revit bridge operations (in seconds).
        /// </summary>
        public const int DefaultBridgeExecutionTimeoutSeconds = 25;

        /// <summary>
        /// Default gateway timeout on the Host before aborting IPC command forwarding (in seconds).
        /// Must be greater than DefaultBridgeExecutionTimeoutSeconds so Bridge can fail gracefully.
        /// </summary>
        public const int DefaultHostGatewayTimeoutSeconds = 30;
    }
}
