using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAgent.Core.RunStore;

/// <summary>
/// Persisted options for the run data store (%REVITAGENT_HOME%\config\run-store.json).
/// Default root is AgentPaths.RunsDir (%REVITAGENT_HOME%\runs).
/// </summary>
public sealed record RunStoreOptions
{
    public const string FileName = "run-store.json";

    public string? Root { get; init; }

    /// <summary>
    /// The configured root, or the default runs folder when none is set OR the configured one is not allowed
    /// (<see cref="Validate"/>): the root is part of the gateway sandbox, so a hand-edited file must never widen it.
    /// </summary>
    public string GetEffectiveRoot(AgentPaths paths) =>
        string.IsNullOrWhiteSpace(Root) || Validate(Root) is not null ? paths.RunsDir : Path.GetFullPath(Root.Trim());

    /// <summary>
    /// Why <paramref name="root"/> cannot hold run data (null = allowed; empty = the default folder). The root becomes part of
    /// the gateway sandbox (models are opened only from inside it), so it must be a dedicated local folder: not relative,
    /// not a network path, not a drive root, not a system folder, not the whole user profile.
    /// </summary>
    public static string? Validate(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var r = root.Trim();
        if (r.StartsWith(@"\\", StringComparison.Ordinal) || r.StartsWith("//", StringComparison.Ordinal))
        {
            return "Network (UNC) paths are not allowed: run data must live on this machine.";
        }

        if (!Path.IsPathFullyQualified(r))
        {
            return "A full path is required, e.g. D:\\RevitAgentRuns.";
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The path is not valid.";
        }

        var driveRoot = Path.GetPathRoot(full) ?? string.Empty;
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(driveRoot), StringComparison.OrdinalIgnoreCase))
        {
            return "A drive root is not allowed (the whole drive would become gateway sandbox): pick a dedicated folder.";
        }

        try
        {
            if (new DriveInfo(driveRoot).DriveType is DriveType.Network)
            {
                return "Network drives are not allowed: run data must live on this machine.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Unknown drive type: the checks below still apply.
        }

        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
        {
            var sys = Environment.GetFolderPath(folder);
            if (sys.Length > 0 && IsSameOrUnder(full, sys))
            {
                return $"System folders are not allowed ({sys}).";
            }
        }

        foreach (var broad in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Desktop) })
        {
            if (broad.Length > 0 && string.Equals(full, Path.TrimEndingDirectorySeparator(broad), StringComparison.OrdinalIgnoreCase))
            {
                return $"The folder is too broad ({broad}): pick a dedicated subfolder for run data.";
            }
        }

        return null;
    }

    private static bool IsSameOrUnder(string path, string folder)
    {
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(path, f, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static RunStoreOptions Load(AgentPaths paths)
    {
        var file = Path.Combine(paths.ConfigDir, FileName);
        if (!File.Exists(file))
        {
            return new RunStoreOptions();
        }

        try
        {
            var json = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.TryGetProperty("root", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            return new RunStoreOptions { Root = root };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RunStoreOptions();
        }
    }

    public static void Save(AgentPaths paths, RunStoreOptions options)
    {
        if (Validate(options.Root) is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(options));
        }

        Directory.CreateDirectory(paths.ConfigDir);
        var file = Path.Combine(paths.ConfigDir, FileName);
        var tmp = file + ".tmp";
        var obj = new JsonObject
        {
            ["root"] = options.Root,
        };
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, overwrite: true);
    }
}
