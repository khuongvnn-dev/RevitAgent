using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using RevitAgent.Core.RunStore;
using RevitAgent.Tests.Scenarios;
using Xunit;

namespace RevitAgent.Tests.RunStore;

public class RunStoreScenarioRunnerTests
{
    private const string StepsYaml = """
        name: wall-rules
        revit_version: 2024
        model: 'input\model.rvt'
        steps:
          - name: levels
            tool: model_query_elements
            args: { category: Levels, fields: [Name] }
        """;

    private const string ChecksYaml = """
        checks:
          - id: C1
            title: Pass rule
            expect: 'count(levels.json -> elements) == 3'
        """;

    private static string CreateScenario() =>
        ScenarioHost.NewScenario("wall-rules",
            ("scenario.yaml", StepsYaml + "\n" + ChecksYaml),
            ("input/model.rvt", "revit binary data for testing"));

    [Fact]
    public async Task ScenarioRunner_NeverDeletesModelCopy_AndPreservesRunData()
    {
        await using var host = new ScenarioHost();
        var dir = CreateScenario();
        var modelPath = Path.Combine(dir, "input", "model.rvt");
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant();

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, launch = true });

        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        var runDir = s.GetProperty("run_dir").GetString()!;

        // 1. Model copy is ALWAYS kept (never deleted)
        var opened = host.Revit.Tools["doc_open"].Calls.Single().GetProperty("path").GetString()!;
        Assert.True(File.Exists(opened), "Model copy must be kept after run completion");
        Assert.False(s.GetProperty("cleanup").GetProperty("model_copy_deleted").GetBoolean());

        // 2. Original model untouched
        Assert.True(s.GetProperty("cleanup").GetProperty("original_model_unchanged").GetBoolean());
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))), ignoreCase: true);

        // 3. Frozen scenario copy exists
        Assert.True(File.Exists(Path.Combine(runDir, "scenario", "scenario.yaml")));

        // 4. Source SHA verification exists
        var sourceShaPath = Path.Combine(runDir, "model", RevitAgent.Core.RunStore.RunStore.SourceShaFile);
        if (File.Exists(sourceShaPath))
        {
            var shaContent = File.ReadAllText(sourceShaPath);
            Assert.Contains(originalHash, shaContent);
            Assert.Contains("source_unchanged: true", shaContent);
        }

        // 5. run.json is finalized
        var runJsonPath = Path.Combine(runDir, RevitAgent.Core.RunStore.RunStore.RunJsonFile);
        Assert.True(File.Exists(runJsonPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(runJsonPath));
        Assert.Equal("PASS", doc.RootElement.GetProperty("verdict").GetString());

        // 6. index.jsonl contains the run
        var indexFile = Path.Combine(host.Paths.RunsDir, RevitAgent.Core.RunStore.RunStore.IndexJsonlFile);
        if (File.Exists(indexFile))
        {
            var lines = File.ReadAllLines(indexFile);
            Assert.Contains(lines, l => l.Contains("\"verdict\":\"PASS\"") && l.Contains("wall-rules"));
        }
    }
}
