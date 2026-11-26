using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.RunStore.Tools;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Tools;
using Xunit;

namespace RevitAgent.Tests.RunStore;

public class RunStoreIndexAndListTests
{
    private static (RevitAgent.Core.RunStore.RunStore Store, AgentPaths Paths) Create()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var store = new RevitAgent.Core.RunStore.RunStore(paths, new GatewayPolicy());
        return (store, paths);
    }

    [Fact]
    public void FinishRun_UpdatesRunJson_AndAppendsLineToIndexJsonl()
    {
        var (store, _) = Create();
        var run = store.CreateRun("doors-audit", "manual", "test finish");

        // Simulate reports
        File.WriteAllText(Path.Combine(run.FolderPath, "report.html"), "<html><body>Report</body></html>");
        File.WriteAllText(Path.Combine(run.FolderPath, "report.md"), "# Report");

        var ended = run.StartedAt.AddSeconds(15);
        store.FinishRun(run.FolderPath, new RunFinishInfo
        {
            EndedAt = ended,
            Verdict = "PASS",
            Pid = 1234,
            RevitYear = 2024,
            Counts = new { steps = new { total = 3, succeeded = 3 }, rules = new { total = 2, pass = 2 } },
        });

        // 1. run.json updated
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.FolderPath, RevitAgent.Core.RunStore.RunStore.RunJsonFile))))
        {
            var root = doc.RootElement;
            Assert.Equal("PASS", root.GetProperty("verdict").GetString());
            Assert.True(root.TryGetProperty("ended_at", out var endProp) && endProp.GetString() != null);
            Assert.Equal(2024, root.GetProperty("revit").GetProperty("year").GetInt32());
            Assert.Equal(3, root.GetProperty("counts").GetProperty("steps").GetProperty("total").GetInt32());
        }

        // 2. index.jsonl appended
        Assert.True(File.Exists(store.IndexFilePath));
        var lines = File.ReadAllLines(store.IndexFilePath);
        Assert.Single(lines);
        using (var indexDoc = JsonDocument.Parse(lines[0]))
        {
            var root = indexDoc.RootElement;
            Assert.Equal(run.Id, root.GetProperty("id").GetString());
            Assert.Equal("doors-audit", root.GetProperty("suite").GetString());
            Assert.Equal("PASS", root.GetProperty("verdict").GetString());
            Assert.Contains(run.Id, root.GetProperty("run_dir").GetString()!);
            Assert.Contains("report.html", root.GetProperty("report_html").GetString()!);
            Assert.Equal(2024, root.GetProperty("revit_version").GetInt32());
        }
    }

    [Fact]
    public void IndexAppend_ToleratesConcurrentWriters()
    {
        var (store, _) = Create();
        var threadCount = 10;
        var runsPerThread = 5;

        Parallel.For(0, threadCount, t =>
        {
            for (var i = 0; i < runsPerThread; i++)
            {
                var run = store.CreateRun($"parallel-{t}-{i}");
                store.FinishRun(run.FolderPath, new RunFinishInfo
                {
                    EndedAt = DateTimeOffset.Now,
                    Verdict = "PASS",
                });
            }
        });

        var lines = File.ReadAllLines(store.IndexFilePath);
        Assert.Equal(threadCount * runsPerThread, lines.Length);
        Assert.All(lines, l =>
        {
            using var doc = JsonDocument.Parse(l);
            Assert.Equal("PASS", doc.RootElement.GetProperty("verdict").GetString());
        });
    }

    [Fact]
    public async Task RunsListTool_FiltersAndOrdersNewestFirst()
    {
        var (store, _) = Create();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-20);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-10);

        var r1 = store.CreateRun("wall-check", "manual");
        store.FinishRun(r1.FolderPath, new RunFinishInfo { EndedAt = t0.AddSeconds(10), Verdict = "PASS" });

        var r2 = store.CreateRun("window-check", "schedule");
        store.FinishRun(r2.FolderPath, new RunFinishInfo { EndedAt = t1.AddSeconds(10), Verdict = "FAIL" });

        var r3 = store.CreateRun("wall-inventory", "agent");
        store.FinishRun(r3.FolderPath, new RunFinishInfo { EndedAt = t2.AddSeconds(10), Verdict = "PASS" });

        var listTool = new RunsListTool(store);

        // 1. All runs (newest first: r3, r2, r1)
        var allRes = await listTool.ExecuteAsync(new ToolCall("runs_list", JsonSerializer.SerializeToElement(new { }), "s"), CancellationToken.None);
        var allRuns = allRes.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(3, allRuns.Length);
        Assert.Equal(r3.Id, allRuns[0].GetProperty("id").GetString());
        Assert.Equal(r2.Id, allRuns[1].GetProperty("id").GetString());
        Assert.Equal(r1.Id, allRuns[2].GetProperty("id").GetString());

        // 2. Filter by suite substring "wall"
        var wallRes = await listTool.ExecuteAsync(new ToolCall("runs_list", JsonSerializer.SerializeToElement(new { suite = "wall" }), "s"), CancellationToken.None);
        var wallRuns = wallRes.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Equal(2, wallRuns.Length);
        Assert.Contains(wallRuns, r => r.GetProperty("id").GetString() == r1.Id);
        Assert.Contains(wallRuns, r => r.GetProperty("id").GetString() == r3.Id);

        // 3. Filter by verdict "FAIL"
        var failRes = await listTool.ExecuteAsync(new ToolCall("runs_list", JsonSerializer.SerializeToElement(new { verdict = "FAIL" }), "s"), CancellationToken.None);
        var failRuns = failRes.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Single(failRuns);
        Assert.Equal(r2.Id, failRuns[0].GetProperty("id").GetString());

        // 4. Limit = 1
        var limitRes = await listTool.ExecuteAsync(new ToolCall("runs_list", JsonSerializer.SerializeToElement(new { limit = 1 }), "s"), CancellationToken.None);
        var limitRuns = limitRes.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Single(limitRuns);
        Assert.Equal(r3.Id, limitRuns[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task CrashSafety_UnindexedAndInFlightRuns_AreDiscoveredAndListed()
    {
        var (store, _) = Create();

        // Run 1: finished and indexed
        var r1 = store.CreateRun("normal-run");
        store.FinishRun(r1.FolderPath, new RunFinishInfo { EndedAt = DateTimeOffset.Now, Verdict = "PASS" });

        // Run 2: in-flight / crashed (never finished, no index line)
        var r2 = store.CreateRun("crashed-run");

        var listTool = new RunsListTool(store);
        var res = await listTool.ExecuteAsync(new ToolCall("runs_list", JsonSerializer.SerializeToElement(new { }), "s"), CancellationToken.None);
        var runs = res.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Equal(2, runs.Length);
        var crashedEntry = runs.Single(r => r.GetProperty("id").GetString() == r2.Id);
        Assert.Equal("RUNNING", crashedEntry.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task OldLayoutCompatibility_DiscoversRunsDirectlyUnderRoot()
    {
        var (store, paths) = Create();

        // Create an old-layout folder directly under root: <root>\20261120-031500-old-test\
        var oldRunId = "20261120-031500-old-test";
        var oldDir = Path.Combine(store.RootDir, oldRunId);
        Directory.CreateDirectory(oldDir);
        var oldInfo = new JsonObject
        {
            ["run_id"] = oldRunId,
            ["name"] = "legacy-check",
            ["runner"] = "scenario_run",
            ["suite"] = "legacy-check",
            ["verdict"] = "PASS",
            ["created_at"] = DateTimeOffset.UtcNow.AddDays(-5).ToString("O"),
            ["started"] = DateTimeOffset.UtcNow.AddDays(-5).ToString("O"),
            ["ended"] = DateTimeOffset.UtcNow.AddDays(-5).AddMinutes(2).ToString("O"),
        };
        File.WriteAllText(Path.Combine(oldDir, "run-info.json"), oldInfo.ToJsonString());

        // 1. runs_list discovers it
        var listTool = new RunsListTool(store);
        var res = await listTool.ExecuteAsync(new ToolCall("runs_list", JsonSerializer.SerializeToElement(new { }), "s"), CancellationToken.None);
        var runs = res.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Contains(runs, r => r.GetProperty("id").GetString() == oldRunId && r.GetProperty("verdict").GetString() == "PASS");

        // 2. scenario_list_runs also discovers it
        var scenarioListTool = new ScenarioListRunsTool(paths, store);
        var sRes = await scenarioListTool.ExecuteAsync(new ToolCall("scenario_list_runs", JsonSerializer.SerializeToElement(new { scenario = "legacy" }), "s"), CancellationToken.None);
        var sRuns = sRes.Structured!.Value.GetProperty("runs").EnumerateArray().ToArray();
        Assert.Single(sRuns);
        Assert.Equal(oldRunId, sRuns[0].GetProperty("run_id").GetString());

        // 3. runs_get retrieves it
        var getTool = new RunsGetTool(store);
        var gRes = await getTool.ExecuteAsync(new ToolCall("runs_get", JsonSerializer.SerializeToElement(new { run_id = oldRunId }), "s"), CancellationToken.None);
        Assert.Equal(oldRunId, gRes.Structured!.Value.GetProperty("run_id").GetString());
        Assert.Equal(oldDir, gRes.Structured!.Value.GetProperty("run_dir").GetString());
    }

    [Fact]
    public async Task RunsGetTool_ReturnsRunJsonAndFilesWithSizes()
    {
        var (store, _) = Create();
        var run = store.CreateRun("inspect-me");
        File.WriteAllText(Path.Combine(run.FolderPath, "sample.txt"), "hello world 123");
        File.WriteAllBytes(Path.Combine(run.FolderPath, "data", "out.bin"), new byte[] { 1, 2, 3, 4, 5 });

        var getTool = new RunsGetTool(store);
        var res = await getTool.ExecuteAsync(new ToolCall("runs_get", JsonSerializer.SerializeToElement(new { run_id = run.Id }), "s"), CancellationToken.None);

        Assert.Equal(run.Id, res.Structured!.Value.GetProperty("run_id").GetString());
        Assert.Equal(run.FolderPath, res.Structured!.Value.GetProperty("run_dir").GetString());
        Assert.Equal(run.Id, res.Structured!.Value.GetProperty("run").GetProperty("id").GetString());

        var files = res.Structured!.Value.GetProperty("files").EnumerateArray().ToArray();
        Assert.Contains(files, f => f.GetProperty("path").GetString() == "sample.txt" && f.GetProperty("size").GetInt64() == 15);
        Assert.Contains(files, f => f.GetProperty("path").GetString() == Path.Combine("data", "out.bin") && f.GetProperty("size").GetInt64() == 5);
    }
}
