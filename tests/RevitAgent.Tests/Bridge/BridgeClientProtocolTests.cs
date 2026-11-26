using System.IO;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>
/// BridgeClient against a hand-written fake Bridge (System.Text.Json, raw bytes) to pin the wire format:
/// 4-byte little-endian length + UTF-8 JSON {id, tool, args, timeout_sec} → {id, ok, result | error}.
/// </summary>
public class BridgeClientProtocolTests
{
    private const int Pid = 515151;

    [Fact]
    public async Task Request_IsFramedLittleEndian_WithIdToolArgsAndShorterBridgeTimeout()
    {
        var pipe = NewPipeName();
        string? received = null;
        byte[]? header = null;
        var server = RunFakeBridge(pipe, (raw, json) =>
        {
            header = raw;
            received = json;
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString();
            return $"{{\"id\":\"{id}\",\"ok\":true,\"result\":{{\"levels\":3}}}}";
        });

        var client = NewClient(pipe, margin: TimeSpan.FromSeconds(4));
        var result = await client.CallAsync(Pid, "model_query_elements", JsonSerializer.SerializeToElement(new { category = "OST_Levels" }), TimeSpan.FromSeconds(60), CancellationToken.None);
        await server;

        Assert.Equal(3, result.GetProperty("levels").GetInt32());
        Assert.NotNull(received);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(received!), BinaryPrimitives.ReadUInt32LittleEndian(header));
        using var req = JsonDocument.Parse(received!);
        Assert.Equal("model_query_elements", req.RootElement.GetProperty("tool").GetString());
        Assert.Equal("OST_Levels", req.RootElement.GetProperty("args").GetProperty("category").GetString());
        Assert.Equal(56, req.RootElement.GetProperty("timeout_sec").GetInt32());
        Assert.False(string.IsNullOrEmpty(req.RootElement.GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData(GatewayErrorCodes.Busy)]
    [InlineData(GatewayErrorCodes.Timeout)]
    [InlineData(GatewayErrorCodes.TargetNotReady)]
    [InlineData(GatewayErrorCodes.Conflict)]
    [InlineData(GatewayErrorCodes.ValidationFailed)]
    public async Task BridgeErrors_KeepTheirCode_MessageAndDetails(string code)
    {
        var pipe = NewPipeName();
        var server = RunFakeBridge(pipe, (_, _) => $"{{\"id\":\"x\",\"ok\":false,\"error\":{{\"code\":\"{code}\",\"message\":\"m-{code}\",\"details\":{{\"running_tool\":\"doc_open\"}}}}}}");
        var ex = await Assert.ThrowsAsync<GatewayException>(() => NewClient(pipe).CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        await server;
        Assert.Equal(code, ex.Code);
        Assert.Equal("m-" + code, ex.Message);
        Assert.Equal("doc_open", ((JsonElement)ex.Details!).GetProperty("running_tool").GetString());
    }

    [Fact]
    public async Task UnknownBridgeErrorCode_BecomesInternal()
    {
        var pipe = NewPipeName();
        var server = RunFakeBridge(pipe, (_, _) => "{\"id\":\"x\",\"ok\":false,\"error\":{\"code\":\"WEIRD\",\"message\":\"odd\"}}");
        var ex = await Assert.ThrowsAsync<GatewayException>(() => NewClient(pipe).CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        await server;
        Assert.Equal(GatewayErrorCodes.Internal, ex.Code);
    }

    [Fact]
    public async Task GarbageResponse_IsInternal_NotACrash()
    {
        var pipe = NewPipeName();
        var server = RunFakeBridge(pipe, (_, _) => "this is not json");
        var ex = await Assert.ThrowsAsync<GatewayException>(() => NewClient(pipe).CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        await server;
        Assert.Equal(GatewayErrorCodes.Internal, ex.Code);
    }

    [Fact]
    public async Task SilentBridge_TimesOutWithinTheBudget()
    {
        var pipe = NewPipeName();
        using var stop = new CancellationTokenSource();
        var server = RunFakeBridge(pipe, (_, _) => null, stop.Token);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => NewClient(pipe).CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(2), budget.Token));
        stop.Cancel();
        await server;
        Assert.Equal(GatewayErrorCodes.Timeout, ex.Code);
        Assert.Contains("may still be running", ex.Message);
    }

    [Fact]
    public async Task BrokenConnection_IsDependencyFailed()
    {
        var pipe = NewPipeName();
        var server = RunFakeBridge(pipe, (_, _) => "__close__");
        var ex = await Assert.ThrowsAsync<GatewayException>(() => NewClient(pipe).CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        await server;
        Assert.Equal(GatewayErrorCodes.DependencyFailed, ex.Code);
    }

    [Fact]
    public async Task MissingPipe_IsDependencyFailed_WithInstallGuidance_AndRaisesStateChange()
    {
        var events = new GatewayEvents();
        var changes = new List<(int, bool)>();
        events.BridgeStateChanged += (p, up) => changes.Add((p, up));
        var client = new BridgeClient(events, new FakeRevitProbe().Add(Pid), new BridgeClientOptions { PipeNameForPid = _ => NewPipeName() });
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.DependencyFailed, ex.Code);
        Assert.StartsWith($"RevitAgent Bridge is not loaded in Revit pid {Pid}. Install it with bridge_install and restart that Revit.", ex.Message);
        Assert.Equal(new[] { (Pid, false) }, changes);

        // Same state again: no duplicate notification.
        await Assert.ThrowsAsync<GatewayException>(() => client.CallAsync(Pid, "doc_info", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Single(changes);
    }

    [Fact]
    public async Task UnknownPid_IsNotFound_AndNonRevitPid_IsValidation()
    {
        var probe = new FakeRevitProbe().Add(777, name: "notepad");
        var client = new BridgeClient(new GatewayEvents(), probe, new BridgeClientOptions());
        Assert.Equal(GatewayErrorCodes.NotFound, (await Assert.ThrowsAsync<GatewayException>(() => client.CallAsync(999, "doc_info", null, TimeSpan.FromSeconds(5), CancellationToken.None))).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<GatewayException>(() => client.CallAsync(777, "doc_info", null, TimeSpan.FromSeconds(5), CancellationToken.None))).Code);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, (await Assert.ThrowsAsync<GatewayException>(() => client.CallAsync(0, "doc_info", null, TimeSpan.FromSeconds(5), CancellationToken.None))).Code);
    }

    internal static string NewPipeName() => "revitagent-test-fake-" + Guid.NewGuid().ToString("N")[..10];

    private static BridgeClient NewClient(string pipe, TimeSpan? margin = null) =>
        new(new GatewayEvents(), new FakeRevitProbe().Add(Pid), new BridgeClientOptions
        {
            PipeNameForPid = _ => pipe,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            BridgeTimeoutMargin = margin ?? TimeSpan.FromSeconds(1),
        });

    /// <summary>One-connection fake Bridge. respond returns the response JSON, null = never answer, "__close__" = drop the connection.</summary>
    internal static Task RunFakeBridge(string pipeName, Func<byte[], string, string?> respond, CancellationToken stop = default)
    {
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        return Task.Run(async () =>
        {
            await using (server)
            {
                await server.WaitForConnectionAsync(stop);
                var header = new byte[4];
                await server.ReadExactlyAsync(header, stop);
                var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
                await server.ReadExactlyAsync(payload, stop);
                var response = respond(header, Encoding.UTF8.GetString(payload));
                if (response is null)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, stop);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    return;
                }

                if (response == "__close__")
                {
                    return;
                }

                var body = Encoding.UTF8.GetBytes(response);
                var frame = new byte[4 + body.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
                body.CopyTo(frame, 4);
                await server.WriteAsync(frame, stop);
                await server.FlushAsync(stop);
                try
                {
                    // Keep the connection open until the client has read the reply and disconnected.
                    await server.ReadExactlyAsync(new byte[1], stop);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                }
            }
        });
    }
}
