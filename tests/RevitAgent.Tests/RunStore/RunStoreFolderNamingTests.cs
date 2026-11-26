using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using Xunit;

namespace RevitAgent.Tests.RunStore;

public class RunStoreFolderNamingTests
{
    private static (RevitAgent.Core.RunStore.RunStore Store, AgentPaths Paths) Create()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var store = new RevitAgent.Core.RunStore.RunStore(paths, new GatewayPolicy());
        return (store, paths);
    }

    [Fact]
    public void CreateRun_FollowsExpectedFolderHierarchyAndNamingPattern()
    {
        var (store, paths) = Create();
        var now = DateTimeOffset.Now;
        var run = store.CreateRun("Kiểm tra Tường & Cửa #1", "manual", "unit test", "sess-1");

        // Folder name pattern: <HHmmss>_<slug>_<id6>
        Assert.Matches(@"^\d{6}_kiem-tra-tuong-cua-1_[0-9a-f]{6}$", run.Id);

        // Path: <root>\<yyyy-MM>\<yyyy-MM-dd>\<HHmmss>_<slug>_<id6>\
        var expectedMonth = now.ToString("yyyy-MM");
        var expectedDay = now.ToString("yyyy-MM-dd");
        Assert.Contains(Path.Combine(expectedMonth, expectedDay, run.Id), run.FolderPath);
        Assert.StartsWith(paths.RunsDir, run.FolderPath, StringComparison.OrdinalIgnoreCase);

        // Inner folders created
        Assert.True(Directory.Exists(run.DataDir), "data/ folder exists");
        Assert.True(Directory.Exists(run.EvidenceDir), "evidence/ folder exists");
        Assert.True(Directory.Exists(run.ScenarioDir), "scenario/ folder exists");
        Assert.True(Directory.Exists(run.ModelDir), "model/ folder exists");
        Assert.True(Directory.Exists(run.LogsDir), "logs/ folder exists");

        // run.json initial structure
        var runJsonPath = Path.Combine(run.FolderPath, RevitAgent.Core.RunStore.RunStore.RunJsonFile);
        Assert.True(File.Exists(runJsonPath), "run.json exists");
        using var doc = JsonDocument.Parse(File.ReadAllText(runJsonPath));
        var root = doc.RootElement;
        Assert.Equal(run.Id, root.GetProperty("id").GetString());
        Assert.Equal("Kiểm tra Tường & Cửa #1", root.GetProperty("suite").GetString());
        Assert.Equal("manual", root.GetProperty("trigger").GetString());
        Assert.Equal(Environment.MachineName, root.GetProperty("machine").GetString());
        Assert.Equal(Environment.UserName, root.GetProperty("user").GetString());
        Assert.True(root.TryGetProperty("revit", out _));
        Assert.True(root.TryGetProperty("addins", out var addins) && addins.GetArrayLength() == 0);
        Assert.True(root.TryGetProperty("gateway_version", out _));
        Assert.True(root.TryGetProperty("started_at", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ended_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("verdict").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("counts").ValueKind);

        // run-info.json for backwards compatibility
        var runInfoPath = Path.Combine(run.FolderPath, "run-info.json");
        Assert.True(File.Exists(runInfoPath), "run-info.json exists");
        using var infoDoc = JsonDocument.Parse(File.ReadAllText(runInfoPath));
        Assert.Equal(run.Id, infoDoc.RootElement.GetProperty("run_id").GetString());
        Assert.Equal("unit test", infoDoc.RootElement.GetProperty("purpose").GetString());
    }

    [Fact]
    public void CreateRun_SameSecondSameName_NeverReusesOrOverwritesExistingFolder()
    {
        var (store, _) = Create();
        var runs = Enumerable.Range(0, 5).Select(_ => store.CreateRun("collision-test")).ToArray();

        // All IDs and folder paths are distinct
        Assert.Equal(5, runs.Select(r => r.Id).Distinct().Count());
        Assert.Equal(5, runs.Select(r => r.FolderPath).Distinct().Count());
        Assert.All(runs, r => Assert.True(Directory.Exists(r.FolderPath)));
    }

    [Theory]
    [InlineData("Mô hình Kiểm tra #1", "mo-hinh-kiem-tra-1")]
    [InlineData("Đường Ống Gió & Cửa Gió", "duong-ong-gio-cua-gio")]
    [InlineData("Very Long Suite Name Exceeding Forty Characters In Total Length", "very-long-suite-name-exceeding-forty-cha")]
    [InlineData("   ### $$$ --- ", "run")]
    [InlineData(null, "run")]
    [InlineData("", "run")]
    public void Slug_ConformsToAsciiLowercaseMax40(string? input, string expectedSlug)
    {
        var slug = RevitAgent.Core.RunStore.RunStore.FormatSlug(input);
        Assert.Equal(expectedSlug, slug);
        Assert.True(slug.Length <= 40);
        Assert.Matches(@"^[a-z0-9-]+$", slug);
    }
}
