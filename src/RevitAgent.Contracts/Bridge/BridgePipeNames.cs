namespace RevitAgent.Contracts.Bridge
{
    /// <summary>
    /// Naming of the per-process Bridge pipe. Each Revit process that loaded the Bridge add-in
    /// hosts its own pipe server named <c>revitagent-bridge-&lt;pid&gt;</c> (current-user ACL);
    /// the Host connects as a client. This lets one gateway drive several Revit instances.
    /// Pure BCL: this file is also compiled into the Bridge add-in (linked source).
    /// </summary>
    public static class BridgePipeNames
    {
        public const string Prefix = "revitagent-bridge-";

        public static string ForProcess(int processId) => Prefix + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Parses the pid out of a pipe name such as <c>revitagent-bridge-1234</c>; null when it is not a Bridge pipe.</summary>
        public static int? TryParsePid(string? pipeName)
        {
            if (pipeName == null || !pipeName.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return int.TryParse(pipeName.Substring(Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? pid
                : (int?)null;
        }
    }
}
