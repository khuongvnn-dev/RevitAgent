using System.IO;
using System.Diagnostics;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>
/// Host BridgeClient ↔ the real Bridge pipe server core (the code linked into the add-in) over a real
/// named pipe, with a fake executor standing in for Revit's UI thread.
/// </summary>
public class BridgeEndToEndTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task Call_RoundTripsArgsAndResult_OnTheRevitThread()
    {
        using var h = new BridgeHarness();
        var result = await h.Client.CallAsync(BridgeHarness.Pid, "echo", Args(new { category = "OST_Levels", limit = 5, names = new[] { "a", "é" } }), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal("OST_Levels", result.GetProperty("echo").GetProperty("category").GetString());
        Assert.Equal(5, result.GetProperty("echo").GetProperty("limit").GetInt32());
        Assert.Equal("é", result.GetProperty("echo").GetProperty("names")[1].GetString());
        Assert.Equal("fake-revit-ui", result.GetProperty("thread").GetString());
        Assert.Contains(h.StateChanges, s => s == (BridgeHarness.Pid, true));
    }

    [Fact]
    public async Task SequentialCalls_NeverSeeStaleBusy()
    {
        using var h = new BridgeHarness();
        for (var i = 0; i < 25; i++)
        {
            var r = await h.Client.CallAsync(BridgeHarness.Pid, "echo", Args(new { i }), TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(i, r.GetProperty("echo").GetProperty("i").GetInt32());
        }
    }

    [Fact]
    public async Task ConcurrentRevitCall_GetsBusyAfterTheQueueWait_ImmediateToolsStillAnswer()
    {
        using var h = new BridgeHarness(queueWait: TimeSpan.FromMilliseconds(300));
        var slow = h.Client.CallAsync(BridgeHarness.Pid, "slow", null, TimeSpan.FromSeconds(20), CancellationToken.None);
        await WaitUntil(() => h.Server.BusyTool == "slow");

        var sw = Stopwatch.StartNew();
        var busy = await Assert.ThrowsAsync<GatewayException>(() =>
            h.Client.CallAsync(BridgeHarness.Pid, "echo", null, TimeSpan.FromSeconds(20), CancellationToken.None));
        sw.Stop();
        Assert.Equal(GatewayErrorCodes.Busy, busy.Code);
        Assert.Contains("slow", busy.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"BUSY took {sw.Elapsed}");

        var ping = await h.Client.CallAsync(BridgeHarness.Pid, "ping", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("slow", ping.GetProperty("busy_tool").GetString());

        h.Executor.SlowGate.Set();
        var done = await slow;
        Assert.Equal("done", done.GetProperty("slow").GetString());
        var after = await h.Client.CallAsync(BridgeHarness.Pid, "echo", null, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(JsonValueKind.Object, after.ValueKind);
    }

    [Fact]
    public async Task Timeout_WhenRevitNeverStartsTheCall_CancelsItAndFreesTheBridge()
    {
        using var h = new BridgeHarness(margin: TimeSpan.FromSeconds(1));
        h.Ui.Pause(); // e.g. a modal dialog: ExternalEvents are not processed
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            h.Client.CallAsync(BridgeHarness.Pid, "echo", null, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.Timeout, ex.Code);
        Assert.Contains("did NOT run", ex.Message);
        Assert.Null(h.Server.BusyTool);

        h.Ui.Resume();
        var ok = await h.Client.CallAsync(BridgeHarness.Pid, "echo", Args(new { again = true }), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(ok.GetProperty("echo").GetProperty("again").GetBoolean());
        Assert.True(h.Executor.Scheduled.First().Completion.IsCanceled); // the timed-out item never ran
    }

    [Fact]
    public async Task Timeout_WhenTheCallIsStillRunning_SaysSoAndKeepsBusyUntilItEnds()
    {
        using var h = new BridgeHarness(margin: TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            h.Client.CallAsync(BridgeHarness.Pid, "slow", null, TimeSpan.FromSeconds(3), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.Timeout, ex.Code);
        Assert.Contains("may still be running", ex.Message);

        var busy = await Assert.ThrowsAsync<GatewayException>(() =>
            h.Client.CallAsync(BridgeHarness.Pid, "echo", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.Busy, busy.Code);

        h.Executor.SlowGate.Set();
        await WaitUntil(() => h.Server.BusyTool is null);
        await h.Client.CallAsync(BridgeHarness.Pid, "echo", null, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Fact]
    public async Task Errors_AreMappedWithCodesAndDetails_WithoutLeakingInternals()
    {
        using var h = new BridgeHarness();
        var nf = await Assert.ThrowsAsync<GatewayException>(() => h.Client.CallAsync(BridgeHarness.Pid, "fail", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.NotFound, nf.Code);
        Assert.Equal("No such element 42.", nf.Message);
        Assert.Equal(42, ((JsonElement)nf.Details!).GetProperty("id").GetInt32());

        var boom = await Assert.ThrowsAsync<GatewayException>(() => h.Client.CallAsync(BridgeHarness.Pid, "boom", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.Internal, boom.Code);
        Assert.DoesNotContain("secret", boom.Message);
        Assert.DoesNotContain("stack", boom.Message);

        var unknown = await Assert.ThrowsAsync<GatewayException>(() => h.Client.CallAsync(BridgeHarness.Pid, "no_such_tool", null, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.NotFound, unknown.Code);
        Assert.Contains("bridge_install", unknown.Message);

        // The Bridge stays healthy after errors.
        await h.Client.CallAsync(BridgeHarness.Pid, "echo", null, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Fact]
    public async Task ServerCore_HandlesMalformedRequests()
    {
        using var h = new BridgeHarness();
        using var bad = JsonDocument.Parse(await h.Server.HandleRequestAsync("not json"));
        Assert.False(bad.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(GatewayErrorCodes.ValidationFailed, bad.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var noTool = JsonDocument.Parse(await h.Server.HandleRequestAsync("{\"id\":\"x1\"}"));
        Assert.Equal("x1", noTool.RootElement.GetProperty("id").GetString());
        Assert.Equal(GatewayErrorCodes.ValidationFailed, noTool.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var badArgs = JsonDocument.Parse(await h.Server.HandleRequestAsync("{\"id\":\"x2\",\"tool\":\"echo\",\"args\":[1]}"));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, badArgs.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var ok = JsonDocument.Parse(await h.Server.HandleRequestAsync("{\"id\":\"x3\",\"tool\":\"ping\"}"));
        Assert.True(ok.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(ok.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task StoppedServer_IsReportedAsBridgeNotLoaded()
    {
        var h = new BridgeHarness();
        await h.Client.CallAsync(BridgeHarness.Pid, "ping", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        h.Dispose();
        await WaitUntil(() => !h.Client.IsPipePresent(BridgeHarness.Pid));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => h.Client.CallAsync(BridgeHarness.Pid, "ping", null, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.DependencyFailed, ex.Code);
        Assert.Contains($"RevitAgent Bridge is not loaded in Revit pid {BridgeHarness.Pid}. Install it with bridge_install and restart that Revit.", ex.Message);
        Assert.Contains(h.StateChanges, s => s == (BridgeHarness.Pid, false));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition not reached.");
            }

            await Task.Delay(20);
        }
    }
}
