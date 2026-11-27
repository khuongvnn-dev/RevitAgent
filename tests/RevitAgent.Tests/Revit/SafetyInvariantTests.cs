using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Tests.Revit;

/// <summary>
/// Safety invariants that must hold against a prompt-injected agent: the user's own Revit is never killed or closed without
/// the owner, a gateway-launched Revit cannot save outside the sandbox, evidence is never overwritten. No Revit and no
/// window is involved: the only processes are console helpers started (and always stopped) by the test itself.
/// </summary>
public class SafetyInvariantTests
{
    private sealed class FakeApproval : IOwnerApproval
    {
        private readonly bool _approve;

        public FakeApproval(bool approve) => _approve = approve;

        public List<OwnerApprovalRequest> Asked { get; } = new();

        public Task<OwnerDecision> RequestAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(_approve ? OwnerDecision.Approve("test", "owner") : OwnerDecision.Deny("test", "owner", "denied by the owner"));
        }
    }

    /// <summary>A windowless console process named Revit.exe (a copy of ping.exe), so the foreign-close path runs without Revit.</summary>
    private sealed class FakeForeignRevit : IDisposable
    {
        public FakeForeignRevit()
        {
            var dir = TestGateway.NewTempDir("revitagent_fake_revit_");
            var exe = Path.Combine(dir, "Revit.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
            Process = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        }

        public Process Process { get; }

        public int Pid => Process.Id;

        public bool Alive => !Process.HasExited;

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                    Process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
            }

            Process.Dispose();
        }
    }

    private static ServiceProvider Gateway(IOwnerApproval approval, out AgentPaths paths)
    {
        paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        return new ServiceCollection()
            .AddSingleton(approval)
            .AddRevitAgentGateway(paths)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    // ------------------------------------------------------------------ B1: revit_close on the user's own Revit

    [Theory]
    [InlineData(false, null, false, false, GatewayErrorCodes.PermissionDenied)] // no force_foreign
    [InlineData(true, 1, false, false, GatewayErrorCodes.PermissionDenied)]     // confirm_pid != pid
    [InlineData(true, 4242, true, false, GatewayErrorCodes.PermissionDenied)]   // force_kill on a foreign Revit
    [InlineData(true, 4242, false, true, GatewayErrorCodes.PermissionDenied)]   // discard_changes on a foreign Revit
    public void ForeignClose_AgentArguments_AreNecessaryButNeverEnoughForKillOrDiscard(bool force, int? confirm, bool kill, bool discard, string code)
    {
        var request = new CloseRequest { Pid = 4242, ForceForeign = force, ConfirmPid = confirm, ForceKill = kill, DiscardChanges = discard };
        var ex = Assert.Throws<GatewayException>(() => RevitProcessService.ValidateForeignClose(request, "Revit"));
        Assert.Equal(code, ex.Code);
        if (kill || discard)
        {
            Assert.Contains("graceful WM_CLOSE only", ex.Message);
        }

        RevitProcessService.ValidateForeignClose(new CloseRequest { Pid = 4242, ForceForeign = true, ConfirmPid = 4242 }, "Revit");
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() =>
            RevitProcessService.ValidateForeignClose(new CloseRequest { Pid = 4242, ForceForeign = true, ConfirmPid = 4242 }, "notepad")).Code);
    }

    [Fact]
    public void ForeignClose_OwnerQuestion_NamesThePidTheDocumentAndTheRisk()
    {
        var q = RevitProcessService.ForeignCloseApproval(new CloseRequest { Pid = 4242, SessionId = "agent-7" }, 2024, "Autodesk Revit 2024.2 - [Tower A - Floor Plan: Level 1]");
        Assert.Equal("revit_close", q.Tool);
        var text = q.Describe();
        Assert.Contains("'agent-7'", text);
        Assert.Contains("pid 4242", text);
        Assert.Contains("'Tower A'", text);
        Assert.Contains("Unsaved work", text);
        Assert.Contains("2024", text);
    }

    [Fact]
    public async Task ForeignClose_WithoutTheOwner_IsDenied_AndNothingIsSent()
    {
        using var revit = new FakeForeignRevit();
        var approval = new FakeApproval(approve: false);
        await using var sp = Gateway(approval, out _);
        var registry = sp.GetRequiredService<ToolRegistry>();

        var r = await registry.InvokeAsync("revit_close", System.Text.Json.JsonSerializer.SerializeToElement(new { pid = revit.Pid, force_foreign = true, confirm_pid = revit.Pid, grace_sec = 2 }), "agent-1", CancellationToken.None);

        Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
        Assert.Contains("owner's approval", ((TextContent)r.Content[0]).Text);
        var asked = Assert.Single(approval.Asked);
        Assert.Contains($"pid {revit.Pid}", asked.Describe());
        Assert.Equal("agent-1", asked.SessionId);
        Assert.True(revit.Alive);
    }

    [Fact]
    public async Task ForeignClose_NoApprovalChannel_IsDenied()
    {
        // The real service with no dashboard and no Telegram: default deny.
        using var revit = new FakeForeignRevit();
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        await using var sp = GatewayServices.BuildGateway(paths);
        var ex = await Assert.ThrowsAsync<GatewayException>(() => sp.GetRequiredService<RevitProcessService>().CloseAsync(
            new CloseRequest { Pid = revit.Pid, ForceForeign = true, ConfirmPid = revit.Pid, Grace = TimeSpan.FromSeconds(2) }, CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        Assert.Contains("no approval channel", ex.Message);
        Assert.True(revit.Alive);
    }

    [Fact]
    public async Task ForeignClose_Approved_IsGracefulOnly_AndNeverKills()
    {
        using var revit = new FakeForeignRevit(); // no window: a graceful close cannot succeed
        var approval = new FakeApproval(approve: true);
        await using var sp = Gateway(approval, out _);
        var processes = sp.GetRequiredService<RevitProcessService>();

        var ex = await Assert.ThrowsAsync<GatewayException>(() => processes.CloseAsync(
            new CloseRequest { Pid = revit.Pid, ForceForeign = true, ConfirmPid = revit.Pid, Grace = TimeSpan.FromSeconds(2) }, CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.TargetNotReady, ex.Code);
        Assert.Contains("never kills", ex.Message);
        Assert.True(revit.Alive);

        // force_kill is refused before the owner is even asked.
        var kill = await Assert.ThrowsAsync<GatewayException>(() => processes.CloseAsync(
            new CloseRequest { Pid = revit.Pid, ForceForeign = true, ConfirmPid = revit.Pid, ForceKill = true }, CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, kill.Code);
        Assert.Single(approval.Asked);
        Assert.True(revit.Alive);
    }

    [Fact]
    public void RevitClose_WaitsLongerOnlyForAForeignClose()
    {
        var tool = new RevitAgent.Core.Revit.Tools.RevitCloseTool(null!);
        var plain = new ToolCall("revit_close", System.Text.Json.JsonSerializer.SerializeToElement(new { pid = 1 }), "s");
        var foreign = new ToolCall("revit_close", System.Text.Json.JsonSerializer.SerializeToElement(new { pid = 1, force_foreign = true }), "s");
        Assert.Equal(tool.Timeout, tool.TimeoutFor(plain));
        Assert.True(tool.TimeoutFor(foreign) >= tool.Timeout + OwnerApprovalRequest.DefaultTimeout);
        Assert.Contains("foreign Revit: owner approval, graceful only", tool.Description);
    }

    // ------------------------------------------------------------------ B1: scenario cleanup never kills after a refusal

    [Fact]
    public void ScenarioCleanup_KillsOnlyAnOwnedProcessWithTheSameStartTime_AndNeverAfterARefusal()
    {
        var start = new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc);
        var owned = new LaunchedProcess { Pid = 10, StartTimeUtc = start };
        Assert.True(ScenarioRunner.MayKillAfterFailedClose(GatewayErrorCodes.Timeout, owned, start));
        Assert.True(ScenarioRunner.MayKillAfterFailedClose(GatewayErrorCodes.ModalBlocked, owned, start.AddMilliseconds(400)));

        Assert.False(ScenarioRunner.MayKillAfterFailedClose(GatewayErrorCodes.PermissionDenied, owned, start)); // the policy said no
        Assert.False(ScenarioRunner.MayKillAfterFailedClose(GatewayErrorCodes.Timeout, null, start));           // not (or no longer) owned
        Assert.False(ScenarioRunner.MayKillAfterFailedClose(GatewayErrorCodes.Timeout, owned, null));           // launch not recorded
        Assert.False(ScenarioRunner.MayKillAfterFailedClose(GatewayErrorCodes.Timeout, owned, start.AddMinutes(3))); // pid reused
    }

    // ------------------------------------------------------------------ I3: save guard environment of a launched Revit

    [Fact]
    public void LaunchEnvironment_MarksTheProcess_AndPassesTheSandboxRootsOnlyWhileEnforced()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        using var sp = GatewayServices.BuildGateway(paths);
        var processes = sp.GetRequiredService<RevitProcessService>();
        var policy = sp.GetRequiredService<GatewayPolicy>();

        var on = processes.LaunchEnvironment();
        Assert.Equal("1", on[SandboxSaveGuard.LaunchedVariable]);
        var guard = SandboxSaveGuard.FromEnvironment(n => on.TryGetValue(n, out var v) ? v : null)!;
        Assert.Contains(Path.GetFullPath(paths.RunsDir).TrimEnd('\\'), guard.Roots, StringComparer.OrdinalIgnoreCase);

        policy.EnforceSandbox = false;
        var off = processes.LaunchEnvironment();
        Assert.Equal("1", off[SandboxSaveGuard.LaunchedVariable]);
        Assert.Null(off[SandboxSaveGuard.SandboxRootVariable]);
        Assert.Null(SandboxSaveGuard.FromEnvironment(n => off.TryGetValue(n, out var v) ? v : null));
    }

    [Fact]
    public void StartWithEnvironment_GivesTheChildTheVariables_AndRestoresThisProcess()
    {
        var dir = TestGateway.NewTempDir("revitagent_env_");
        var output = Path.Combine(dir, "env.txt");
        var psi = new ProcessStartInfo("cmd.exe", $"/c echo %{SandboxSaveGuard.LaunchedVariable}%^|%{SandboxSaveGuard.SandboxRootVariable}%>\"{output}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        var env = new Dictionary<string, string?> { [SandboxSaveGuard.LaunchedVariable] = "1", [SandboxSaveGuard.SandboxRootVariable] = @"C:\sandbox\runs" };

        using (var p = RevitProcessService.StartWithEnvironment(psi, env)!)
        {
            Assert.True(p.WaitForExit(15000));
        }

        Assert.Equal(@"1|C:\sandbox\runs", File.ReadAllText(output).Trim());
        Assert.Null(Environment.GetEnvironmentVariable(SandboxSaveGuard.LaunchedVariable));
        Assert.Null(Environment.GetEnvironmentVariable(SandboxSaveGuard.SandboxRootVariable));
    }

    [Fact]
    public void SaveGuard_OnlyInAGatewayLaunchedRevit_AndOnlyInsideTheRoots()
    {
        Assert.Null(SandboxSaveGuard.FromEnvironment(_ => null)); // the user's own Revit: no guard at all
        Assert.Null(SandboxSaveGuard.FromEnvironment(n => n == SandboxSaveGuard.SandboxRootVariable ? @"C:\runs" : null));
        Assert.Null(SandboxSaveGuard.FromEnvironment(n => n == SandboxSaveGuard.LaunchedVariable ? "1" : null));

        var guard = SandboxSaveGuard.FromEnvironment(n => n == SandboxSaveGuard.LaunchedVariable ? "1" : @"C:\Data\runs\;D:\Suites")!;
        Assert.Null(guard.SaveRefusal(@"C:\Data\runs\r1\copy.rvt"));
        Assert.Null(guard.SaveRefusal(@"d:\suites\a\b.rvt"));
        Assert.NotNull(guard.SaveRefusal(@"C:\Projects\Tower\central.rvt"));
        Assert.NotNull(guard.SaveRefusal(@"C:\Data\runs\..\original.rvt"));
        Assert.NotNull(guard.SaveRefusal(@"C:\Data\runsX\copy.rvt"));
        Assert.NotNull(guard.SaveRefusal(@"\\server\share\central.rvt"));
        Assert.NotNull(guard.SaveRefusal("BIM 360://Project/Model.rvt"));
        Assert.NotNull(guard.SaveRefusal("relative.rvt"));
        Assert.NotNull(guard.SaveRefusal(null));

        // Enforcement asked for, but no usable root: every save is outside (fail closed).
        var broken = SandboxSaveGuard.FromEnvironment(n => n == SandboxSaveGuard.LaunchedVariable ? "1" : @"relative;\\server\share;C:\")!;
        Assert.Empty(broken.Roots);
        Assert.NotNull(broken.SaveRefusal(@"C:\Data\runs\r1\copy.rvt"));
    }

    // ------------------------------------------------------------------ I4: evidence is never overwritten

    [Fact]
    public void ExplicitImagePaths_MustBeNewPngFiles()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var runDir = Path.Combine(paths.RunsDir, "r1");
        Directory.CreateDirectory(runDir);
        var report = Path.Combine(runDir, "report.json");
        File.WriteAllText(report, "{}");
        var shot = Path.Combine(runDir, "shot.png");
        File.WriteAllBytes(shot, new byte[] { 1 });

        Assert.Equal(GatewayErrorCodes.Conflict, Assert.Throws<GatewayException>(() => UiPaths.NewImageFile(paths, shot, paths.CapturesDir)).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => UiPaths.NewImageFile(paths, report, paths.CapturesDir)).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => UiPaths.NewImageFile(paths, Path.Combine(runDir, "new.json"), paths.CapturesDir)).Code);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, Assert.Throws<GatewayException>(() => UiPaths.NewImageFile(paths, @"C:\Windows\x.png", paths.CapturesDir)).Code);
        Assert.Equal(Path.Combine(runDir, "fresh.png"), UiPaths.NewImageFile(paths, Path.Combine(runDir, "fresh"), paths.CapturesDir));
        Assert.Equal(Path.Combine(runDir, "fresh.PNG"), UiPaths.NewImageFile(paths, Path.Combine(runDir, "fresh.PNG"), paths.CapturesDir));

        // view_export_image: the same rule, reported as a sandbox / conflict error before the Bridge is called.
        Assert.Equal(GatewayErrorCodes.Conflict, Assert.Throws<GatewayException>(() => ViewExportImageSupport.ResolveSavePath(paths, shot)).Code);
        Assert.Equal(GatewayErrorCodes.SandboxViolation, Assert.Throws<GatewayException>(() => ViewExportImageSupport.ResolveSavePath(paths, @"C:\Windows\x.png")).Code);

        Assert.Equal("{}", File.ReadAllText(report));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(shot));
    }

    [Fact]
    public void WriteNew_NeverOverwrites()
    {
        var dir = TestGateway.NewTempDir("revitagent_write_");
        var file = Path.Combine(dir, "sub", "a.png");
        UiPaths.WriteNew(file, new byte[] { 1, 2 });
        Assert.Equal(GatewayErrorCodes.Conflict, Assert.Throws<GatewayException>(() => UiPaths.WriteNew(file, new byte[] { 9 })).Code);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(file));
    }
}
