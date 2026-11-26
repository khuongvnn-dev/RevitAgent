using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Sandbox;

public class SandboxServiceTests
{
    private static (SandboxService Sandbox, AgentPaths Paths) Create()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        return (new SandboxService(paths, new GatewayPolicy()), paths);
    }

    private static string NewModel(string name = "Original Model.rvt", int bytes = 300_000)
    {
        var dir = TestGateway.NewTempDir("revitagent_src_");
        var path = Path.Combine(dir, name);
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void CreateRun_MakesDataEvidenceAndRunInfo_WithASafeSlug()
    {
        var (sandbox, paths) = Create();
        var run = sandbox.CreateRun("Kiểm tra Đơn vị #1", "unit test", "s1");

        Assert.Matches(@"^\d{8}-\d{6}-kiem-tra-don-vi-1$", run.RunId);
        Assert.StartsWith(paths.RunsDir, run.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(run.DataDir));
        Assert.True(Directory.Exists(run.EvidenceDir));
        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Path, SandboxService.RunInfoFile)));
        Assert.Equal(run.RunId, info.RootElement.GetProperty("run_id").GetString());
        Assert.Equal("unit test", info.RootElement.GetProperty("purpose").GetString());
        Assert.Equal("Kiểm tra Đơn vị #1", info.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void CreateRun_SameSecondSameName_GetsDistinctIds()
    {
        var (sandbox, _) = Create();
        var ids = Enumerable.Range(0, 3).Select(_ => sandbox.CreateRun("dup").RunId).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal("run", SandboxService.Slug("  ###  "));
    }

    [Fact]
    public void CopyModel_CopiesIntoTheRun_ProvesTheOriginalIsUntouched_AndHandlesCollisions()
    {
        var (sandbox, paths) = Create();
        var source = NewModel();
        var link = Path.Combine(Path.GetDirectoryName(source)!, "Link.rvt");
        File.WriteAllBytes(link, new byte[] { 1, 2, 3 });
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var originalWrite = File.GetLastWriteTimeUtc(source);

        var first = sandbox.CopyModel(source, null, new[] { link }, null, "s1", CancellationToken.None);

        Assert.True(first.RunCreated);
        Assert.True(first.OriginalUnchanged);
        Assert.Equal(Path.Combine(first.RunDir, "Original Model.rvt"), first.CopyPath);
        Assert.True(new GatewayPolicy().IsInSandbox(first.CopyPath, paths));
        Assert.Equal(2, first.Copies.Count);
        var c = first.Copies[0];
        Assert.Equal(originalHash, c.Sha256SourceBefore);
        Assert.Equal(originalHash, c.Sha256SourceAfter);
        Assert.Equal(originalHash, c.Sha256Copy);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(first.CopyPath));
        Assert.True(File.Exists(Path.Combine(first.RunDir, "Link.rvt")));

        // The original is byte-for-byte and timestamp-for-timestamp untouched.
        Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(source));

        // Second copy into the same run: no overwrite, a -2 suffix.
        var second = sandbox.CopyModel(source, first.RunId, null, null, "s1", CancellationToken.None);
        Assert.False(second.RunCreated);
        Assert.Equal(Path.Combine(first.RunDir, "Original Model-2.rvt"), second.CopyPath);
        Assert.Equal(originalHash, second.Copies[0].Sha256Copy);

        using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(first.RunDir, SandboxService.RunInfoFile)));
        Assert.Equal(3, info.RootElement.GetProperty("copies").GetArrayLength());
    }

    [Fact]
    public void CopyModel_ReadOnlySource_GivesAWritableCopy()
    {
        var (sandbox, _) = Create();
        var source = NewModel("ro.rvt", 1000);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        try
        {
            var result = sandbox.CopyModel(source, null, null, "ro", null, CancellationToken.None);
            Assert.Equal(0, (int)(File.GetAttributes(result.CopyPath) & FileAttributes.ReadOnly));
            Assert.NotEqual(0, (int)(File.GetAttributes(source) & FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\..\\evil")]
    [InlineData("../evil")]
    [InlineData("a\\b")]
    [InlineData("C:\\Windows")]
    [InlineData("x..y")]
    [InlineData(".hidden")]
    public void RunId_TraversalAndPaths_AreRejected(string runId)
    {
        var (sandbox, _) = Create();
        var ex = Assert.Throws<GatewayException>(() => sandbox.ResolveRunDir(runId));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex.Code);
        var source = NewModel("t.rvt", 10);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => sandbox.CopyModel(source, runId, null, null, null, CancellationToken.None)).Code);
    }

    [Fact]
    public void CopyModel_RejectsMissingFoldersAndNonRevitSources()
    {
        var (sandbox, _) = Create();
        var dir = TestGateway.NewTempDir("revitagent_src_");
        Assert.Equal(GatewayErrorCodes.NotFound, Assert.Throws<GatewayException>(() => sandbox.CopyModel(Path.Combine(dir, "missing.rvt"), null, null, null, null, CancellationToken.None)).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => sandbox.CopyModel(dir, null, null, null, null, CancellationToken.None)).Code);
        var txt = Path.Combine(dir, "notes.txt");
        File.WriteAllText(txt, "x");
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => sandbox.CopyModel(txt, null, null, null, null, CancellationToken.None)).Code);
        Assert.Equal(GatewayErrorCodes.NotFound, Assert.Throws<GatewayException>(() => sandbox.CopyModel(NewModel("ok.rvt", 10), "20990101-000000-none", null, null, null, CancellationToken.None)).Code);
        Assert.Equal(GatewayErrorCodes.NotFound, Assert.Throws<GatewayException>(() => sandbox.CopyModel(NewModel("ok.rvt", 10), null, new[] { Path.Combine(dir, "nolink.rvt") }, null, null, CancellationToken.None)).Code);
        Assert.Empty(sandbox.ListRuns(10)); // nothing was created by failed calls
    }

    [Fact]
    public void CopyModel_RefusesNonModelExtraFiles_AndTheGatewaysOwnData()
    {
        var (sandbox, paths) = Create();
        var model = NewModel("m.rvt", 10);
        var exe = Path.Combine(Path.GetDirectoryName(model)!, "tool.exe");
        File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => sandbox.CopyModel(model, null, new[] { exe }, null, null, CancellationToken.None)).Code);

        var secret = Path.Combine(paths.ConfigDir, "dialog-rules.json");
        File.WriteAllText(secret, "[]");
        Assert.Equal(GatewayErrorCodes.PermissionDenied, Assert.Throws<GatewayException>(() => sandbox.CopyModel(model, null, new[] { secret }, null, null, CancellationToken.None)).Code);
        var inHome = Path.Combine(paths.Root, "stolen.rvt");
        File.WriteAllBytes(inHome, new byte[] { 1 });
        Assert.Equal(GatewayErrorCodes.PermissionDenied, Assert.Throws<GatewayException>(() => sandbox.CopyModel(inHome, null, null, null, null, CancellationToken.None)).Code);
        Assert.Empty(sandbox.ListRuns(10));
    }

    [Fact]
    public void CopyModel_RefusesToWriteThroughAJunctionInsideRuns()
    {
        var (sandbox, paths) = Create();
        var outside = TestGateway.NewTempDir("revitagent_outside_");
        var link = Path.Combine(paths.RunsDir, "20990101-000000-link");
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mk.WaitForExit(15000);
        }

        Assert.True(Directory.Exists(link), "junction could not be created");
        try
        {
            var ex = Assert.Throws<GatewayException>(() => sandbox.CopyModel(NewModel("j.rvt", 10), "20990101-000000-link", null, null, null, CancellationToken.None));
            Assert.Equal(GatewayErrorCodes.SandboxViolation, ex.Code);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(link); // removes the junction only, not the target
        }
    }

    [Fact]
    public void ListRuns_ToleratesHandEditedRunInfo()
    {
        var (sandbox, _) = Create();
        var run = sandbox.CreateRun("edited");
        File.WriteAllText(Path.Combine(run.Path, SandboxService.RunInfoFile), """{ "name": 42, "created_at": true, "copies": [ { "copy_path": 7 } ] }""");
        var listed = Assert.Single(sandbox.ListRuns(10));
        Assert.Null(listed.Name);
        Assert.Empty(listed.Models);
    }

    [Fact]
    public void ListRuns_NewestFirst_WithLimitAndFilter()
    {
        var (sandbox, _) = Create();
        var a = sandbox.CreateRun("alpha");
        Thread.Sleep(30);
        var b = sandbox.CreateRun("beta");
        Thread.Sleep(30);
        var c = sandbox.CreateRun("gamma");
        sandbox.CopyModel(NewModel("m.rvt", 10), c.RunId, null, null, null, CancellationToken.None);

        var all = sandbox.ListRuns(10);
        Assert.Equal(new[] { c.RunId, b.RunId, a.RunId }, all.Select(r => r.RunId));
        Assert.Single(all[0].Models);
        Assert.Equal(2, sandbox.ListRuns(2).Count);
        Assert.Equal(b.RunId, Assert.Single(sandbox.ListRuns(10, "BETA")).RunId);
    }

    [Fact]
    public async Task SandboxTools_ThroughTheRegistry()
    {
        await using var sp = TestGateway.BuildProvider(out var paths);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var source = NewModel("Tool Model.rvt", 2048);

        var create = await registry.InvokeAsync("sandbox_create_run", JsonDocument.Parse("""{"name": "tool run"}""").RootElement, "s1", CancellationToken.None);
        Assert.False(create.IsError);
        var runId = create.Structured!.Value.GetProperty("run_id").GetString()!;

        var copyArgs = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["source_path"] = source, ["run_id"] = runId });
        var copy = await registry.InvokeAsync("sandbox_copy_model", copyArgs, "s1", CancellationToken.None);
        Assert.False(copy.IsError, ((TextContent)copy.Content[0]).Text);
        var copyPath = copy.Structured!.Value.GetProperty("copy_path").GetString()!;
        Assert.StartsWith(paths.RunsDir, copyPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(copy.Structured!.Value.GetProperty("original_unchanged").GetBoolean());
        Assert.Equal(runId, copy.Structured!.Value.GetProperty("run_id").GetString());
        var before = copy.Structured!.Value.GetProperty("source_sha256_before").GetString();
        Assert.Equal(64, before!.Length);
        Assert.Equal(before, copy.Structured!.Value.GetProperty("source_sha256_after").GetString());
        Assert.Equal(before, copy.Structured!.Value.GetProperty("copy_sha256").GetString());
        Assert.Equal(runId, create.Structured!.Value.GetProperty("run_id").GetString());
        Assert.True(Directory.Exists(create.Structured!.Value.GetProperty("path").GetString()));

        var list = await registry.InvokeAsync("sandbox_list_runs", JsonDocument.Parse("""{"limit": 5}""").RootElement, "s1", CancellationToken.None);
        Assert.Equal(runId, list.Structured!.Value.GetProperty("runs")[0].GetProperty("run_id").GetString());

        var evil = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["source_path"] = source, ["run_id"] = "..\\..\\outside" });
        Assert.Equal(GatewayErrorCodes.ValidationFailed, (await registry.InvokeAsync("sandbox_copy_model", evil, "s1", CancellationToken.None)).ErrorCode);
    }
}
