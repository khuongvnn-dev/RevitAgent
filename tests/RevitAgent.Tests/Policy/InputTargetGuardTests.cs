using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;
using RevitAgent.Tests.Bridge;

namespace RevitAgent.Tests.Policy;

/// <summary>
/// The central check on the process that really receives UI input (after refs / selectors / the current window resolved).
/// Fake processes only: nothing is sent to any window.
/// </summary>
public sealed class InputTargetGuardTests
{
    private const int Self = 50;
    private const int Host = 60;
    private const int Shim = 61;
    private const int SameFolder = 62;
    private const int UiTestApp = 70;
    private const int Notepad = 80;
    private const int Owned = 100;
    private const int Foreign = 200;
    private const string GatewayDir = @"C:\Program Files\RevitAgent";

    private sealed class Owners : IOwnedProcessLookup
    {
        public bool IsOwned(int pid) => pid == Owned;
    }

    private sealed class Rig
    {
        public Rig()
        {
            Probe = new FakeRevitProbe()
                .Add(Self, "testhost").Add(Host, "RevitAgent.Host").Add(Shim, "RevitAgent.Shim")
                .Add(SameFolder, "helper", exePath: GatewayDir + @"\helper.exe")
                .Add(UiTestApp, "RevitAgent.UiTestApp").Add(Notepad, "notepad").Add(Owned).Add(Foreign);
            var probe = Probe;
            Grants = new ActionGrantStore(TimeProvider.System, pid => probe.Get(pid)?.StartTime);
            Guard = new InputTargetGuard(Policy, new ActionGuard(Grants, Probe, new Owners(), new SystemWindowProcessResolver()), Probe, Self, GatewayDir);
        }

        public FakeRevitProbe Probe { get; }

        public GatewayPolicy Policy { get; } = new();

        public ActionGrantStore Grants { get; }

        public InputTargetGuard Guard { get; }

        public string Refused(int pid) => Assert.Throws<GatewayException>(() => Guard.EnsureAllowed(pid, "ui_click")) is { Code: GatewayErrorCodes.PermissionDenied } ex
            ? ex.Message
            : throw new Xunit.Sdk.XunitException($"pid {pid}: expected PERMISSION_DENIED");
    }

    [Theory]
    [InlineData(Self)]
    [InlineData(Host)]
    [InlineData(Shim)]
    [InlineData(SameFolder)]
    public void TheGatewayItself_IsRefused_EvenWhenTheOwnerListsIt(int pid)
    {
        var rig = new Rig();
        Assert.Contains("RevitAgent gateway itself", rig.Refused(pid));

        rig.Policy.SetAllowedInputProcesses(new[] { "RevitAgent.Host", "RevitAgent.Shim.exe", "testhost", "helper" });
        Assert.Contains("RevitAgent gateway itself", rig.Refused(pid));
    }

    [Fact]
    public void NonRevitProcess_IsRefused_UnlessTheOwnerListedIt()
    {
        var rig = new Rig();
        Assert.Contains("only goes to Revit", rig.Refused(Notepad));
        Assert.Contains("RevitAgent gateway itself", rig.Refused(UiTestApp)); // RevitAgent.* is the gateway unless listed

        rig.Policy.SetAllowedInputProcesses(new[] { " notepad.EXE ", "RevitAgent.UiTestApp" });
        Assert.False(rig.Guard.EnsureAllowed(Notepad, "ui_click"));
        Assert.False(rig.Guard.EnsureAllowed(UiTestApp, "ui_click"));
        Assert.Equal(new[] { "notepad", "RevitAgent.UiTestApp" }, rig.Policy.AllowedInputProcesses);
    }

    [Fact]
    public void ForeignRevit_NeedsAGrant_GatewayLaunchedRevitDoesNot()
    {
        var rig = new Rig();
        Assert.False(rig.Guard.EnsureAllowed(Owned, "ui_click"));
        Assert.Contains("Let AI act on my Revit", rig.Refused(Foreign));

        rig.Grants.Grant(GrantScope.Pid, Foreign, TimeSpan.FromMinutes(5));
        Assert.True(rig.Guard.EnsureAllowed(Foreign, "ui_click"));

        rig.Probe.Reuse(Foreign); // the granted Revit exited; a new process got its pid
        Assert.Contains("Let AI act on my Revit", rig.Refused(Foreign));
    }

    [Fact]
    public void ProcessThatIsGone_IsRefused()
    {
        var rig = new Rig();
        var ex = Assert.Throws<GatewayException>(() => rig.Guard.EnsureAllowed(4_000_000, "ui_click"));
        Assert.Equal(GatewayErrorCodes.NotFound, ex.Code);
    }

    /// <summary>Gateway with fake processes; nothing here touches a real window (the guard runs before any UI Automation).</summary>
    private static ServiceProvider Gateway(FakeRevitProbe probe)
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        return new ServiceCollection()
            .AddSingleton<IRevitProcessProbe>(probe)
            .AddRevitAgentGateway(paths)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static TopLevelWindow WindowOf(int pid, string title) => new(0x1234, pid, "x", title, "#32770", 0, true, 0, 0, 100, 100);

    [Fact]
    public async Task UiAction_OnAResolvedForeignRevitWindow_IsRefusedWithoutAGrant()
    {
        // ui_snapshot {pid: FOREIGN} then ui_click {ref} without a pid: the pre-check saw no pid, the resolved window is
        // the user's Revit. EnsureActionAllowed is the path every ui action takes after resolving its target.
        await using var sp = Gateway(new FakeRevitProbe().Add(Foreign));
        var ui = sp.GetRequiredService<UiAutomationService>();

        var ex = await Assert.ThrowsAsync<GatewayException>(() => ui.RunAsync("s", TimeSpan.FromSeconds(10), input: true, CancellationToken.None, op =>
        {
            UiSafety.EnsureActionAllowed(op, WindowOf(Foreign, "Autodesk Revit 2024 - [Project1]"));
            return 0;
        }));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        Assert.Contains("Let AI act on my Revit", ex.Message);
    }

    [Fact]
    public async Task UiAction_OnTheDashboardWindow_IsRefused()
    {
        await using var sp = Gateway(new FakeRevitProbe().Add(Host, "RevitAgent.Host"));
        var ui = sp.GetRequiredService<UiAutomationService>();

        var ex = await Assert.ThrowsAsync<GatewayException>(() => ui.RunAsync("s", TimeSpan.FromSeconds(10), input: true, CancellationToken.None, op =>
        {
            UiSafety.EnsureActionAllowed(op, WindowOf(Host, "RevitAgent Host"));
            return 0;
        }));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        Assert.Contains("gateway itself", ex.Message);
    }

    [Fact]
    public async Task UiScript_ProcessIsChecked_WhenTheRunnerPinsIt_ButReadOnlyOperationsAreNot()
    {
        await using var sp = Gateway(new FakeRevitProbe().Add(Notepad, "notepad"));
        var ui = sp.GetRequiredService<UiAutomationService>();

        // The script runner sets RestrictPid before any step or dialog rule runs.
        var ex = await Assert.ThrowsAsync<GatewayException>(() => ui.RunAsync("s", TimeSpan.FromSeconds(10), input: true, CancellationToken.None, op =>
        {
            op.RestrictPid = Notepad;
            return 0;
        }));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);

        var observed = await ui.RunAsync("s", TimeSpan.FromSeconds(10), input: false, CancellationToken.None, op =>
        {
            op.RestrictPid = Notepad; // observing is allowed on any window
            return op.RestrictPid;
        });
        Assert.Equal(Notepad, observed);
    }

    [Fact]
    public async Task RevitHandleDialog_OnANonRevitProcessTheGatewayLaunched_IsRefused()
    {
        // EnsureRevitTarget lets a gateway-launched process through; the input guard still wants Revit (or an allowed name).
        using var helper = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            await using var sp = TestGateway.BuildProvider(out _);
            sp.GetRequiredService<LaunchedProcessRegistry>().Register(helper);
            var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync(
                "revit_handle_dialog",
                System.Text.Json.JsonSerializer.SerializeToElement(new { pid = helper.Id, hwnd = "0x1234", button = "OK" }),
                "s",
                CancellationToken.None);
            Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
            Assert.Contains("only goes to Revit", ((TextContent)r.Content[0]).Text);
        }
        finally
        {
            try
            {
                helper.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
