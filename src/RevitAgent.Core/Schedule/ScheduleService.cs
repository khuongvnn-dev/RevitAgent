using System.Globalization;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Scenario;

namespace RevitAgent.Core.Schedule;

/// <summary>
/// Manages the nightly schedule: evaluates timing rules, avoids double fires,
/// notifies missed runs on startup, and fires the batch runner when due.
/// </summary>
public sealed class ScheduleService : IDisposable
{
    private readonly ScheduleConfigStore _configStore;
    private readonly ScenarioBatchRunner _batchRunner;
    private readonly INotifier _notifier;
    private readonly object _lock = new();

    private System.Threading.Timer? _timer;
    private bool _isStarted;

    public ScheduleService(
        ScheduleConfigStore configStore,
        ScenarioBatchRunner batchRunner,
        INotifier notifier)
    {
        _configStore = configStore;
        _batchRunner = batchRunner;
        _notifier = notifier;
    }

    /// <summary>Injectable clock for unit tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public ScheduleConfig GetConfig() => _configStore.Load();

    public void SaveConfig(ScheduleConfig config) => _configStore.Save(config);

    public void Start()
    {
        lock (_lock)
        {
            if (_isStarted) return;
            _isStarted = true;

            // Startup check for missed runs
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckMissedOnStartupAsync(Clock(), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore startup check errors
                }
            });

            // Timer every 30 seconds
            _timer = new System.Threading.Timer(
                _ => _ = TickAsync(Clock(), CancellationToken.None),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isStarted = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// Checks if any scheduled run was missed while the host was off, notifies once, and records it.
    /// </summary>
    public async Task CheckMissedOnStartupAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ScheduleConfig config;
        lock (_lock)
        {
            config = _configStore.Load();
        }

        if (!config.Enabled || config.Scenarios == null || config.Scenarios.Count == 0 || config.Days == null || config.Days.Count == 0)
        {
            return;
        }

        if (!TryParseTimeOfDay(config.Time, out var targetTime))
        {
            return;
        }

        // Check if there was a scheduled run in the past days that was missed
        // Check up to 7 days in the past
        var candidateDates = new List<DateTime>();
        for (var i = 7; i >= 1; i--)
        {
            var pastDate = now.Date.AddDays(-i);
            if (IsDayMatching(pastDate.DayOfWeek, config.Days))
            {
                candidateDates.Add(pastDate);
            }
        }

        // Also check today if now is already past the scheduled time
        if (now.TimeOfDay >= targetTime && IsDayMatching(now.DayOfWeek, config.Days))
        {
            candidateDates.Add(now.Date);
        }

        DateTime? mostRecentMissed = null;
        foreach (var candidate in candidateDates)
        {
            var scheduledDateTime = candidate.Add(targetTime);
            // Was it already fired on or after this candidate?
            if (config.LastFired.HasValue && config.LastFired.Value.Date >= candidate)
            {
                continue;
            }

            // Was it already notified as missed?
            if (config.LastMissedNotified.HasValue && config.LastMissedNotified.Value.Date >= candidate)
            {
                continue;
            }

            mostRecentMissed = candidate;
        }

        if (mostRecentMissed.HasValue)
        {
            var missedDate = mostRecentMissed.Value;
            var text = $"Missed the nightly run {missedDate:yyyy-MM-dd}";

            try
            {
                await _notifier.NotifyAsync(NotificationKind.RunResult, text, null, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best effort
            }

            lock (_lock)
            {
                config.LastMissedNotified = missedDate;
                // Advance LastFired to prevent this missed day from firing on subsequent ticks
                if (!config.LastFired.HasValue || config.LastFired.Value.Date < missedDate)
                {
                    config.LastFired = missedDate.Add(targetTime);
                }
                _configStore.Save(config);
            }
        }
    }

    /// <summary>
    /// Executes one timer evaluation tick.
    /// </summary>
    public async Task<bool> TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ScheduleConfig config;
        lock (_lock)
        {
            config = _configStore.Load();
        }

        if (!config.Enabled || config.Scenarios == null || config.Scenarios.Count == 0 || config.Days == null || config.Days.Count == 0)
        {
            return false;
        }

        if (!TryParseTimeOfDay(config.Time, out var targetTime))
        {
            return false;
        }

        // Day matching
        if (!IsDayMatching(now.DayOfWeek, config.Days))
        {
            return false;
        }

        // Time matching: must be at or after the scheduled time
        if (now.TimeOfDay < targetTime)
        {
            return false;
        }

        // No double fire: if already fired today, skip
        if (config.LastFired.HasValue && config.LastFired.Value.Date == now.Date)
        {
            return false;
        }

        // Mark fired immediately and persist to avoid double runs on crash/restart
        lock (_lock)
        {
            config.LastFired = now;
            _configStore.Save(config);
        }

        // Launch batch execution
        var request = new BatchRequest
        {
            ScenarioDirs = config.Scenarios,
            Trigger = "schedule",
            RevitYear = config.RevitYear,
            SessionId = "schedule",
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await _batchRunner.RunBatchAsync(request, ct).ConfigureAwait(false);
            }
            catch
            {
                // Runner logs errors
            }
        }, ct);

        return true;
    }

    /// <summary>
    /// Calculates the next scheduled run timestamp relative to now.
    /// </summary>
    public DateTimeOffset? CalculateNextRunTime(DateTimeOffset now)
    {
        var config = GetConfig();
        if (!config.Enabled || config.Scenarios == null || config.Scenarios.Count == 0 || config.Days == null || config.Days.Count == 0)
        {
            return null;
        }

        if (!TryParseTimeOfDay(config.Time, out var targetTime))
        {
            return null;
        }

        for (var i = 0; i < 8; i++)
        {
            var candidateDate = now.Date.AddDays(i);
            if (!IsDayMatching(candidateDate.DayOfWeek, config.Days))
            {
                continue;
            }

            var candidateDateTime = new DateTimeOffset(candidateDate.Add(targetTime), now.Offset);
            if (candidateDateTime <= now)
            {
                continue;
            }

            if (candidateDate == now.Date && config.LastFired.HasValue && config.LastFired.Value.Date == now.Date)
            {
                // Already fired today
                continue;
            }

            return candidateDateTime;
        }

        return null;
    }

    public static bool IsDayMatching(DayOfWeek dow, IEnumerable<string> days)
    {
        var dayAbbr = GetDayAbbreviation(dow);
        var fullName = dow.ToString();
        return days.Any(d =>
            string.Equals(d, dayAbbr, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d, fullName, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetDayAbbreviation(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Sunday => "Sun",
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        _ => "Mon",
    };

    private static bool TryParseTimeOfDay(string? timeStr, out TimeSpan time)
    {
        if (string.IsNullOrWhiteSpace(timeStr))
        {
            time = TimeSpan.FromHours(22);
            return false;
        }

        if (TimeSpan.TryParseExact(timeStr.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out time) ||
            TimeSpan.TryParseExact(timeStr.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time) ||
            TimeSpan.TryParse(timeStr.Trim(), CultureInfo.InvariantCulture, out time))
        {
            return true;
        }

        time = TimeSpan.FromHours(22);
        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
