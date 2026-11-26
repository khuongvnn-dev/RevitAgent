using System.Globalization;
using RevitAgent.Contracts.Bridge;

namespace RevitAgent.Core.Revit;

/// <summary>
/// Cheap existence check of the per-process Bridge pipes (<c>\\.\pipe\revitagent-bridge-&lt;pid&gt;</c>) by listing
/// the pipe namespace. It never connects: opening the pipe (or GetFileAttributes on it) would consume a server
/// instance of the Bridge.
/// </summary>
public static class BridgePipes
{
    /// <summary>Pids that currently host a Bridge pipe, or null when the pipe namespace cannot be listed.</summary>
    public static HashSet<int>? ListPids()
    {
        try
        {
            var pids = new HashSet<int>();
            foreach (var path in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith(BridgePipeNames.Prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.AsSpan(BridgePipeNames.Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    pids.Add(pid);
                }
            }

            return pids;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True/false when known, null when the pipe namespace cannot be listed.</summary>
    public static bool? IsAvailable(int pid) => ListPids()?.Contains(pid);
}
