using System.IO;
using RevitAgent.Core;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Schedule;
using RevitAgent.Tests.Scenarios;
using RunStoreService = RevitAgent.Core.RunStore.RunStore;
using Xunit;

namespace RevitAgent.Tests.Schedule;

public class ScheduleServiceTests : IAsyncDisposable
{
    private readonly ScenarioHost _host = new();
    private readonly FakeNotifier _notifier = new();

    private ScheduleService CreateService(ScheduleConfig initialConfig, Func<DateTimeOffset>? clock = null)
    {
        var configStore = new ScheduleConfigStore(_host.Paths);
        configStore.Save(initialConfig);

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        var batchRunner = new ScenarioBatchRunner(
            _host.Registry,
            _host.Paths,
            _host.Policy,
            runStore,
            _notifier);

        var service = new ScheduleService(configStore, batchRunner, _notifier);
        if (clock != null)
        {
            service.Clock = clock;
        }

        return service;
    }

    [Fact]
    public async Task DayAndTimeMatching_FiresAtOrAfterScheduledTime()
    {
        // Monday 2026-11-23
        var monday = new DateTimeOffset(2026, 11, 23, 21, 55, 0, TimeSpan.FromHours(7));
        var config = new ScheduleConfig
        {
            Enabled = true,
            Time = "22:00",
            Days = new List<string> { "Mon", "Wed", "Fri" },
            Scenarios = new List<string> { "dummy-scenario" },
            RevitYear = 2024,
        };

        var service = CreateService(config, () => monday);

        // 1. Tick at 21:55:00 on Monday -> should NOT fire
        var fired1 = await service.TickAsync(monday);
        Assert.False(fired1);

        // 2. Tick at 22:01:00 on Monday -> SHOULD fire
        var mondayAtOrAfter = monday.AddMinutes(6); // 22:01
        var fired2 = await service.TickAsync(mondayAtOrAfter);
        Assert.True(fired2);

        // 3. Tick at 22:05:00 on Tuesday -> should NOT fire (Tuesday not in Days)
        var tuesday = monday.AddDays(1).AddMinutes(10); // Tuesday 22:05
        var fired3 = await service.TickAsync(tuesday);
        Assert.False(fired3);
    }

    [Fact]
    public async Task NoDoubleFire_SkipsSubsequentTicksOnSameDay()
    {
        var monday = new DateTimeOffset(2026, 11, 23, 22, 0, 0, TimeSpan.FromHours(7));
        var config = new ScheduleConfig
        {
            Enabled = true,
            Time = "22:00",
            Days = new List<string> { "Mon" },
            Scenarios = new List<string> { "dummy-scenario" },
        };

        var service = CreateService(config);

        // First tick at 22:00 -> fires
        var first = await service.TickAsync(monday);
        Assert.True(first);

        // Second tick at 22:05 -> does NOT fire
        var second = await service.TickAsync(monday.AddMinutes(5));
        Assert.False(second);

        // Third tick at 22:30 -> does NOT fire
        var third = await service.TickAsync(monday.AddMinutes(30));
        Assert.False(third);
    }

    [Fact]
    public async Task CheckMissedOnStartup_NotifiesMissedRunWhileHostWasOff()
    {
        // Scheduled every Monday at 22:00
        var sundayFired = new DateTimeOffset(2026, 11, 22, 22, 0, 0, TimeSpan.FromHours(7));
        var config = new ScheduleConfig
        {
            Enabled = true,
            Time = "22:00",
            Days = new List<string> { "Mon" },
            Scenarios = new List<string> { "dummy-scenario" },
            LastFired = sundayFired,
        };

        // Host starts on Tuesday at 08:00 AM (Monday 22:00 was missed while off)
        var tuesdayStart = new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.FromHours(7));
        var service = CreateService(config);

        await service.CheckMissedOnStartupAsync(tuesdayStart);

        // Verifies notification was sent for missed run
        Assert.Single(_notifier.Notifications);
        var notif = _notifier.Notifications[0];
        Assert.Equal(NotificationKind.RunResult, notif.Kind);
        Assert.Contains("Missed the nightly run 2026-11-23", notif.Text);

        // Second check on startup should NOT notify again
        await service.CheckMissedOnStartupAsync(tuesdayStart.AddHours(1));
        Assert.Single(_notifier.Notifications);
    }

    [Fact]
    public void CalculateNextRunTime_FindsUpcomingScheduledSlot()
    {
        var config = new ScheduleConfig
        {
            Enabled = true,
            Time = "22:00",
            Days = new List<string> { "Mon", "Wed", "Fri" },
            Scenarios = new List<string> { "dummy-scenario" },
        };

        var service = CreateService(config);

        // Monday at 10:00 AM -> next is Monday at 22:00
        var mondayMorning = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(7));
        var next1 = service.CalculateNextRunTime(mondayMorning);
        Assert.NotNull(next1);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 22, 0, 0, TimeSpan.FromHours(7)), next1.Value);

        // Monday at 23:00 (already fired) -> next is Wednesday at 22:00
        config.LastFired = mondayMorning.AddHours(12); // Monday 22:00
        service.SaveConfig(config);
        var mondayNight = new DateTimeOffset(2026, 11, 23, 23, 0, 0, TimeSpan.FromHours(7));
        var next2 = service.CalculateNextRunTime(mondayNight);
        Assert.NotNull(next2);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 22, 0, 0, TimeSpan.FromHours(7)), next2.Value);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }
}
