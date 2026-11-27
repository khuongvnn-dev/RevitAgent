using System.IO;
using System.Collections.Concurrent;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Sandbox;

namespace RevitAgent.Tests.Sandbox;

public class EvidenceReservationTests
{
    [Fact]
    public void Reserve_SameNameTwice_GivesDistinctExistingFiles()
    {
        var dir = TestGateway.NewTempDir("revitagent_resv_");
        var p = Path.Combine(dir, "a.png");
        var first = EvidenceService.Reserve(p);
        var second = EvidenceService.Reserve(p);
        var third = EvidenceService.Reserve(p);
        Assert.Equal(p, first);
        Assert.EndsWith("a-2.png", second);
        Assert.EndsWith("a-3.png", third);
        Assert.True(File.Exists(second) && File.Exists(third));
    }

    [Fact]
    public void Reserve_ParallelCallers_NeverShareAName()
    {
        var dir = TestGateway.NewTempDir("revitagent_resv_");
        var p = Path.Combine(dir, "x.png");
        var got = new ConcurrentBag<string>();
        Parallel.For(0, 40, _ => got.Add(EvidenceService.Reserve(p)));
        Assert.Equal(40, got.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void RecordDirectory_CalledTwice_DoesNotDuplicateIndexLines()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var sandbox = new SandboxService(paths, new GatewayPolicy(), evidence: new EvidenceService());
        var run = sandbox.CreateRun("a", null, "s1");
        var sub = Path.Combine(run.EvidenceDir, "script");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "report.json"), "{}");
        File.WriteAllText(Path.Combine(sub, "report.md"), "x");

        sandbox.Evidence.RecordDirectory(sub, "t", "s1");
        sandbox.Evidence.RecordDirectory(sub, "t", "s1");
        var lines = File.ReadAllLines(Path.Combine(run.EvidenceDir, EvidenceService.IndexFile)).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
    }
}
