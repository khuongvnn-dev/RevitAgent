using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Install;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>The Bridge tools as the gateway exposes them (DI discovery, names, permissions, schemas, Host-side checks).</summary>
public class BridgeGatewayToolsTests
{
    private const int Pid = 616161;

    private static readonly Dictionary<string, ToolPermission> Contract = new()
    {
        ["bridge_status"] = ToolPermission.Read,
        ["bridge_list_installs"] = ToolPermission.Read,
        ["bridge_install"] = ToolPermission.Admin,
        ["bridge_uninstall"] = ToolPermission.Admin,
        ["doc_info"] = ToolPermission.Read,
        ["doc_list_open"] = ToolPermission.Read,
        ["doc_open"] = ToolPermission.Write,
        ["doc_close"] = ToolPermission.Write,
        ["model_list_categories"] = ToolPermission.Read,
        ["model_query_elements"] = ToolPermission.Read,
        ["model_get_element"] = ToolPermission.Read,
        ["model_list_views"] = ToolPermission.Read,
        ["model_get_warnings"] = ToolPermission.Read,
        ["schedule_list_schedulable_categories"] = ToolPermission.Read,
        ["schedule_read"] = ToolPermission.Read,
        ["schedule_create"] = ToolPermission.Write,
        ["ribbon_list_items"] = ToolPermission.Read,
        ["command_list_postable"] = ToolPermission.Read,
        ["command_post"] = ToolPermission.Interact,
        ["view_activate"] = ToolPermission.Interact,
        ["selection_get"] = ToolPermission.Read,
        ["selection_set"] = ToolPermission.Interact,
        ["dialog_list_events"] = ToolPermission.Read,
    };

    /// <summary>Gateway with temp paths, a fake Revit pid and Bridge pipe names pointing at <paramref name="pipeName"/>.</summary>
    internal static ServiceProvider BuildGateway(out AgentPaths paths, string? pipeName = null, BridgeInstallEnvironment? installEnv = null, Action<IServiceCollection>? configure = null)
    {
        paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var services = new ServiceCollection();
        services.AddSingleton<IRevitProcessProbe>(new FakeRevitProbe().Add(Pid));
        var grants = new RevitAgent.Core.Policy.ActionGrantStore();
        grants.Grant(RevitAgent.Core.Policy.GrantScope.All, null, null); // these tests act on a non-launched fake Revit: the owner grant is on
        services.AddSingleton(grants);
        services.AddSingleton(new BridgeClientOptions
        {
            PipeNameForPid = pid => pipeName ?? BridgePipeNames.ForProcess(pid) + "-test-" + Guid.NewGuid().ToString("N")[..6],
            BridgeTimeoutMargin = TimeSpan.FromSeconds(1),
        });
        if (installEnv is not null)
        {
            services.AddSingleton(installEnv);
        }

        configure?.Invoke(services);
        services.AddRevitAgentGateway(paths);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    [Fact]
    public async Task AllPackageBTools_AreRegistered_WithHonestPermissions_AndRequiredPid()
    {
        await using var sp = BuildGateway(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        foreach (var (name, permission) in Contract)
        {
            Assert.True(registry.TryGet(name, out var tool), $"{name} missing");
            Assert.Equal(permission, tool.Permission);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            var schema = tool.InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            if (name.StartsWith("bridge_", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(schema.GetProperty("properties").TryGetProperty("pid", out _), $"{name} has no pid");
            Assert.Contains("pid", schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        }

        Assert.Equal(BridgeToolNames.Forwarded.OrderBy(n => n), BridgeToolCatalog.All.Select(d => d.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task ForwardedTools_WithoutPid_AreValidationErrors()
    {
        await using var sp = BuildGateway(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var r = await registry.InvokeAsync("doc_info", JsonDocument.Parse("{}").RootElement, "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, r.ErrorCode);
    }

    [Fact]
    public async Task DocOpen_OutsideSandbox_IsRefusedBeforeReachingRevit()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var contacted = false;
        using var stop = new CancellationTokenSource();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, _) =>
        {
            contacted = true;
            return "{\"id\":\"x\",\"ok\":true,\"result\":{}}";
        }, stop.Token);

        await using var sp = BuildGateway(out _, pipe);
        var registry = sp.GetRequiredService<ToolRegistry>();
        foreach (var path in new[] { @"C:\Projects\original.rvt", @"\\server\share\central.rvt" })
        {
            var r = await registry.InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path }), "t", CancellationToken.None);
            Assert.Equal(GatewayErrorCodes.SandboxViolation, r.ErrorCode);
        }

        var traversal = await registry.InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = Path.Combine(sp.GetRequiredService<AgentPaths>().RunsDir, "..", "evil.rvt") }), "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.SandboxViolation, traversal.ErrorCode);

        var relative = await registry.InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = "model.rvt" }), "t", CancellationToken.None);
        Assert.NotNull(relative.ErrorCode);

        Assert.False(contacted);
        stop.Cancel();
        await Task.WhenAny(server, Task.Delay(2000));
    }

    [Fact]
    public async Task DocOpen_InsideSandbox_ForwardsNormalizedPath_WithoutPid()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        string? request = null;
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, json) =>
        {
            request = json;
            return "{\"id\":\"x\",\"ok\":true,\"result\":{\"opened\":true}}";
        });

        await using var sp = BuildGateway(out var paths, pipe);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var copy = Path.Combine(paths.RunsDir, "r1", "sub", "..", "model.rvt");
        var r = await registry.InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = copy, audit = false }), "t", CancellationToken.None);
        await server;

        Assert.False(r.IsError, (r.Content[0] as TextContent)?.Text);
        Assert.True(r.Structured!.Value.GetProperty("opened").GetBoolean());
        using var req = JsonDocument.Parse(request!);
        Assert.Equal("doc_open", req.RootElement.GetProperty("tool").GetString());
        var args = req.RootElement.GetProperty("args");
        Assert.Equal(Path.GetFullPath(copy), args.GetProperty("path").GetString());
        Assert.False(args.TryGetProperty("pid", out _));
        Assert.False(args.GetProperty("audit").GetBoolean());
        Assert.Equal(600 - 1, req.RootElement.GetProperty("timeout_sec").GetInt32());
    }

    [Fact]
    public async Task DocClose_RejectsSave_AndMapsPathOrTitleToTheDocument()
    {
        await using (var sp0 = BuildGateway(out _))
        {
            var save = await sp0.GetRequiredService<ToolRegistry>().InvokeAsync("doc_close", JsonSerializer.SerializeToElement(new { pid = Pid, save = true }), "t", CancellationToken.None);
            Assert.Equal(GatewayErrorCodes.ValidationFailed, save.ErrorCode);
            var both = await sp0.GetRequiredService<ToolRegistry>().InvokeAsync("doc_close", JsonSerializer.SerializeToElement(new { pid = Pid, path = @"C:\a.rvt", title = "b" }), "t", CancellationToken.None);
            Assert.Equal(GatewayErrorCodes.ValidationFailed, both.ErrorCode);
        }

        var pipe = BridgeClientProtocolTests.NewPipeName();
        string? request = null;
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, json) =>
        {
            request = json;
            return "{\"id\":\"x\",\"ok\":true,\"result\":{\"closed\":true}}";
        });
        await using var sp = BuildGateway(out _, pipe);
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("doc_close", JsonSerializer.SerializeToElement(new { pid = Pid, path = @"C:\runs\copy.rvt", save = false }), "t", CancellationToken.None);
        await server;
        Assert.False(r.IsError);
        using var req = JsonDocument.Parse(request!);
        var args = req.RootElement.GetProperty("args");
        Assert.Equal(@"C:\runs\copy.rvt", args.GetProperty("document").GetString());
        Assert.False(args.TryGetProperty("save", out _));
        Assert.False(args.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task SandboxEnforcementOff_AllowsOutsidePaths()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, _) => "{\"id\":\"x\",\"ok\":true,\"result\":{\"opened\":true}}");
        await using var sp = BuildGateway(out _, pipe);
        sp.GetRequiredService<RevitAgent.Core.Policy.GatewayPolicy>().EnforceSandbox = false;
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = @"C:\Projects\original.rvt" }), "t", CancellationToken.None);
        await server;
        Assert.False(r.IsError);
    }

    [Fact]
    public async Task BridgeStatus_ReportsMissingPipe_AsNotLoaded()
    {
        await using var sp = BuildGateway(out _);
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("bridge_status", JsonSerializer.SerializeToElement(new { pid = Pid }), "t", CancellationToken.None);
        Assert.False(r.IsError);
        var inst = r.Structured!.Value.GetProperty("instances")[0];
        Assert.False(inst.GetProperty("pipe_present").GetBoolean());
        Assert.False(inst.GetProperty("reachable").GetBoolean());
        Assert.Equal(GatewayErrorCodes.DependencyFailed, inst.GetProperty("error").GetProperty("code").GetString());

        var all = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("bridge_status", null, "t", CancellationToken.None);
        Assert.Equal(1, all.Structured!.Value.GetProperty("count").GetInt32()); // only the fake probe's process

        var missing = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("bridge_status", JsonSerializer.SerializeToElement(new { pid = 99 }), "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task BridgeStatus_PingsAReachableBridge()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, json) =>
        {
            Assert.Contains("\"tool\":\"ping\"", json);
            return "{\"id\":\"x\",\"ok\":true,\"result\":{\"protocol\":1,\"bridge_version\":\"2.0.0\",\"busy\":false}}";
        });
        await using var sp = BuildGateway(out _, pipe);
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("bridge_status", JsonSerializer.SerializeToElement(new { pid = Pid }), "t", CancellationToken.None);
        await server;
        var inst = r.Structured!.Value.GetProperty("instances")[0];
        Assert.True(inst.GetProperty("reachable").GetBoolean());
        Assert.Equal("2.0.0", inst.GetProperty("bridge").GetProperty("bridge_version").GetString());
        Assert.Equal(1, r.Structured!.Value.GetProperty("reachable").GetInt32());
    }
}
