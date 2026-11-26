using System.IO;
using System.Text.Json;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Sandbox;

namespace RevitAgent.Tests.Sandbox;

public class EvidenceServiceTests
{
    private static (SandboxService Sandbox, AgentPaths Paths) Create()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        return (new SandboxService(paths, new GatewayPolicy(), evidence: new EvidenceService()), paths);
    }

    private static JsonElement[] ReadIndex(string evidenceDir) =>
        File.ReadAllLines(Path.Combine(evidenceDir, EvidenceService.IndexFile))
            .Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToArray();

    [Fact]
    public void CreateRun_MakesItTheCurrentRunOfThatSessionOnly()
    {
        var (sandbox, _) = Create();
        var run = sandbox.CreateRun("a", null, "s1");

        Assert.Equal(run.Path, sandbox.Evidence.SessionRunDir("s1"));
        Assert.Null(sandbox.Evidence.SessionRunDir("s2"));
        Assert.Equal(run.EvidenceDir, sandbox.Evidence.EvidenceDirFor("s1"));
        Assert.Null(sandbox.Evidence.EvidenceDirFor("s2"));
    }

    [Fact]
    public void CopyModel_CreatesOrUsesARun_AndSetsItAsCurrent()
    {
        var (sandbox, _) = Create();
        var src = Path.Combine(TestGateway.NewTempDir("revitagent_src_"), "m.rvt");
        File.WriteAllBytes(src, new byte[1000]);

        var created = sandbox.CopyModel(src, null, null, null, "s1", CancellationToken.None);
        Assert.Equal(created.RunDir, sandbox.Evidence.SessionRunDir("s1"));

        var reused = sandbox.CopyModel(src, created.RunId, null, null, "s2", CancellationToken.None);
        Assert.Equal(created.RunDir, reused.RunDir);
        Assert.Equal(created.RunDir, sandbox.Evidence.SessionRunDir("s2"));
    }

    [Fact]
    public void NoteModelOpened_MapsSessionAndPid_ForARunCopyOnly()
    {
        var (sandbox, _) = Create();
        var run = sandbox.CreateRun("a", null, "other");
        var model = Path.Combine(run.Path, "model", "m.rvt");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, new byte[10]);

        sandbox.Evidence.NoteModelOpened("s9", 4242, model);
        Assert.Equal(run.Path, sandbox.Evidence.SessionRunDir("s9"));
        Assert.Equal(run.Path, sandbox.Evidence.PidRunDir(4242));

        sandbox.Evidence.NoteModelOpened("s8", 77, Path.Combine(TestGateway.NewTempDir("revitagent_src_"), "x.rvt"));
        Assert.Null(sandbox.Evidence.SessionRunDir("s8"));
        Assert.Null(sandbox.Evidence.PidRunDir(77));
    }

    [Fact]
    public void GatewayCapture_UsesThePidRun_WhenNoSessionIsKnown()
    {
        var (sandbox, _) = Create();
        var run = sandbox.CreateRun("a", null, "s1");
        sandbox.Evidence.SetPidRun(100, run.Path);

        Assert.Equal(run.EvidenceDir, sandbox.Evidence.EvidenceDirFor(null, 100));
        Assert.Null(sandbox.Evidence.EvidenceDirFor(null, 101));
        // The session's run wins over the pid's.
        var other = sandbox.CreateRun("b", null, "s2");
        Assert.Equal(other.EvidenceDir, sandbox.Evidence.EvidenceDirFor("s2", 100));
    }

    [Fact]
    public void DefaultPath_IsInTheRunEvidence_OrTheFallbackFolder_WithASortableName()
    {
        var (sandbox, paths) = Create();
        var without = sandbox.Evidence.DefaultPath("s1", null, "ui_screenshot", null, ".png", paths.CapturesDir);
        Assert.Equal(paths.CapturesDir, Path.GetDirectoryName(without));

        var run = sandbox.CreateRun("a", null, "s1");
        var with = sandbox.Evidence.DefaultPath("s1", null, "ui_screenshot", "Main Window", ".png", paths.CapturesDir);
        Assert.Equal(run.EvidenceDir, Path.GetDirectoryName(with));
        Assert.Matches(@"^\d{6}-\d{3}_ui_screenshot_Main-Window\.png$", Path.GetFileName(with));
    }

    [Fact]
    public void ViewExportImage_DefaultPathFollowsTheCurrentRun_AndExplicitPathStillValidated()
    {
        var (sandbox, paths) = Create();
        var before = ViewExportImageSupport.ResolveSavePath(paths, null, sandbox.Evidence, "s1", 5);
        Assert.Equal(paths.CapturesDir, Path.GetDirectoryName(before));

        var run = sandbox.CreateRun("a", null, "s1");
        var after = ViewExportImageSupport.ResolveSavePath(paths, null, sandbox.Evidence, "s1", 5);
        Assert.Equal(run.EvidenceDir, Path.GetDirectoryName(after));

        var explicitPath = Path.Combine(run.EvidenceDir, "mine.png");
        Assert.Equal(explicitPath, ViewExportImageSupport.ResolveSavePath(paths, explicitPath, sandbox.Evidence, "s1", 5));
        Assert.ThrowsAny<Exception>(() => ViewExportImageSupport.ResolveSavePath(paths, Path.Combine(Path.GetTempPath(), "outside.png"), sandbox.Evidence, "s1", 5));
    }

    [Fact]
    public void Record_WritesOneLinePerFile_ForDefaultAndExplicitPaths_AndSkipsOtherFolders()
    {
        var (sandbox, paths) = Create();
        var run = sandbox.CreateRun("a", null, "s1");
        var ev = sandbox.Evidence;

        var def = ev.DefaultPath("s1", 12, "ui_screenshot", null, ".png", paths.CapturesDir);
        File.WriteAllBytes(def, new byte[123]);
        ev.Record(def, "screenshot", "ui_screenshot", "s1", 12, "call1");

        var explicitFile = Path.Combine(run.EvidenceDir, "step2.png");
        File.WriteAllBytes(explicitFile, new byte[7]);
        ev.Record(explicitFile, "screenshot", "ui_screenshot", "s1", 12, "call2");

        var nested = Path.Combine(run.EvidenceDir, "ui-scripts", "x", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "{}");
        ev.Record(nested, "report", "ui_run_script", "s1", 12);

        var captured = Path.Combine(paths.CapturesDir, "loose.png");
        File.WriteAllBytes(captured, new byte[3]);
        ev.Record(captured, "screenshot", "ui_screenshot", "s1", 12);

        var lines = ReadIndex(run.EvidenceDir);
        Assert.Equal(3, lines.Length);
        Assert.Equal(def, lines[0].GetProperty("file").GetString());
        Assert.Equal(123, lines[0].GetProperty("bytes").GetInt64());
        Assert.Equal("ui_screenshot", lines[0].GetProperty("tool").GetString());
        Assert.Equal("s1", lines[0].GetProperty("session_id").GetString());
        Assert.Equal(12, lines[0].GetProperty("pid").GetInt32());
        Assert.Equal("call1", lines[0].GetProperty("call_id").GetString());
        Assert.Equal("screenshot", lines[0].GetProperty("kind").GetString());
        Assert.True(lines[0].TryGetProperty("at", out _));
        Assert.Equal(explicitFile, lines[1].GetProperty("file").GetString());
        Assert.Equal("report", lines[2].GetProperty("kind").GetString());
        Assert.False(File.Exists(Path.Combine(paths.CapturesDir, EvidenceService.IndexFile)));
    }

    [Fact]
    public void Record_ConcurrentAppends_KeepEveryLineIntact()
    {
        var (sandbox, _) = Create();
        var run = sandbox.CreateRun("a", null, "s1");
        var files = Enumerable.Range(0, 60).Select(i => Path.Combine(run.EvidenceDir, $"f{i}.png")).ToArray();
        foreach (var f in files)
        {
            File.WriteAllBytes(f, new byte[5]);
        }

        // Two services (as two gateway processes would) append to the same index.
        var other = new EvidenceService();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 12 }, f =>
            (Array.IndexOf(files, f) % 2 == 0 ? sandbox.Evidence : other).Record(f, "screenshot", "t", "s1"));

        var lines = ReadIndex(run.EvidenceDir);
        Assert.Equal(60, lines.Length);
        Assert.Equal(60, lines.Select(l => l.GetProperty("file").GetString()).Distinct().Count());
    }
}
