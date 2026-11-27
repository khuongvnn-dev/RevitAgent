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

    /// <summary>
    /// An explicit <c>save_path</c> for an image the call will create: inside the evidence folders (<see cref="Evidence"/>),
    /// with no extension (".png" is added) or ".png" (any other extension: VALIDATION_FAILED), and NEW: an existing file or
    /// folder is CONFLICT, so run data and evidence are never overwritten.
    /// </summary>
    public static string NewImageFile(AgentPaths paths, string requested, string baseDir)
    {
        var full = Evidence(paths, requested, baseDir);
        var ext = Path.GetExtension(full);
        if (ext.Length == 0)
        {
            full += ImageExtension;
        }
        else if (!string.Equals(ext, ImageExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw GatewayException.Validation($"save_path '{requested}' must be a .png file (images are written as PNG).");
        }

        EnsureNew(full);
        return full;
    }

    /// <summary>
    /// True for an empty file: a name claimed by <see cref="Sandbox.EvidenceService.Reserve"/> (or any 0-byte file), which
    /// may be written because no data can be lost.
    /// </summary>
    public static bool IsEmptyReservation(string full)
    {
        try
        {
            return File.Exists(full) && new FileInfo(full).Length == 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>CONFLICT when <paramref name="full"/> already exists (a folder, or a file with content; an empty reserved file is fine).</summary>
    public static void EnsureNew(string full)
    {
        if (Directory.Exists(full) || (File.Exists(full) && !IsEmptyReservation(full)))
        {
            throw new GatewayException(
                Contracts.Protocol.GatewayErrorCodes.Conflict,
                $"'{full}' already exists. An explicit save_path must name a new file: existing evidence and run data are never overwritten.");
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a file that must not exist yet, or that is an empty reserved file
    /// (<see cref="IsEmptyReservation"/>); a file with content is CONFLICT, never overwritten.
    /// </summary>
    public static void WriteNew(string full, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            // An empty reservation is filled in place (Truncate opens an existing file only); otherwise the file must be new.
            var mode = IsEmptyReservation(full) ? FileMode.Truncate : FileMode.CreateNew;
            using var stream = new FileStream(full, mode, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (IOException) when (File.Exists(full))
        {
            EnsureNew(full);
            throw;
        }
    }

    public const string ImageExtension = ".png";

    public static bool IsUnder(string full, string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
