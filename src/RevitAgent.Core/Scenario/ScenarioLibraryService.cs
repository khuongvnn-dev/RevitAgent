using RevitAgent.Core.RunStore;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Service that discovers scenarios in the configured library folder (%REVITAGENT_HOME%\scenarios).
/// </summary>
public sealed class ScenarioLibraryService
{
    private readonly AgentPaths _paths;
    private readonly RunStore.RunStore _runStore;

    public ScenarioLibraryService(AgentPaths paths, RunStore.RunStore runStore)
    {
        _paths = paths;
        _runStore = runStore;
    }

    public string LibraryDir => ScenarioLibraryOptions.GetEffectiveLibraryDir(_paths);

    public void SetLibraryDir(string dir)
    {
        ScenarioLibraryOptions.Save(_paths, dir);
    }

    public IReadOnlyList<ScenarioLibraryItem> ListScenarios(string? customDir = null)
    {
        var dir = customDir ?? LibraryDir;
        if (!Directory.Exists(dir))
        {
            return Array.Empty<ScenarioLibraryItem>();
        }

        var items = new List<ScenarioLibraryItem>();
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            var scenarioPath = Path.Combine(subDir, ScenarioDefinition.FileName);
            if (!File.Exists(scenarioPath))
            {
                continue;
            }

            var load = ScenarioLoader.Load(subDir);
            if (load.Scenario is not null)
            {
                var scenarioName = load.Scenario.Name;
                var folderName = Path.GetFileName(subDir);

                string? lastVerdict = null;
                string? lastRunId = null;
                DateTimeOffset? lastRunTime = null;

                try
                {
                    var recent = _runStore.ListRuns(new RunFilter { Scenario = scenarioName, Limit = 1 });
                    if (recent.Count > 0)
                    {
                        lastVerdict = recent[0].Verdict;
                        lastRunId = recent[0].Id;
                        lastRunTime = recent[0].StartedAt;
                    }
                }
                catch
                {
                    // Best-effort lookup
                }

                items.Add(new ScenarioLibraryItem
                {
                    Directory = subDir,
                    Name = folderName,
                    ScenarioName = scenarioName,
                    RevitVersion = load.Scenario.RevitVersion,
                    Description = load.Scenario.Description,
                    LastVerdict = lastVerdict,
                    LastRunId = lastRunId,
                    LastRunTime = lastRunTime,
                });
            }
        }

        return items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
