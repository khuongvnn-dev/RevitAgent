using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests;

/// <summary>
/// End-to-end MCP tests through the official SDK client over the real named-pipe listener.
/// The SDK client uses numeric JSON-RPC ids, which the old hand-written server rejected.
/// </summary>
public class GatewayMcpTests
{
    [Fact]
    public async Task McpClient_OverNamedPipe_InitializesListsAndCallsTools()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var listener = sp.GetRequiredService<GatewayPipeListener>();
        var pipeName = "revitagent-test-" + Guid.NewGuid().ToString("N")[..8];
        listener.Start(pipeName);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe));

            Assert.Equal(McpGatewayServer.ServerName, client.ServerInfo!.Name);
            Assert.False(string.IsNullOrWhiteSpace(client.ServerInstructions));

            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, t => t.Name == "gateway_get_info");

            var result = await client.CallToolAsync("gateway_get_info", new Dictionary<string, object?>());
            Assert.NotEqual(true, result.IsError);
            var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
            using var doc = JsonDocument.Parse(text);
            Assert.Equal("revitagent", doc.RootElement.GetProperty("name").GetString());
            Assert.True(doc.RootElement.GetProperty("tools_total").GetInt32() >= 1);
            Assert.Equal(ToolProfiles.Core, doc.RootElement.GetProperty("profile").GetString());
            Assert.Equal(tools.Count, doc.RootElement.GetProperty("tools_listed").GetInt32());

            var unknown = await client.CallToolAsync("no_such_tool", new Dictionary<string, object?>());
            Assert.True(unknown.IsError);
            Assert.Contains(GatewayErrorCodes.NotFound, Assert.IsType<TextContentBlock>(unknown.Content[0]).Text);
        }
        finally
        {
            await listener.StopAsync();
        }
    }

    [Fact]
    public async Task Registry_MapsExceptionsToErrorCodes_AndAudits()
    {
        await using var sp = TestGateway.BuildProvider(out var paths);
        var audit = sp.GetRequiredService<AuditLog>();
        var registry = new ToolRegistry(
            new IGatewayTool[] { new ThrowingTool("t_validation", GatewayException.Validation("bad pid")), new ThrowingTool("t_boom", new InvalidOperationException("secret stack")) },
            new RevitAgent.Core.Policy.GatewayPolicy(),
            sp.GetRequiredService<RevitAgent.Core.Policy.ActionGuard>(),
            audit,
            Array.Empty<IToolCallObserver>());

        var v = await registry.InvokeAsync("t_validation", null, "s1", CancellationToken.None);
        Assert.True(v.IsError);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, v.ErrorCode);

        var b = await registry.InvokeAsync("t_boom", null, "s1", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.Internal, b.ErrorCode);
        Assert.DoesNotContain("secret stack", ((TextContent)b.Content[0]).Text);

        Assert.Equal(2, audit.Recent().Count);
        Assert.True(Directory.EnumerateFiles(paths.AuditDir, "*.jsonl").Any());
    }

    [Fact]
    public async Task Registry_UsesThePerCallBudget_NotOnlyTheDefaultTimeout()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = new ToolRegistry(
            new IGatewayTool[] { new SlowTool() },
            new RevitAgent.Core.Policy.GatewayPolicy(),
            sp.GetRequiredService<RevitAgent.Core.Policy.ActionGuard>(),
            sp.GetRequiredService<AuditLog>(),
            Array.Empty<IToolCallObserver>());

        var shortBudget = await registry.InvokeAsync("t_slow", null, "s1", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.Timeout, shortBudget.ErrorCode);

        var longBudget = await registry.InvokeAsync("t_slow", JsonSerializer.SerializeToElement(new { wait_for_human = true }), "s1", CancellationToken.None);
        Assert.False(longBudget.IsError, ((TextContent)longBudget.Content[0]).Text);
    }

    /// <summary>Takes 600 ms; default budget 200 ms, 5 s when called with wait_for_human.</summary>
    private sealed class SlowTool : IGatewayTool
    {
        public string Name => "t_slow";

        public string Description => "slow";

        public ToolPermission Permission => ToolPermission.Read;

        public JsonElement InputSchema { get; } = Schema.Object().Boolean("wait_for_human", "x").Build();

        public TimeSpan Timeout => TimeSpan.FromMilliseconds(200);

        public TimeSpan TimeoutFor(ToolCall call) => call.GetBool("wait_for_human") ? TimeSpan.FromSeconds(5) : Timeout;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
        {
            await Task.Delay(600, cancellationToken);
            return ToolResult.Json(new { ok = true });
        }
    }

    [Fact]
    public async Task Registry_PolicyBlocksToolsAbovePermission()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var policy = new RevitAgent.Core.Policy.GatewayPolicy { MaxPermission = ToolPermission.Read };
        var registry = new ToolRegistry(
            new IGatewayTool[] { new ThrowingTool("t_admin", new Exception("must not run"), ToolPermission.Admin) },
            policy,
            sp.GetRequiredService<RevitAgent.Core.Policy.ActionGuard>(),
            sp.GetRequiredService<AuditLog>(),
            Array.Empty<IToolCallObserver>());

        var r = await registry.InvokeAsync("t_admin", null, "s1", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
    }

    [Fact]
    public void Policy_SandboxCheck_RejectsTraversalAndOutsidePaths()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var policy = new RevitAgent.Core.Policy.GatewayPolicy();
        Assert.True(policy.IsInSandbox(Path.Combine(paths.RunsDir, "r1", "model.rvt"), paths));
        Assert.False(policy.IsInSandbox(Path.Combine(paths.RunsDir, "..", "evil.rvt"), paths));
        Assert.False(policy.IsInSandbox(@"C:\Projects\original.rvt", paths));
        var ex = Assert.Throws<GatewayException>(() => policy.EnsureInSandbox(@"C:\Projects\original.rvt", paths, "Model"));
        Assert.Equal(GatewayErrorCodes.SandboxViolation, ex.Code);
    }

    [Fact]
    public void ToolCall_ArgumentHelpers_ValidateTypes()
    {
        var call = new ToolCall("x", JsonDocument.Parse("""{"pid": 12, "hwnd": "0x1A", "flag": true, "names": ["a","b"], "limit": 5000}""").RootElement);
        Assert.Equal(12, call.RequireInt("pid"));
        Assert.Equal(0x1A, call.GetLong("hwnd"));
        Assert.True(call.GetBool("flag"));
        Assert.Equal(new[] { "a", "b" }, call.GetStringList("names"));
        Assert.Equal(1000, call.GetLimit(max: 1000));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, Assert.Throws<GatewayException>(() => call.RequireString("missing")).Code);
    }

    private sealed class ThrowingTool : GatewayTool
    {
        private readonly Exception _ex;

        public ThrowingTool(string name, Exception ex, ToolPermission permission = ToolPermission.Read)
        {
            Name = name;
            _ex = ex;
            Permission = permission;
        }

        public override string Name { get; }

        public override string Description => "test";

        public override ToolPermission Permission { get; }

        public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken) => throw _ex;
    }
}

public class RevitInstallationsTests
{
    [Fact]
    public void Discover_ReturnsOnlyFoldersWithRevitExe_SortedByYear()
    {
        var found = RevitAgent.Core.Revit.RevitInstallations.Discover();
        Assert.All(found, i => Assert.True(File.Exists(i.ExePath)));
        Assert.Equal(found.Select(i => i.Year).OrderBy(y => y), found.Select(i => i.Year));
        Assert.All(found, i => Assert.EndsWith(i.Year.ToString(), i.UserAddinsDir));
    }
}

public class SandboxReparseTests
{
    [Fact]
    public void Policy_SandboxCheck_RejectsJunctionInsideRuns()
    {
        var paths = new RevitAgent.Core.AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var outside = TestGateway.NewTempDir("revitagent_outside_");
        File.WriteAllText(Path.Combine(outside, "original.rvt"), "x");
        var link = Path.Combine(paths.RunsDir, "run1", "lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        // A junction needs no special privilege (unlike a symlink) — exactly what an agent's shell could create.
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mk.WaitForExit(10_000);
        }

        Assert.True(Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "junction was not created");
        var policy = new RevitAgent.Core.Policy.GatewayPolicy();
        Assert.False(policy.IsInSandbox(Path.Combine(link, "original.rvt"), paths));
        Assert.True(policy.IsInSandbox(Path.Combine(paths.RunsDir, "run1", "copy.rvt"), paths));
        Assert.False(policy.IsInSandbox(@"\server\share\runs\x.rvt", paths));
    }
}
