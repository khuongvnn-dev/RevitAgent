using System.IO;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Scenario;
using RevitAgent.Tests.Scenarios;
using RunStoreService = RevitAgent.Core.RunStore.RunStore;
using Xunit;

namespace RevitAgent.Tests.Scenarios;

public class ScenarioLibraryTests : IAsyncDisposable
{
    private readonly ScenarioHost _host = new();

    private const string Scenario1Yaml = """
        name: scenario-one
        revit_version: 2024
        description: First sample scenario description
        steps:
          - name: s1
            tool: schedule_list_schedulable_categories
        """;

    private const string Scenario2Yaml = """
        name: scenario-two
        revit_version: 2025
        description: Second sample scenario description
        steps:
          - name: s2
            tool: schedule_list_schedulable_categories
        """;

    [Fact]
    public void ListScenarios_DiscoversSubfoldersWithScenarioYaml()
    {
        var root = TestGateway.NewTempDir("lib_test_");
        var folder1 = Path.Combine(root, "ScenarioA");
        var folder2 = Path.Combine(root, "ScenarioB");
        var folderIgnored = Path.Combine(root, "IgnoredFolder");

        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);
        Directory.CreateDirectory(folderIgnored);

        File.WriteAllText(Path.Combine(folder1, "scenario.yaml"), Scenario1Yaml);
        File.WriteAllText(Path.Combine(folder2, "scenario.yaml"), Scenario2Yaml);
        File.WriteAllText(Path.Combine(folderIgnored, "notes.txt"), "no scenario here");

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        var libService = new ScenarioLibraryService(_host.Paths, runStore);

        var list = libService.ListScenarios(root);

        Assert.Equal(2, list.Count);

        var item1 = list.FirstOrDefault(x => x.Name == "ScenarioA");
        Assert.NotNull(item1);
        Assert.Equal("scenario-one", item1.ScenarioName);
        Assert.Equal(2024, item1.RevitVersion);
        Assert.Equal("First sample scenario description", item1.Description);
        Assert.Null(item1.LastVerdict);

        var item2 = list.FirstOrDefault(x => x.Name == "ScenarioB");
        Assert.NotNull(item2);
        Assert.Equal("scenario-two", item2.ScenarioName);
        Assert.Equal(2025, item2.RevitVersion);
    }

    [Fact]
    public void ListScenarios_EnrichesWithLastRunVerdictFromRunStore()
    {
        var root = TestGateway.NewTempDir("lib_verdict_test_");
        var folder = Path.Combine(root, "ScenarioWithHistory");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "scenario.yaml"), Scenario1Yaml);

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        // Create and finish a run in the run store for scenario-one
        var run = runStore.CreateRun("scenario-one", trigger: "manual");
        runStore.FinishRun(run.FolderPath, new RunFinishInfo
        {
            EndedAt = DateTimeOffset.Now,
            Verdict = "PASS",
        });

        var libService = new ScenarioLibraryService(_host.Paths, runStore);
        var list = libService.ListScenarios(root);

        Assert.Single(list);
        Assert.Equal("scenario-one", list[0].ScenarioName);
        Assert.Equal("PASS", list[0].LastVerdict);
        Assert.NotNull(list[0].LastRunId);
    }

    [Fact]
    public void ScenarioLibraryOptions_PersistsAndLoadsConfig()
    {
        var tempCustomDir = TestGateway.NewTempDir("custom_scenarios_");

        // Save
        ScenarioLibraryOptions.Save(_host.Paths, tempCustomDir);

        // Load
        var loaded = ScenarioLibraryOptions.Load(_host.Paths);
        Assert.Equal(tempCustomDir, loaded.LibraryDir);

        var effective = ScenarioLibraryOptions.GetEffectiveLibraryDir(_host.Paths);
        Assert.Equal(Path.GetFullPath(tempCustomDir), effective);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }
}
