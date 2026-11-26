using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Persisted options for the scenario library folder (%REVITAGENT_HOME%\config\scenarios.json).
/// </summary>
public sealed class ScenarioLibraryOptions
{
    public const string ConfigFileName = "scenarios.json";

    public string? LibraryDir { get; set; }

    public static string GetEffectiveLibraryDir(AgentPaths paths)
    {
        var loaded = Load(paths);
        return !string.IsNullOrWhiteSpace(loaded.LibraryDir)
            ? Path.GetFullPath(loaded.LibraryDir)
            : Path.Combine(paths.Root, "scenarios");
    }

    public static ScenarioLibraryOptions Load(AgentPaths paths)
    {
        var file = Path.Combine(paths.ConfigDir, ConfigFileName);
        if (File.Exists(file))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("library_dir", out var dirProp))
                {
                    return new ScenarioLibraryOptions { LibraryDir = dirProp.GetString() };
                }
            }
            catch
            {
                // Fall back to default
            }
        }

        return new ScenarioLibraryOptions();
    }

    public static void Save(AgentPaths paths, string libraryDir)
    {
        Directory.CreateDirectory(paths.ConfigDir);
        var file = Path.Combine(paths.ConfigDir, ConfigFileName);
        var obj = new JsonObject
        {
            ["library_dir"] = libraryDir,
        };
        File.WriteAllText(file, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
