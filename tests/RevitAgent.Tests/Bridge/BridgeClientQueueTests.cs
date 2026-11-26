using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>Calls to one Revit pid run one after another (Revit's API is single-threaded); a wait that runs out is BUSY.</summary>
public class BridgeClientQueueTests
{
    private const int Pid = 616161;

    [Fact]
    public async Task ParallelCalls_RunOneAfterAnother()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        using var stop = new CancellationTokenSource();
        var fake = new FakeBridge(pipe, _ => 150, stop.Token);
        var client = NewClient(pipe, TimeSpan.FromSeconds(10));

        var calls = Enumerable.Range(0, 4)
            .Select(i => client.CallAsync(Pid, "model_query_elements", JsonSerializer.SerializeToElement(new { i }), TimeSpan.FromSeconds(30), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(calls);
        stop.Cancel();

        Assert.Equal(4, fake.Handled);
        Assert.Equal(1, fake.MaxConcurrent);
    }

    [Fact]
    public async Task QueuedCall_GetsBusy_AfterTheWait_NamingTheRunningTool()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        using var stop = new CancellationTokenSource();
        var fake = new FakeBridge(pipe, tool => tool == "doc_open" ? 3000 : 0, stop.Token);
        var client = NewClient(pipe, TimeSpan.FromMilliseconds(300));

        var slow = client.CallAsync(Pid, "doc_open", null, TimeSpan.FromSeconds(30), CancellationToken.None);
        await fake.WaitForFirstRequestAsync();
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.CallAsync(Pid, "model_get_warnings", null, TimeSpan.FromSeconds(30), CancellationToken.None));

        Assert.Equal(GatewayErrorCodes.Busy, ex.Code);
        var details = JsonSerializer.SerializeToElement(ex.Details);
        Assert.Equal("doc_open", details.GetProperty("busy_tool").GetString());
        Assert.True(details.GetProperty("waited_ms").GetInt64() >= 250);
        await slow; // the in-flight call is unaffected
        stop.Cancel();
        Assert.Equal(1, fake.Handled);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotHoldTheSlot()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        using var stop = new CancellationTokenSource();
        var fake = new FakeBridge(pipe, tool => tool == "doc_open" ? 800 : 0, stop.Token);
        var client = NewClient(pipe, TimeSpan.FromSeconds(10));

        var first = client.CallAsync(Pid, "doc_open", null, TimeSpan.FromSeconds(30), CancellationToken.None);
        await fake.WaitForFirstRequestAsync();
        using var cts = new CancellationTokenSource();
        var waiter = client.CallAsync(Pid, "model_get_warnings", null, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAsync<GatewayException>(() => waiter);

        await first;
        // The slot is free again: a later call runs normally.
        await client.CallAsync(Pid, "model_query_elements", null, TimeSpan.FromSeconds(30), CancellationToken.None);
        stop.Cancel();
        Assert.Equal(2, fake.Handled);
    }

    [Fact]
    public async Task CancelledRunningCall_ReleasesTheSlot()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        using var stop = new CancellationTokenSource();
        var fake = new FakeBridge(pipe, tool => tool == "doc_open" ? 60_000 : 0, stop.Token);
        var client = NewClient(pipe, TimeSpan.FromSeconds(10));

        using var cts = new CancellationTokenSource();
        var running = client.CallAsync(Pid, "doc_open", null, TimeSpan.FromSeconds(120), cts.Token);
        await fake.WaitForFirstRequestAsync();
        var next = client.CallAsync(Pid, "model_query_elements", null, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAsync<GatewayException>(() => running);

        await next.WaitAsync(TimeSpan.FromSeconds(20));
        stop.Cancel();
    }

    private static BridgeClient NewClient(string pipe, TimeSpan queueWait) =>
        new(new GatewayEvents(), new FakeRevitProbe().Add(Pid), new BridgeClientOptions
        {
            PipeNameForPid = _ => pipe,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            BridgeTimeoutMargin = TimeSpan.FromSeconds(1),
            QueueWait = queueWait,
        });

    /// <summary>
    /// Fake Bridge that accepts many sequential connections and, like the real one, would answer BUSY if two
    /// requests overlapped; it records the highest overlap seen. delayMs(tool) is how long a request "runs".
    /// </summary>
    private sealed class FakeBridge
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrent;

        public FakeBridge(string pipeName, Func<string, int> delayMs, CancellationToken stop)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await server.WaitForConnectionAsync(stop);
                        _ = Task.Run(() => ServeAsync(server, delayMs, stop));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public int Handled { get; private set; }

        public int MaxConcurrent { get; private set; }

        public Task WaitForFirstRequestAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private async Task ServeAsync(NamedPipeServerStream server, Func<string, int> delayMs, CancellationToken stop)
        {
            await using (server)
            {
                try
                {
                    var header = new byte[4];
                    await server.ReadExactlyAsync(header, stop);
                    var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
                    await server.ReadExactlyAsync(payload, stop);
                    using var doc = JsonDocument.Parse(payload);
                    var id = doc.RootElement.GetProperty("id").GetString();
                    var tool = doc.RootElement.GetProperty("tool").GetString()!;
                    var now = Interlocked.Increment(ref _concurrent);
                    lock (this)
                    {
                        MaxConcurrent = Math.Max(MaxConcurrent, now);
                    }

                    _first.TrySetResult();
                    try
                    {
                        await Task.Delay(delayMs(tool), stop);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _concurrent);
                    }

                    var body = Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"ok\":true,\"result\":{{}}}}");
                    var frame = new byte[4 + body.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
                    body.CopyTo(frame, 4);
                    await server.WriteAsync(frame, stop);
                    await server.FlushAsync(stop);
                    lock (this)
                    {
                        Handled++;
                    }

                    await server.ReadExactlyAsync(new byte[1], stop);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
                {
                }
            }
        }
    }
}
