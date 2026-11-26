using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;
using RevitAgent.Tests.Bridge;
using RevitAgent.Tests.Host;

namespace RevitAgent.Tests.Policy;

public sealed class ActionGuardTests
{
    private const int Owned = 100;
    private const int Foreign = 200;
    private const int Foreign2 = 201;
    private const int Other = 300;

    private sealed class Owners : IOwnedProcessLookup
    {
        public HashSet<int> Pids { get; } = new() { Owned };

        public bool IsOwned(int pid) => Pids.Contains(pid);
    }

    private sealed class Windows : IWindowProcessResolver
    {
        public Dictionary<string, int> ByWindow { get; } = new();

        public int? ProcessOf(WindowSpec spec) =>
            spec.Window is { } w && ByWindow.TryGetValue(w, out var p) ? p
            : spec.Hwnd is { } h && ByWindow.TryGetValue("hwnd:" + h, out var q) ? q
            : null;
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _sp;

        public Rig()
        {
            _sp = TestGateway.BuildProvider(out _);
            Time = new FakeTime();
            Grants = new ActionGrantStore(Time);
            var probe = new FakeRevitProbe().Add(Owned).Add(Foreign).Add(Foreign2).Add(Other, "notepad");
            Audit = _sp.GetRequiredService<AuditLog>();
            Registry = new ToolRegistry(
                new IGatewayTool[]
                {
                    new FakeTool("fake_act", ToolPermission.Interact),
                    new FakeTool("fake_write", ToolPermission.Write),
                    new FakeTool("fake_read", ToolPermission.Read),
                    new FakeTool("revit_close", ToolPermission.Admin),
                },
                new GatewayPolicy(),
                new ActionGuard(Grants, probe, Owners, Windows),
                Audit,
                Array.Empty<IToolCallObserver>());
        }

        public FakeTime Time { get; }

        public ActionGrantStore Grants { get; }

        public Owners Owners { get; } = new();

        public Windows Windows { get; } = new();

        public AuditLog Audit { get; }

        public ToolRegistry Registry { get; }

        public Task<ToolResult> Call(string tool, object args) =>
            Registry.InvokeAsync(tool, JsonSerializer.SerializeToElement(args), "s", CancellationToken.None);

        public ValueTask DisposeAsync() => _sp.DisposeAsync();
    }

    [Fact]
    public async Task OwnedPid_IsAllowed_WithoutGrant()
    {
        await using var rig = new Rig();
        Assert.False((await rig.Call("fake_act", new { pid = Owned })).IsError);
        Assert.False(rig.Audit.Recent().Last().GrantUsed);
    }

    [Theory]
    [InlineData("fake_act")]
    [InlineData("fake_write")]
    public async Task NotOwnedRevit_Interact_IsDenied_WithGuidance(string tool)
    {
        await using var rig = new Rig();
        var r = await rig.Call(tool, new { pid = Foreign });
        Assert.True(r.IsError);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
        var text = ((TextContent)r.Content[0]).Text;
        Assert.Contains("Let AI act on my Revit", text);
        Assert.Contains("Read-only tools", text);
    }

    [Fact]
    public async Task PidGrant_AllowsThatPidOnly_AndIsAudited()
    {
        await using var rig = new Rig();
        rig.Grants.Grant(GrantScope.Pid, Foreign, TimeSpan.FromMinutes(15));
        Assert.False((await rig.Call("fake_act", new { pid = Foreign })).IsError);
        Assert.True(rig.Audit.Recent().Last().GrantUsed);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, (await rig.Call("fake_act", new { pid = Foreign2 })).ErrorCode);
    }

    [Fact]
    public async Task AllGrant_AllowsAnyNotOwnedRevit()
    {
        await using var rig = new Rig();
        rig.Grants.Grant(GrantScope.All, null, null);
        Assert.False((await rig.Call("fake_act", new { pid = Foreign })).IsError);
        Assert.False((await rig.Call("fake_act", new { pid = Foreign2 })).IsError);
        Assert.True(rig.Audit.Recent().Last().GrantUsed);
    }

    [Fact]
    public async Task ExpiredGrant_IsDenied_AndRevokeWorks()
    {
        await using var rig = new Rig();
        rig.Grants.Grant(GrantScope.All, null, TimeSpan.FromMinutes(15));
        rig.Time.Now += TimeSpan.FromMinutes(14);
        Assert.False((await rig.Call("fake_act", new { pid = Foreign })).IsError);
        rig.Time.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, (await rig.Call("fake_act", new { pid = Foreign })).ErrorCode);
        Assert.Empty(rig.Grants.Active());

        rig.Grants.Grant(GrantScope.Pid, Foreign, null);
        Assert.False((await rig.Call("fake_act", new { pid = Foreign })).IsError);
        rig.Grants.Revoke(GrantScope.Pid, Foreign);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, (await rig.Call("fake_act", new { pid = Foreign })).ErrorCode);
    }

    [Fact]
    public async Task ReadTool_IsNeverAffected()
    {
        await using var rig = new Rig();
        Assert.False((await rig.Call("fake_read", new { pid = Foreign })).IsError);
    }

    [Fact]
    public async Task NonRevitProcess_AndNoTarget_AreUnaffected()
    {
        await using var rig = new Rig();
        Assert.False((await rig.Call("fake_act", new { pid = Other })).IsError);
        Assert.False((await rig.Call("fake_act", new { })).IsError);
        Assert.False((await rig.Call("fake_act", new { pid = 99999 })).IsError); // not running: the tool reports it
    }

    [Fact]
    public async Task WindowTargetedTool_ResolvesTheWindowsProcess()
    {
        await using var rig = new Rig();
        rig.Windows.ByWindow["Autodesk Revit"] = Foreign;
        rig.Windows.ByWindow["Notepad"] = Other;
        rig.Windows.ByWindow["hwnd:4660"] = Foreign;

        Assert.Equal(GatewayErrorCodes.PermissionDenied, (await rig.Call("fake_act", new { window = "Autodesk Revit" })).ErrorCode);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, (await rig.Call("fake_act", new { hwnd = 4660 })).ErrorCode);
        Assert.False((await rig.Call("fake_act", new { window = "Notepad" })).IsError);

        // pid says "owned" but the window belongs to the user's Revit: still denied.
        Assert.Equal(GatewayErrorCodes.PermissionDenied, (await rig.Call("fake_act", new { pid = Owned, window = "Autodesk Revit" })).ErrorCode);

        rig.Grants.Grant(GrantScope.Pid, Foreign, TimeSpan.FromHours(1));
        Assert.False((await rig.Call("fake_act", new { window = "Autodesk Revit" })).IsError);
    }

    [Fact]
    public async Task RevitClose_KeepsItsOwnRule_GuardDoesNotApply()
    {
        await using var rig = new Rig();
        Assert.False((await rig.Call("revit_close", new { pid = Foreign })).IsError);
    }
}
