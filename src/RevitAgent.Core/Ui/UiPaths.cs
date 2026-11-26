using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui;

/// <summary>Where UI evidence (screenshots, tables, script reports) may be written: the runs and captures folders only.</summary>
public static class UiPaths
{
    /// <summary>
    /// Full path of <paramref name="requested"/> (relative to <paramref name="baseDir"/>) when it lies inside the gateway's
    /// runs or captures folder; PERMISSION_DENIED otherwise (audit, config and logs are never overwritten).
    /// </summary>
    public static string Evidence(AgentPaths paths, string requested, string baseDir)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(baseDir, requested));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw GatewayException.Validation($"Invalid path '{requested}': {ex.Message}");
        }

        if (!IsUnder(full, paths.RunsDir) && !IsUnder(full, paths.CapturesDir))
        {
            throw GatewayException.Denied(
                $"Path '{full}' is outside the gateway evidence folders ({paths.RunsDir} or {paths.CapturesDir}); write evidence under ${{env:REVITAGENT_UI_OUT}} / the captures folder.");
        }

        return full;
    }

    public static bool IsUnder(string full, string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
