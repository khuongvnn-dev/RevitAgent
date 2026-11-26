namespace RevitAgent.Host.Services
{
    using System;
    using System.IO;

    /// <summary>Finds RevitAgent.Shim.exe, the stdio relay an AI client launches to reach this gateway.</summary>
    public static class ShimLocator
    {
        public const string ShimFileName = "RevitAgent.Shim.exe";

        private const string HostProject = "RevitAgent.Host";
        private const string ShimProject = "RevitAgent.Shim";

        /// <summary>
        /// Same folder as the Host exe (deployed layout), else the dev layout
        /// <c>src\RevitAgent.Shim\bin\&lt;cfg&gt;\&lt;tfm&gt;\RevitAgent.Shim.exe</c> derived from
        /// <c>src\RevitAgent.Host\bin\&lt;cfg&gt;\&lt;tfm&gt;\</c>. Returns null when neither exists.
        /// </summary>
        public static string? Resolve(string? baseDirectory = null)
        {
            var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
            var deployed = Path.Combine(baseDir, ShimFileName);
            if (File.Exists(deployed))
            {
                return Path.GetFullPath(deployed);
            }

            var dev = DevLayoutCandidate(baseDir);
            return dev is not null && File.Exists(dev) ? dev : null;
        }

        /// <summary>Where the Shim would be in the dev layout (whether or not it is built), or null outside that layout.</summary>
        public static string? DevLayoutCandidate(string baseDirectory)
        {
            var parts = Path.GetFullPath(baseDirectory).TrimEnd('\\', '/').Split('\\', '/');
            for (int i = parts.Length - 2; i >= 1; i--)
            {
                if (parts[i] == "bin" && string.Equals(parts[i - 1], HostProject, StringComparison.OrdinalIgnoreCase))
                {
                    parts[i - 1] = ShimProject;
                    return string.Join("\\", parts) + "\\" + ShimFileName;
                }
            }

            return null;
        }

        /// <summary><c>claude mcp add revitagent -- "&lt;shim&gt;"</c></summary>
        public static string ClaudeCodeCommand(string shimPath) => $"claude mcp add revitagent -- \"{shimPath}\"";

        /// <summary>The <c>mcpServers</c> snippet for Claude Desktop / Cursor (paths JSON-escaped).</summary>
        public static string McpServersJson(string shimPath)
        {
            var escaped = System.Text.Json.JsonSerializer.Serialize(shimPath);
            return "{\"mcpServers\":{\"revitagent\":{\"command\":" + escaped + ",\"args\":[]}}}";
        }
    }
}
