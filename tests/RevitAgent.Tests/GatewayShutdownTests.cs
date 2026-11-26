using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core.Mcp;

namespace RevitAgent.Tests;

/// <summary>Regression: the dashboard hung on exit (2026-11-20). Gateway shutdown must be quick.</summary>
public class GatewayShutdownTests
{
    [Fact]
    public async Task Gateway_StartedListener_DisposesWithinTwoSeconds()
    {
        var sp = TestGateway.BuildProvider(out _);
        var listener = sp.GetRequiredService<GatewayPipeListener>();
        listener.Start("revitagent-test-" + Guid.NewGuid().ToString("N")[..8]);
        await Task.Delay(200);

        var sw = Stopwatch.StartNew();
        await listener.StopAsync();
        var stop = sw.ElapsedMilliseconds;
        sw.Restart();
        await sp.DisposeAsync();
        var dispose = sw.ElapsedMilliseconds;

        Assert.True(stop < 2000, $"listener.StopAsync took {stop} ms");
        Assert.True(dispose < 2000, $"provider.DisposeAsync took {dispose} ms");
    }
}
