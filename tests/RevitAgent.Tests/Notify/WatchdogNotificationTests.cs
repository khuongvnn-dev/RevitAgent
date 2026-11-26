using System.IO;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Revit;

namespace RevitAgent.Tests.Notify;

public class WatchdogNotificationTests
{
    /// <summary>Notifications are sent fire-and-forget (Task.Run): wait for them instead of a fixed delay (flaky on a cold run).</summary>
    private static async Task WaitForCallsAsync(FakeNotifier notifier, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (notifier.Calls.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
    }

    private static (AgentPaths paths, LaunchedProcessRegistry registry, FakeNotifier notifier, GatewayEvents events, AuditLog audit) CreateEnv()
    {
        var tempDir = TestGateway.NewTempDir("watchdog_notify_tests_");
        var paths = new AgentPaths(tempDir);
        var registry = new LaunchedProcessRegistry(paths);
        var notifier = new FakeNotifier();
        var events = new GatewayEvents();
        var audit = new AuditLog(paths);
        return (paths, registry, notifier, events, audit);
    }

    [Fact]
    public async Task AddinTrust_DoNotLoad_SendsAddinBlockedNotification_WithScreenshot()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        var shotPath = Path.Combine(paths.CapturesDir, "dummy_addin_blocked.png");
        File.WriteAllBytes(shotPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        using var watchdog = new RevitWatchdog(registry, notifier, events, paths, audit);

        events.RaiseAddinTrust(new AddinTrustEvent(
            Pid: 1234,
            Name: "EvilAddin.App",
            Location: "C:\\Temp\\EvilAddin.dll",
            Decision: "do_not_load",
            ScreenshotPath: shotPath
        ));

        // Allow async Task.Run inside OnAddinTrust to complete
        await WaitForCallsAsync(notifier, 1);

        var call = Assert.Single(notifier.Calls);
        Assert.Equal(NotificationKind.AddinBlocked, call.Kind);
        Assert.Contains("EvilAddin.App", call.Text);
        Assert.Contains("C:\\Temp\\EvilAddin.dll", call.Text);
        Assert.Contains("Revit pid 1234", call.Text);
        Assert.NotNull(call.Images);
        Assert.Equal(shotPath, Assert.Single(call.Images));
    }

    [Fact]
    public async Task AddinTrust_LoadOnce_SendsNoNotification()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();

        using var watchdog = new RevitWatchdog(registry, notifier, events, paths, audit);

        events.RaiseAddinTrust(new AddinTrustEvent(
            Pid: 1234,
            Name: "TrustedAddin.App",
            Location: "C:\\Trusted\\TrustedAddin.dll",
            Decision: "load_once",
            ScreenshotPath: null
        ));

        await Task.Delay(100);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task AddinTrust_ViaStatusEvent_SendsNotificationOnlyForDoNotLoad()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();

        using var watchdog = new RevitWatchdog(registry, notifier, events, paths, audit);

        // Status event with Load Once -> no notification
        events.RaiseStatus("addin_trust", "pid 4567: unsigned add-in 'SafeAddin' (C:\\Safe.dll) → Load Once (allowlist entry 1)");
        await Task.Delay(100);
        Assert.Empty(notifier.Calls);

        // Status event with Do Not Load -> notification sent
        events.RaiseStatus("addin_trust", "pid 4567: unsigned add-in 'UntrustedAddin' (C:\\Untrusted.dll) → Do Not Load (not on the allowlist)");
        await WaitForCallsAsync(notifier, 1);

        var call = Assert.Single(notifier.Calls);
        Assert.Equal(NotificationKind.AddinBlocked, call.Kind);
        Assert.Contains("UntrustedAddin", call.Text);
        Assert.Contains("C:\\Untrusted.dll", call.Text);
        Assert.Contains("Revit pid 4567", call.Text);
    }

    [Fact]
    public async Task AddinTrust_DeduplicatesRapidRepeatedEvents()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();

        using var watchdog = new RevitWatchdog(registry, notifier, events, paths, audit);

        // Fire AddinTrust AND Status for the same addin & pid
        events.RaiseAddinTrust(new AddinTrustEvent(
            Pid: 8888,
            Name: "DuplicateTestAddin",
            Location: "C:\\Dup.dll",
            Decision: "do_not_load",
            ScreenshotPath: null
        ));
        events.RaiseStatus("addin_trust", "pid 8888: unsigned add-in 'DuplicateTestAddin' (C:\\Dup.dll) → Do Not Load");

        await WaitForCallsAsync(notifier, 1);

        // Must be deduplicated: exactly 1 notification sent
        Assert.Single(notifier.Calls);
    }
}
