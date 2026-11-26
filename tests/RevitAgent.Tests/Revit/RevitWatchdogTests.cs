using System.IO;
using System.Text.Json;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Revit.Tools;
using RevitAgent.Core.Tools;
using RevitAgent.Tests.Notify;

namespace RevitAgent.Tests.Revit;

public class RevitWatchdogTests
{
    private static (AgentPaths paths, LaunchedProcessRegistry registry, FakeNotifier notifier, GatewayEvents events, AuditLog audit) CreateEnv()
    {
        var tempDir = TestGateway.NewTempDir("revit_watchdog_tests_");
        var paths = new AgentPaths(tempDir);
        var registry = new LaunchedProcessRegistry(paths);
        var notifier = new FakeNotifier();
        var events = new GatewayEvents();
        var audit = new AuditLog(paths);
        return (paths, registry, notifier, events, audit);
    }

    /// <summary>
    /// The watchdog sends notifications fire-and-forget (Task.Run): wait for <paramref name="count"/> calls instead of a fixed
    /// 50 ms, which was too short on a cold first run after a build; the short settle after it still lets an extra call show up.
    /// </summary>
    private static async Task WaitForCallsAsync(FakeNotifier notifier, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (notifier.Calls.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
    }

    private static void RegisterDummyPid(LaunchedProcessRegistry registry, int pid, int year = 2024)
    {
        var entry = new LaunchedProcess
        {
            Pid = pid,
            StartTimeUtc = DateTime.UtcNow,
            ExePath = "C:\\Program Files\\Autodesk\\Revit 2024\\Revit.exe",
            Year = year,
            LaunchedAtUtc = DateTime.UtcNow,
        };
        var list = new List<LaunchedProcess>(registry.ReadAllEntries().Where(e => e.Pid != pid)) { entry };
        File.WriteAllText(registry.FilePath, JsonSerializer.Serialize(list, GatewayJson.Options));
    }

    [Fact]
    public async Task Watchdog_HangEpisode_NotifiesExactlyOnce_ThenNotifiesRecovery()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        int pid = 9101;
        RegisterDummyPid(registry, pid, 2024);

        var now = DateTimeOffset.UtcNow;
        bool responding = false;
        bool hung = true;
        string? title = "Autodesk Revit 2024 - [TestProject.rvt]";

        var statusEvents = new List<(string Cat, string Msg)>();
        events.Status += (cat, msg) => statusEvents.Add((cat, msg));

        var watchdogEvents = new List<RevitWatchdogEvent>();
        using var watchdog = new RevitWatchdog(
            registry,
            notifier,
            events,
            paths,
            audit,
            probe: _ => new ProcessProbeResult(true, false, null, responding, hung, title, new IntPtr(12345)),
            clock: () => now,
            capture: (hwnd, path) =>
            {
                File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                return path;
            },
            journalFinder: _ => "C:\\Journals\\journal.0001.txt"
        );
        watchdog.WatchdogEvent += e => watchdogEvents.Add(e);

        // 1. Initial poll at 0s: start hang tracking, no notification yet (hang_after_sec defaults to 120)
        await watchdog.PollOnceAsync();
        Assert.Empty(notifier.Calls);
        Assert.Empty(watchdogEvents);

        // 2. Poll at 60s: still hanging, < 120s, no notification yet
        now = now.AddSeconds(60);
        await watchdog.PollOnceAsync();
        Assert.Empty(notifier.Calls);

        // 3. Poll at 120s: reached 120s! Exactly one RevitHang notification
        now = now.AddSeconds(60);
        await watchdog.PollOnceAsync();
        // Give async Task.Run a moment if any
        await WaitForCallsAsync(notifier, 1);

        var hangCall = Assert.Single(notifier.Calls);
        Assert.Equal(NotificationKind.RevitHang, hangCall.Kind);
        Assert.Contains("không phản hồi sau 120s", hangCall.Text);
        Assert.Contains("TestProject.rvt", hangCall.Text);
        Assert.NotNull(hangCall.Images);
        Assert.Single(hangCall.Images);

        var hangEv = Assert.Single(watchdogEvents);
        Assert.Equal(RevitWatchdogEventKind.Hang, hangEv.Kind);
        Assert.Equal(pid, hangEv.Pid);
        Assert.Equal(2024, hangEv.Year);

        Assert.Contains(statusEvents, s => s.Cat == "revit_watchdog" && s.Msg.Contains("has not responded"));

        // 4. Poll at 130s: still hanging! Must NOT send duplicate notification in same episode
        now = now.AddSeconds(10);
        await watchdog.PollOnceAsync();
        await WaitForCallsAsync(notifier, 1);
        Assert.Single(notifier.Calls);
        Assert.Single(watchdogEvents);

        // 5. Poll at 140s: process recovers! (responding = true, hung = false)
        responding = true;
        hung = false;
        now = now.AddSeconds(10);
        await watchdog.PollOnceAsync();
        await WaitForCallsAsync(notifier, 2);

        Assert.Equal(2, notifier.Calls.Count);
        var recoveryCall = notifier.Calls[1];
        Assert.Contains("đã phản hồi lại", recoveryCall.Text);

        Assert.Equal(2, watchdogEvents.Count);
        var recoveryEv = watchdogEvents[1];
        Assert.Equal(RevitWatchdogEventKind.Recovered, recoveryEv.Kind);
        Assert.Equal(pid, recoveryEv.Pid);

        Assert.Contains(statusEvents, s => s.Cat == "revit_watchdog" && s.Msg.Contains("is responding again"));

        // 6. Poll at 150s: still responding, no new notifications
        now = now.AddSeconds(10);
        await watchdog.PollOnceAsync();
        await WaitForCallsAsync(notifier, 2);
        Assert.Equal(2, notifier.Calls.Count);
        Assert.Equal(2, watchdogEvents.Count);
    }

    [Fact]
    public async Task Watchdog_CrashDetection_NotifiesWithExitCode_Title_AndJournal()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        int pid = 8822;
        RegisterDummyPid(registry, pid, 2024);

        var now = DateTimeOffset.UtcNow;
        var statusEvents = new List<(string Cat, string Msg)>();
        events.Status += (cat, msg) => statusEvents.Add((cat, msg));
        var watchdogEvents = new List<RevitWatchdogEvent>();

        bool alive = true;
        using var watchdog = new RevitWatchdog(
            registry,
            notifier,
            events,
            paths,
            audit,
            probe: p => alive
                ? new ProcessProbeResult(true, false, null, true, false, "Autodesk Revit 2024", IntPtr.Zero)
                : new ProcessProbeResult(true, true, -1073741819, false, false, null, IntPtr.Zero),
            clock: () => now,
            journalFinder: year => $"C:\\Users\\User\\AppData\\Local\\Autodesk\\Revit\\Autodesk Revit {year}\\Journals\\journal.0999.txt"
        );
        watchdog.WatchdogEvent += e => watchdogEvents.Add(e);

        // First poll while alive to record last known title
        await watchdog.PollOnceAsync();
        Assert.Empty(notifier.Calls);

        // Process exits unexpectedly (crashes with exit code 0xC0000005 = -1073741819)
        alive = false;
        now = now.AddSeconds(5);
        await watchdog.PollOnceAsync();
        await WaitForCallsAsync(notifier, 1);

        var crashCall = Assert.Single(notifier.Calls);
        Assert.Equal(NotificationKind.RevitCrash, crashCall.Kind);
        Assert.Contains("gặp sự cố/crash", crashCall.Text);
        Assert.Contains("-1073741819", crashCall.Text);
        Assert.Contains("Autodesk Revit 2024", crashCall.Text);
        Assert.Contains("journal.0999.txt", crashCall.Text);

        var crashEv = Assert.Single(watchdogEvents);
        Assert.Equal(RevitWatchdogEventKind.Crash, crashEv.Kind);
        Assert.Equal(pid, crashEv.Pid);

        Assert.Contains(statusEvents, s => s.Cat == "revit_watchdog" && s.Msg.Contains("crashed with exit code"));

        // Registry entry must be removed after crash
        Assert.DoesNotContain(registry.ReadAllEntries(), e => e.Pid == pid);
    }

    [Fact]
    public async Task Watchdog_NormalClose_DoesNotTriggerCrashNotification()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        int pid = 7711;
        RegisterDummyPid(registry, pid, 2024);

        var now = DateTimeOffset.UtcNow;
        var watchdogEvents = new List<RevitWatchdogEvent>();

        using var watchdog = new RevitWatchdog(
            registry,
            notifier,
            events,
            paths,
            audit,
            probe: _ => new ProcessProbeResult(false, true, 0, false, false, null, IntPtr.Zero),
            clock: () => now
        );
        watchdog.WatchdogEvent += e => watchdogEvents.Add(e);

        // Mark graceful close as revit_close does
        registry.BeginGracefulClose(pid);

        // Poll
        await watchdog.PollOnceAsync();
        await Task.Delay(50);

        Assert.Empty(notifier.Calls);
        Assert.Empty(watchdogEvents);
    }

    [Fact]
    public async Task Watchdog_IgnoresNonRegisteredProcesses()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        // Do NOT register pid 9999 in registry
        var probedPids = new List<int>();

        using var watchdog = new RevitWatchdog(
            registry,
            notifier,
            events,
            paths,
            audit,
            probe: p =>
            {
                probedPids.Add(p);
                return new ProcessProbeResult(true, false, null, false, true, "Foreign Revit", IntPtr.Zero);
            }
        );

        await watchdog.PollOnceAsync();
        Assert.Empty(probedPids);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task WatchdogStatusTool_ReturnsWatchedPids_States_AndEvents()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        int pid = 5566;
        RegisterDummyPid(registry, pid, 2024);

        using var watchdog = new RevitWatchdog(
            registry,
            notifier,
            events,
            paths,
            audit,
            probe: _ => new ProcessProbeResult(true, false, null, true, false, "Revit Main", IntPtr.Zero)
        );

        await watchdog.PollOnceAsync();

        var tool = new RevitWatchdogStatusTool(watchdog);
        var res = await tool.ExecuteAsync(new ToolCall("revit_watchdog_status", null), CancellationToken.None);

        Assert.False(res.IsError);
        var json = res.Structured!.Value.GetRawText();
        Assert.Contains("\"enabled\":true", json);
        Assert.Contains("\"hang_after_sec\":120", json);
        Assert.Contains("5566", json);
        Assert.Contains("Revit Main", json);
    }

    [Fact]
    public async Task Watchdog_RespectsCustomConfig_InWatchdogJson()
    {
        var (paths, registry, notifier, events, audit) = CreateEnv();
        int pid = 3344;
        RegisterDummyPid(registry, pid, 2024);

        // Write watchdog.json with hang_after_sec: 20
        var configFile = Path.Combine(paths.ConfigDir, WatchdogOptions.FileName);
        File.WriteAllText(configFile, JsonSerializer.Serialize(new { enabled = true, hang_after_sec = 20 }));

        var now = DateTimeOffset.UtcNow;
        using var watchdog = new RevitWatchdog(
            registry,
            notifier,
            events,
            paths,
            audit,
            probe: _ => new ProcessProbeResult(true, false, null, false, true, "Hung Revit", IntPtr.Zero),
            clock: () => now
        );

        // At 0s
        await watchdog.PollOnceAsync();
        Assert.Empty(notifier.Calls);

        // At 20s: triggers because config set hang_after_sec = 20
        now = now.AddSeconds(20);
        await watchdog.PollOnceAsync();
        await WaitForCallsAsync(notifier, 1);

        Assert.Single(notifier.Calls);
        Assert.Contains("không phản hồi sau 20s", notifier.Calls[0].Text);
    }
}
