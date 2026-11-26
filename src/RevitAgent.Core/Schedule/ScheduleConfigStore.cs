using System.Text.Json;

namespace RevitAgent.Core.Schedule;

/// <summary>
/// Persists and loads schedule configuration from %REVITAGENT_HOME%\config\schedule.json.
/// </summary>
public sealed class ScheduleConfigStore
{
    public const string ConfigFileName = "schedule.json";

    private readonly AgentPaths _paths;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public ScheduleConfigStore(AgentPaths paths)
    {
        _paths = paths;
    }

    public string ConfigFilePath => Path.Combine(_paths.ConfigDir, ConfigFileName);

    public ScheduleConfig Load()
    {
        var file = ConfigFilePath;
        if (File.Exists(file))
        {
            try
            {
                var json = File.ReadAllText(file);
                var config = JsonSerializer.Deserialize<ScheduleConfig>(json);
                if (config != null)
                {
                    return config;
                }
            }
            catch
            {
                // Fall back to default
            }
        }

        return new ScheduleConfig();
    }

    public void Save(ScheduleConfig config)
    {
        Directory.CreateDirectory(_paths.ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigFilePath, json);
    }
}
