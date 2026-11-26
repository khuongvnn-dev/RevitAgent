using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Tools;
using Xunit.Abstractions;

namespace RevitAgent.Tests;

/// <summary>Tool profiles (core / full): filtering, how the profile reaches a session, the core context budget.</summary>
public class ToolProfileTests
{
    /// <summary>Core tools/list without macro_list / macro_run + server instructions, in characters.</summary>
    private const int CoreBudgetChars = 19_500;

    /// <summary>The same with the macro tools (≈ 5.25k tokens; view_activate + model_get_warnings added to core on 2026-11-26).</summary>
    private const int CoreWithMacrosBudgetChars = 21_000;

    /// <summary>Legacy revision with the initialize handshake; null = the SDK default (2026-07-28: server/discover + per-request _meta).</summary>
    private const string LegacyProtocol = "2025-06-18";

    private readonly ITestOutputHelper _output;

    public ToolProfileTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CoreProfile_ToolsListPlusInstructions_FitTheContextBudget()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var list = McpGatewayServer.ListTools(registry.Tools, ToolProfiles.Core);

        var table = new StringBuilder();
        foreach (var tool in list.Tools)
        {
            table.AppendLine($"{tool.Name,-24} {JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions).Length,6}");
        }

        var withoutMacros = new ListToolsResult { Tools = list.Tools.Where(t => !t.Name.StartsWith("macro_", StringComparison.Ordinal)).ToList() };
        var toolsJson = JsonSerializer.Serialize(withoutMacros, McpJsonUtilities.DefaultOptions).Length;
        var toolsWithMacrosJson = JsonSerializer.Serialize(list, McpJsonUtilities.DefaultOptions).Length;
        var total = toolsJson + AgentInstructions.Text.Length;
        var totalWithMacros = toolsWithMacrosJson + AgentInstructions.Text.Length;
        table.AppendLine($"{"tools/list (core)",-24} {toolsJson,6}  (with macro_*: {toolsWithMacrosJson})");
        table.AppendLine($"{"instructions",-24} {AgentInstructions.Text.Length,6}");
        table.AppendLine($"{"total",-24} {total,6}  (with macro_*: {totalWithMacros})");
        _output.WriteLine(table.ToString());

        Assert.True(total <= CoreBudgetChars, $"core tools/list (without macro_*) + instructions = {total} chars > {CoreBudgetChars}:\n{table}");
        Assert.True(totalWithMacros <= CoreWithMacrosBudgetChars, $"core tools/list + instructions = {totalWithMacros} chars > {CoreWithMacrosBudgetChars}:\n{table}");
        Assert.True(AgentInstructions.Text.Length <= 2_000, $"instructions are {AgentInstructions.Text.Length} chars (target ≤ 2,000)");
    }

    [Fact]
    public async Task CoreProfile_ListsTheCoreToolsOnly_FullListsEverything()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var core = McpGatewayServer.ListTools(registry.Tools, ToolProfiles.Core).Tools.Select(t => t.Name).ToArray();
        var full = McpGatewayServer.ListTools(registry.Tools, ToolProfiles.Full).Tools.Select(t => t.Name).ToArray();

        // Every core tool except the macro tools (added separately) is registered, so the budget test measures them all.
        var expected = ToolProfiles.CoreTools.Where(n => !n.StartsWith("macro_", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, core.Where(n => !n.StartsWith("macro_", StringComparison.Ordinal)));
        Assert.Equal(registry.Tools.Count, full.Length);
        Assert.True(full.Length > core.Length);
        Assert.Equal(full.OrderBy(n => n, StringComparer.Ordinal), full);
    }

    [Theory]
    [InlineData("core", "core")]
    [InlineData(" FULL ", "full")]
    [InlineData("everything", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_AcceptsCoreAndFull(string? value, string? expected) => Assert.Equal(expected, ToolProfiles.Parse(value));

    [Fact]
    public void NotificationLine_IsOneNewlineTerminatedJsonRpcNotification()
    {
        var text = Encoding.UTF8.GetString(ToolProfiles.NotificationLine(ToolProfiles.Full));

        Assert.EndsWith("\n", text);
        Assert.Equal(1, text.Count(c => c == '\n'));
        var json = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("2.0", json["jsonrpc"]!.GetValue<string>());
        Assert.Equal(ToolProfiles.NotificationMethod, json["method"]!.GetValue<string>());
        Assert.False(json.ContainsKey("id")); // a notification: the server never replies
        Assert.Equal(ToolProfiles.Full, ToolProfiles.FromNotification(json["params"]));
    }

    [Theory]
    [InlineData("""{"profile":"core"}""", "core")]
    [InlineData("""{"profile":"FULL"}""", "full")]
    [InlineData("""{"profile":"huge"}""", null)]
    [InlineData("""{"profile":1}""", null)]
    [InlineData("""{}""", null)]
    [InlineData("""["full"]""", null)]
    public void FromNotification_ReadsTheProfile(string parameters, string? expected) =>
        Assert.Equal(expected, ToolProfiles.FromNotification(JsonNode.Parse(parameters)));

    [Fact]
    public void FromClientCapabilities_ReadsExperimentalRevitagentProfile()
    {
        Assert.Null(ToolProfiles.FromClientCapabilities(null));
        Assert.Null(ToolProfiles.FromClientCapabilities(new ClientCapabilities()));
        Assert.Equal(ToolProfiles.Full, ToolProfiles.FromClientCapabilities(Caps("""{"profile":"full"}""")));
        Assert.Null(ToolProfiles.FromClientCapabilities(Caps("""{"profile":"huge"}""")));
        Assert.Null(ToolProfiles.FromClientCapabilities(Caps("\"full\"")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LegacyProtocol)]
    public async Task PipeSession_WithoutProfile_ListsCore_ButRunsEveryTool(string? protocol)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var total = sp.GetRequiredService<ToolRegistry>().Tools.Count;
        await using var listener = StartListener(sp, out var pipeName);
        await using var pipe = await ConnectAsync(pipeName);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), Options(protocol));

        var tools = await client.ListToolsAsync();
        Assert.True(tools.Count < total);
        Assert.All(tools, t => Assert.Contains(t.Name, ToolProfiles.CoreTools));
        var info = await GatewayInfoAsync(client);
        Assert.Equal("core", info.GetProperty("profile").GetString());
        Assert.Equal(tools.Count, info.GetProperty("tools_listed").GetInt32());
        Assert.Equal(total, info.GetProperty("tools_total").GetInt32());
        Assert.Contains("--profile full", info.GetProperty("hint").GetString());

        // A tool outside the profile still runs when called by name.
        var notListed = await client.CallToolAsync("sandbox_list_runs", new Dictionary<string, object?>());
        Assert.NotEqual(true, notListed.IsError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LegacyProtocol)]
    public async Task PipeSession_AfterTheShimNotification_ListsFull(string? protocol)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var total = sp.GetRequiredService<ToolRegistry>().Tools.Count;
        await using var listener = StartListener(sp, out var pipeName);
        await using var pipe = await ConnectAsync(pipeName);

        // Exactly what the relay Shim with --profile full puts on the pipe: one line, then the client's bytes unchanged.
        await pipe.WriteAsync(ToolProfiles.NotificationLine(ToolProfiles.Full));
        await pipe.FlushAsync();
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), Options(protocol));

        Assert.Equal(total, (await client.ListToolsAsync()).Count);
        var info = await GatewayInfoAsync(client);
        Assert.Equal("full", info.GetProperty("profile").GetString());
        Assert.Equal(total, info.GetProperty("tools_listed").GetInt32());
        Assert.False(info.TryGetProperty("hint", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LegacyProtocol)]
    public async Task PipeSession_ClientDeclaringFullInCapabilities_ListsFull(string? protocol)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var total = sp.GetRequiredService<ToolRegistry>().Tools.Count;
        await using var listener = StartListener(sp, out var pipeName);
        await using var pipe = await ConnectAsync(pipeName);
        await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), Options(protocol, Caps("""{"profile":"full"}""")));

        Assert.Equal(total, (await client.ListToolsAsync()).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LegacyProtocol)]
    public async Task OlderHost_WithoutTheNotificationHandler_IgnoresTheShimLine(string? protocol)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var options = sp.GetRequiredService<McpGatewayServer>().BuildOptions("old-host");
        options.Handlers.NotificationHandlers = null; // what a Host built before profiles does with the line
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (serverSide, clientSide) = await PipePairAsync();
        await using (serverSide)
        await using (clientSide)
        {
            await using var server = ModelContextProtocol.Server.McpServer.Create(new ModelContextProtocol.Server.StreamServerTransport(serverSide, serverSide), options, null, sp);
            var run = server.RunAsync(cts.Token);
            await clientSide.WriteAsync(ToolProfiles.NotificationLine(ToolProfiles.Full), cts.Token);
            await clientSide.FlushAsync(cts.Token);
            await using (var client = await McpClient.CreateAsync(new StreamClientTransport(clientSide, clientSide), Options(protocol), cancellationToken: cts.Token))
            {
                var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
                Assert.NotEmpty(tools);
                Assert.All(tools, t => Assert.Contains(t.Name, ToolProfiles.CoreTools));
            }

            cts.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        }
    }

    [Fact]
    public async Task HeadlessSession_FixedProfileWins_OverWhatTheClientDeclared()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var server = sp.GetRequiredService<McpGatewayServer>();
        var total = sp.GetRequiredService<ToolRegistry>().Tools.Count;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (serverSide, clientSide) = await PipePairAsync();
        await using (serverSide)
        await using (clientSide)
        {
            var session = server.RunSessionAsync(serverSide, serverSide, "headless-test", cts.Token, ToolProfiles.Full);
            await using (var client = await McpClient.CreateAsync(new StreamClientTransport(clientSide, clientSide), Options(null, Caps("""{"profile":"core"}""")), cancellationToken: cts.Token))
            {
                Assert.Equal(total, (await client.ListToolsAsync(cancellationToken: cts.Token)).Count);
            }

            cts.Cancel();
            await session.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        }
    }

    private static ClientCapabilities Caps(string revitagent) => new()
    {
        Experimental = new Dictionary<string, object> { [ToolProfiles.CapabilityKey] = JsonDocument.Parse(revitagent).RootElement.Clone() },
    };

    private static McpClientOptions Options(string? protocol, ClientCapabilities? capabilities = null) => new()
    {
        ProtocolVersion = protocol,
        Capabilities = capabilities,
    };

    private static GatewayPipeListener StartListener(IServiceProvider sp, out string pipeName)
    {
        var listener = sp.GetRequiredService<GatewayPipeListener>();
        pipeName = "revitagent-test-" + Guid.NewGuid().ToString("N")[..8];
        listener.Start(pipeName);
        return listener;
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    /// <summary>A connected duplex pair with cancellable async I/O (anonymous pipes block on reads).</summary>
    private static async Task<(Stream Server, Stream Client)> PipePairAsync()
    {
        var name = "revitagent-test-pair-" + Guid.NewGuid().ToString("N")[..8];
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var waiting = server.WaitForConnectionAsync();
        var client = await ConnectAsync(name);
        await waiting;
        return (server, client);
    }

    private static async Task<JsonElement> GatewayInfoAsync(McpClient client)
    {
        var result = await client.CallToolAsync("gateway_get_info", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement.Clone();
    }

    // ---- ShimPreamble: the profile line is taken off the pipe before the MCP server reads it (no race with tools/list) ----

    private const string Lf = "\n";

    [Fact]
    public async Task Preamble_NotificationAndClientMessageInOneWrite_ConsumesOnlyTheLine()
    {
        var client = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}" + Lf;
        var bytes = ToolProfiles.NotificationLine(ToolProfiles.Full).Concat(Encoding.UTF8.GetBytes(client)).ToArray();
        var (input, profile) = await ShimPreamble.ReadAsync(new MemoryStream(bytes), CancellationToken.None);
        Assert.Equal(ToolProfiles.Full, profile);
        Assert.Equal(client, await new StreamReader(input).ReadToEndAsync());
    }

    [Fact]
    public async Task Preamble_WithoutNotification_ReplaysEveryByte()
    {
        var client = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\"}" + Lf + "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}" + Lf;
        var (input, profile) = await ShimPreamble.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(client)), CancellationToken.None);
        Assert.Null(profile);
        Assert.Equal(client, await new StreamReader(input).ReadToEndAsync());
    }

    [Fact]
    public async Task Preamble_LineSplitAcrossReads_StillParsed()
    {
        var line = ToolProfiles.NotificationLine(ToolProfiles.Full);
        var rest = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}" + Lf);
        var (input, profile) = await ShimPreamble.ReadAsync(new OneByteStream(line.Concat(rest).ToArray()), CancellationToken.None);
        Assert.Equal(ToolProfiles.Full, profile);
        Assert.Equal(Encoding.UTF8.GetString(rest), await new StreamReader(input).ReadToEndAsync());
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/revitagent/profile\",\"params\":{\"profile\":\"bogus\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"notifications/revitagent/profile\",\"params\":{\"profile\":\"full\"}}")]
    [InlineData("not json")]
    public async Task Preamble_OtherFirstLine_IsReplayed(string first)
    {
        var text = first + Lf;
        var (input, profile) = await ShimPreamble.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);
        Assert.Null(profile);
        Assert.Equal(text, await new StreamReader(input).ReadToEndAsync());
    }

    /// <summary>Returns at most one byte per read, like a slow pipe.</summary>
    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
