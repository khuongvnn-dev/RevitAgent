using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Install;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>bridge_install / bridge_uninstall need the machine owner's approval; agents have no way to give it.</summary>
public class OwnerApprovalTests
{
    private sealed class FakeChannel : IOwnerApprovalChannel
    {
        public Func<OwnerApprovalRequest, OwnerDecision> Answer { get; set; } = _ => OwnerDecision.Approve("fake", "owner (fake)");

        public bool Available { get; set; } = true;

        public List<OwnerApprovalRequest> Asked { get; } = new();

        public string Name => "fake";

        public bool IsAvailable => Available;

        public Task<OwnerDecision> AskAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(FakeChannel? channel)
        {
            Root = TestGateway.NewTempDir("revitagent_approval_");
            Addins = Path.Combine(Root, "Addins");
            var payload = Path.Combine(Root, "payload", BridgeInstaller.YearFolder(2024));
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, BridgeWire.AssemblyFileName), "build");
            var env = new BridgeInstallEnvironment
            {
                InstallRoot = Path.Combine(Root, "bridge"),
                UserAddinsDir = y => Path.Combine(Addins, y.ToString()),
                Trust = new InMemoryBridgeTrustStore(),
                Installations = () => new[] { new RevitInstallation(2024, Path.Combine(Root, "Revit"), Path.Combine(Root, "Revit", "Revit.exe"), "24.0", null, Path.Combine(Addins, "2024"), Path.Combine(Root, "ProgramData")) },
                PayloadRoots = () => new[] { Path.Combine(Root, "payload") },
                RunningRevit = () => new List<RevitProcessInfo>(),
            };
            Provider = BridgeGatewayToolsTests.BuildGateway(out _, installEnv: env, configure: s =>
            {
                if (channel is not null)
                {
                    s.AddSingleton<IOwnerApprovalChannel>(channel);
                }
            });
            Registry = Provider.GetRequiredService<ToolRegistry>();
            Audit = Provider.GetRequiredService<AuditLog>();
        }

        public string Root { get; }

        public string Addins { get; }

        public ServiceProvider Provider { get; }

        public ToolRegistry Registry { get; }

        public AuditLog Audit { get; }

        public string Manifest => Path.Combine(Addins, "2024", BridgeWire.ManifestFileName);

        public Task<ToolResult> Call(string tool) =>
            Registry.InvokeAsync(tool, JsonSerializer.SerializeToElement(new { years = new[] { 2024 } }), "agent-1", CancellationToken.None);

        public void Dispose() => Provider.Dispose();
    }

    [Fact]
    public async Task Deny_IsPermissionDenied_AndNothingIsInstalled()
    {
        var channel = new FakeChannel { Answer = _ => OwnerDecision.Deny("fake", "owner (fake)", "denied by the owner") };
        using var rig = new Rig(channel);

        var result = await rig.Call("bridge_install");

        Assert.Equal(GatewayErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.Contains("Owner approval required", (result.Content[0] as TextContent)?.Text);
        Assert.False(File.Exists(rig.Manifest));
        var asked = Assert.Single(channel.Asked);
        Assert.Equal("agent-1", asked.SessionId);
        Assert.Equal(new[] { 2024 }, asked.Years);
    }

    [Fact]
    public async Task Approve_RunsTheInstall_AndUninstallAsksAgain()
    {
        var channel = new FakeChannel();
        using var rig = new Rig(channel);

        var install = await rig.Call("bridge_install");
        Assert.False(install.IsError, (install.Content[0] as TextContent)?.Text);
        Assert.True(File.Exists(rig.Manifest));

        var uninstall = await rig.Call("bridge_uninstall");
        Assert.False(uninstall.IsError);
        Assert.False(File.Exists(rig.Manifest));
        Assert.Equal(new[] { "bridge_install", "bridge_uninstall" }, channel.Asked.Select(a => a.Tool));
    }

    [Fact]
    public async Task UninstallDeny_LeavesTheInstallInPlace()
    {
        var channel = new FakeChannel();
        using var rig = new Rig(channel);
        Assert.False((await rig.Call("bridge_install")).IsError);

        channel.Answer = _ => OwnerDecision.Deny("fake", "owner (fake)", "denied by the owner");
        var result = await rig.Call("bridge_uninstall");

        Assert.Equal(GatewayErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.True(File.Exists(rig.Manifest));
    }

    [Fact]
    public async Task NoChannelAvailable_DeniesImmediately()
    {
        using var rig = new Rig(null); // dashboard channel never attached, Telegram not configured
        var result = await rig.Call("bridge_install");

        Assert.Equal(GatewayErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.Contains("configure Telegram", (result.Content[0] as TextContent)?.Text);
        Assert.False(File.Exists(rig.Manifest));
    }

    [Fact]
    public async Task UnavailableChannel_IsNotAsked()
    {
        var channel = new FakeChannel { Available = false };
        using var rig = new Rig(channel);
        var result = await rig.Call("bridge_install");

        Assert.Equal(GatewayErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.Empty(channel.Asked);
    }

    [Fact]
    public async Task ChannelTimeout_IsADeny()
    {
        var channel = new FakeChannel { Answer = _ => OwnerDecision.Deny("fake", "system", "no answer in time", isExplicit: false) };
        using var rig = new Rig(channel);
        var result = await rig.Call("bridge_install");

        Assert.Equal(GatewayErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.False(File.Exists(rig.Manifest));
    }

    [Fact]
    public async Task DashboardChannel_AutoDeniesWhenNobodyAnswers()
    {
        using var rig = new Rig(null);
        var dashboard = rig.Provider.GetRequiredService<DashboardApprovalChannel>();
        dashboard.Attach();
        var approval = rig.Provider.GetRequiredService<IOwnerApproval>();

        var decision = await approval.RequestAsync(new OwnerApprovalRequest("bridge_install", "agent-1", new[] { 2024 }, TimeSpan.FromMilliseconds(100)), CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Empty(dashboard.Pending);
    }

    [Fact]
    public async Task DashboardChannel_ApproveFromTheCard_RunsTheInstall()
    {
        using var rig = new Rig(null);
        var dashboard = rig.Provider.GetRequiredService<DashboardApprovalChannel>();
        dashboard.Attach();
        dashboard.Changed += () =>
        {
            foreach (var p in dashboard.Pending)
            {
                dashboard.Resolve(p, approved: true);
            }
        };

        var result = await rig.Call("bridge_install");

        Assert.False(result.IsError, (result.Content[0] as TextContent)?.Text);
        Assert.True(File.Exists(rig.Manifest));
    }

    [Fact]
    public async Task EveryDecision_IsAudited()
    {
        var channel = new FakeChannel();
        using var rig = new Rig(channel);
        await rig.Call("bridge_install");
        channel.Answer = _ => OwnerDecision.Deny("fake", "owner (fake)", "denied by the owner");
        await rig.Call("bridge_uninstall");

        var entries = rig.Audit.Recent().Where(e => e.Tool == "owner_approval").ToList();
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].Ok);
        Assert.Equal("agent-1", entries[0].SessionId);
        using var first = JsonDocument.Parse(entries[0].Args);
        Assert.Equal("owner_approval", first.RootElement.GetProperty("kind").GetString());
        Assert.Equal("bridge_install", first.RootElement.GetProperty("tool").GetString());
        Assert.Equal("fake", first.RootElement.GetProperty("channel").GetString());
        Assert.Equal("approved", first.RootElement.GetProperty("decision").GetString());
        Assert.Equal("owner (fake)", first.RootElement.GetProperty("by").GetString());
        Assert.Equal(2024, first.RootElement.GetProperty("years")[0].GetInt32());
        Assert.False(entries[1].Ok);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, entries[1].ErrorCode);
        Assert.Contains("\"denied\"", entries[1].Args);
    }

    [Fact]
    public async Task ToolArguments_CannotSkipApproval()
    {
        var channel = new FakeChannel { Answer = _ => OwnerDecision.Deny("fake", "owner (fake)", "denied by the owner") };
        using var rig = new Rig(channel);
        var args = JsonSerializer.SerializeToElement(new { years = new[] { 2024 }, approved = true, force = true, skip_approval = true, confirm = true });

        var result = await rig.Registry.InvokeAsync("bridge_install", args, "agent-1", CancellationToken.None);

        Assert.Equal(GatewayErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.False(File.Exists(rig.Manifest));
    }

    private sealed class FakeEscalation : IHumanEscalation
    {
        public HumanAnswer Answer { get; set; } = null!;

        public HumanQuestion? Asked { get; private set; }

        public Task<HumanAnswer> AskAsync(HumanQuestion q, CancellationToken ct = default)
        {
            Asked = q;
            return Task.FromResult(Answer);
        }
    }

    private static HumanAnswer Answered(string choice, bool timedOut = false) =>
        new(choice, "owner", timedOut, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("Approve", false, true, true)]
    [InlineData("Deny", false, false, true)]
    [InlineData("Deny", true, false, false)]
    [InlineData("Approve", true, false, false)]
    public async Task TelegramChannel_MapsTheAnswer_AndOffersOnlyApproveAndDeny(string choice, bool timedOut, bool approved, bool isExplicit)
    {
        var escalation = new FakeEscalation { Answer = Answered(choice, timedOut) };
        var config = new TelegramConfigStore(new AgentPaths(TestGateway.NewTempDir("revitagent_tg_")));
        var channel = new TelegramOwnerApprovalChannel(escalation, config);

        var decision = await channel.AskAsync(new OwnerApprovalRequest("bridge_install", "agent-1", new[] { 2024 }, TimeSpan.FromMinutes(5)), CancellationToken.None);

        Assert.Equal(approved, decision.Approved);
        Assert.Equal(isExplicit, decision.Explicit);
        Assert.Equal(new[] { "Approve", "Deny" }, escalation.Asked!.Options);
        Assert.Equal("Deny", escalation.Asked.SafeOption);
        Assert.Equal(TimeSpan.FromMinutes(5), escalation.Asked.Timeout);
        Assert.False(channel.IsAvailable); // Telegram is not configured in a fresh home
    }

    [Fact]
    public void BridgeTools_SayApprovalIsRequired()
    {
        using var rig = new Rig(null);
        foreach (var name in new[] { "bridge_install", "bridge_uninstall" })
        {
            Assert.True(rig.Registry.TryGet(name, out var tool));
            Assert.Contains("owner's approval", tool.Description);
        }
    }
}
