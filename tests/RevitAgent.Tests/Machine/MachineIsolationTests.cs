using RevitAgent.Core;
using RevitAgent.Core.Machine;

namespace RevitAgent.Tests.Machine;

public class MachineIsolationTests
{
    [Fact]
    public void ThrowingCheck_IsIsolated_ReturnsWarnItem_AndDoesNotCrashReport()
    {
        var home = TestGateway.NewTempDir("revitagent_home_");
        var paths = new AgentPaths(home);

        var readiness = new MachineReadiness(paths)
        {
            // Inject an unhandled throwing probe to test RunIsolated
            IsLockedProbe = () => throw new InvalidOperationException("Simulation: locked probe exploded"),
            // Inject a throwing powercfg runner
            PowerCfgRunner = () => throw new InvalidOperationException("Simulation: powercfg failed with an error"),
            RdpProbe = () => (false, 0, 0),
            InstallationsProvider = () => Array.Empty<RevitAgent.Core.Revit.RevitInstallation>(),
            BridgeListProvider = () => Array.Empty<object>()
        };

        var report = readiness.Check(new MachineCheckOptions { Year = 2024 });

        Assert.NotNull(report);
        Assert.NotEmpty(report.Items);

        // Find the locked check item (caught by RunIsolated)
        var lockedItem = Assert.Single(report.Items, i => i.Id == "locked");
        Assert.Equal(MachineCheckStatus.Warn, lockedItem.Status);
        Assert.Contains("InvalidOperationException", lockedItem.Detail);
        Assert.NotNull(lockedItem.Fix);

        // Find the sleep check item
        var sleepItem = Assert.Single(report.Items, i => i.Id == "sleep");
        Assert.Equal(MachineCheckStatus.Warn, sleepItem.Status);
        Assert.Contains("InvalidOperationException", sleepItem.Detail);
        Assert.NotNull(sleepItem.Fix);
    }
}
