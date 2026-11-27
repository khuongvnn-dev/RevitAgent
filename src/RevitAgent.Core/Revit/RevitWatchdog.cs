using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Revit.Native;
using RevitAgent.Core.Ui;

namespace RevitAgent.Core.Revit;

public enum RevitWatchdogEventKind
{
    Hang,
    Recovered,
    Crash
}

public sealed record RevitWatchdogEvent(
    RevitWatchdogEventKind Kind,
    int Pid,
    int? Year,
    DateTimeOffset At
)
{
    public string KindString => Kind switch
    {
        RevitWatchdogEventKind.Hang => "hang",
        RevitWatchdogEventKind.Recovered => "recovered",
        RevitWatchdogEventKind.Crash => "crash",
        _ => Kind.ToString().ToLowerInvariant()
    };
}

public sealed record ProcessProbeResult(
    bool Exists,
    bool HasExited,
    int? ExitCode,
    bool Responding,
    bool Hung,
    string? MainWindowTitle,
    IntPtr MainWindowHwnd
);

public sealed record WatchedProcessInfo(
    int Pid,
    int? Year,
    string State,
    DateTimeOffset? HungSince,
    double? HungSeconds,
    string? LastKnownTitle
);

public sealed record RevitWatchdogStatus(
    bool Enabled,
    int HangAfterSec,
    IReadOnlyList<int> WatchedPids,
    IReadOnlyList<WatchedProcessInfo> Processes,
    IReadOnlyList<RevitWatchdogEvent> LastEvents
);

public sealed record WatchdogOptions
{
    public const string FileName = "watchdog.json";

    public bool Enabled { get; init; } = true;
    public int HangAfterSec { get; init; } = 120;

    public static WatchdogOptions Load(AgentPaths paths)
    {
        var file = Path.Combine(paths.ConfigDir, FileName);
        if (!File.Exists(file))
        {
            return new WatchdogOptions();
        }

        try
        {
            var json = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool enabled = true;
            if (root.TryGetProperty("enabled", out var pEnabled) && (pEnabled.ValueKind == JsonValueKind.True || pEnabled.ValueKind == JsonValueKind.False))
            {
                enabled = pEnabled.GetBoolean();
            }
            int hangSec = 120;
            if (root.TryGetProperty("hang_after_sec", out var pHang) && pHang.TryGetInt32(out var sec))
            {
                hangSec = sec;
            }
            return new WatchdogOptions { Enabled = enabled, HangAfterSec = hangSec };
        }
        catch
        {
            return new WatchdogOptions();
        }
    }
}

/// <summary>
/// Background service monitoring Revit processes launched by the gateway.
/// Detects hangs and crashes without ever killing or touching the target processes.
/// Subscribes to unsigned add-in blocked events to notify the owner.
/// </summary>
public sealed class RevitWatchdog : IDisposable, IAsyncDisposable
{
    private sealed class ProcessState
    {
        public int Pid { get; init; }
        public int? Year { get; set; }
        public DateTimeOffset? HungSince { get; set; }
        public bool NotifiedHang { get; set; }
        public string? LastKnownTitle { get; set; }
        public string? LastEvent { get; set; }
    }

    private readonly LaunchedProcessRegistry _launched;
    private readonly INotifier _notifier;
    private readonly GatewayEvents _events;
    private readonly AgentPaths _paths;
    private readonly AuditLog _auditLog;

    private readonly Func<int, ProcessProbeResult> _probe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IntPtr, string, string?> _capture;
    private readonly Func<int?, string?> _journalFinder;

    private readonly ConcurrentDictionary<int, ProcessState> _states = new();
    private readonly List<RevitWatchdogEvent> _lastEvents = new();
    private readonly object _eventsGate = new();
    private const int MaxEventsHistory = 50;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentAddinBlocked = new();

    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;
    private readonly object _lifecycleGate = new();

    public event Action<RevitWatchdogEvent>? WatchdogEvent;

    public RevitWatchdog(
        LaunchedProcessRegistry launched,
        INotifier notifier,
        GatewayEvents events,
        AgentPaths paths,
        AuditLog auditLog,
        Func<int, ProcessProbeResult>? probe = null,
        Func<DateTimeOffset>? clock = null,
        Func<IntPtr, string, string?>? capture = null,
        Func<int?, string?>? journalFinder = null)
    {
        _launched = launched ?? throw new ArgumentNullException(nameof(launched));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));

        _probe = probe ?? DefaultProbe;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _capture = capture ?? DefaultCapture;
        _journalFinder = journalFinder ?? DefaultJournalFinder;

        _events.AddinTrust += OnAddinTrust;
        _events.Status += OnStatus;
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _pollLoopTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lifecycleGate)
        {
            cts = _cts;
            task = _pollLoopTask;
            _cts = null;
            _pollLoopTask = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            try
            {
                task?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Ignored during shutdown
            }
            cts.Dispose();
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _auditLog.Diagnostic("Watchdog poll error", ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Executes one polling cycle. Public for deterministic unit testing.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var options = WatchdogOptions.Load(_paths);
        if (!options.Enabled)
        {
            return;
        }

        var now = _clock();
        var registered = _launched.ReadAllEntries();
        var registeredPids = registered.Select(r => r.Pid).ToHashSet();

        // Prune memory state for PIDs no longer registered
        foreach (var pid in _states.Keys)
        {
            if (!registeredPids.Contains(pid))
            {
                _states.TryRemove(pid, out _);
            }
        }

        foreach (var entry in registered)
        {
            ct.ThrowIfCancellationRequested();

            if (_launched.IsGracefulClosing(entry.Pid))
            {
                _states.TryRemove(entry.Pid, out _);
                continue;
            }

            var state = _states.GetOrAdd(entry.Pid, p => new ProcessState
            {
                Pid = p,
                Year = entry.Year
            });
            state.Year ??= entry.Year;

            ProcessProbeResult probe;
            try
            {
                probe = _probe(entry.Pid);
            }
            catch (Exception ex)
            {
                _auditLog.Diagnostic($"Watchdog probe failed for pid {entry.Pid}", ex);
                continue;
            }

            if (!probe.Exists || probe.HasExited)
            {
                // CRASH: Process exited while still registered (not closed via revit_close)
                await HandleCrashAsync(entry, state, probe, now, ct).ConfigureAwait(false);
                continue;
            }

            // ALIVE: observe hang and recovery
            HandleLiveness(entry, state, probe, options, now, ct);
        }
    }

    private async Task HandleCrashAsync(
        LaunchedProcess entry,
        ProcessState state,
        ProcessProbeResult probe,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var exitCodeStr = probe.ExitCode.HasValue ? probe.ExitCode.Value.ToString() : "unknown";
        var lastTitle = state.LastKnownTitle ?? "(none)";
        var journalPath = _journalFinder(entry.Year);

        var message = $"Revit {entry.Year} (pid {entry.Pid}) crashed (exit code: {exitCodeStr}, title: '{lastTitle}')." +
                      (journalPath != null ? $"\nJournal: {journalPath}" : string.Empty);

        try
        {
            await _notifier.NotifyAsync(NotificationKind.RevitCrash, message, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _auditLog.Diagnostic($"Failed to send RevitCrash notification for pid {entry.Pid}", ex);
        }

        _events.RaiseStatus("revit_watchdog", $"Revit {entry.Year} (pid {entry.Pid}) crashed with exit code {exitCodeStr}.");

        var ev = new RevitWatchdogEvent(RevitWatchdogEventKind.Crash, entry.Pid, entry.Year, now);
        RecordEvent(ev);
        try
        {
            WatchdogEvent?.Invoke(ev);
        }
        catch (Exception ex)
        {
            _auditLog.Diagnostic("WatchdogEvent subscriber failed", ex);
        }

        _launched.Remove(entry.Pid);
        _states.TryRemove(entry.Pid, out _);
    }

    private void HandleLiveness(
        LaunchedProcess entry,
        ProcessState state,
        ProcessProbeResult probe,
        WatchdogOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(probe.MainWindowTitle))
        {
            state.LastKnownTitle = probe.MainWindowTitle;
        }

        bool isHung = probe.Hung || !probe.Responding;
        if (isHung)
        {
            state.HungSince ??= now;
            var elapsedSec = (now - state.HungSince.Value).TotalSeconds;

            if (elapsedSec >= options.HangAfterSec && !state.NotifiedHang)
            {
                // Hang episode notification
                string? shotPath = null;
                if (probe.MainWindowHwnd != IntPtr.Zero)
                {
                    try
                    {
                        var fileName = $"watchdog_hang_{entry.Pid}_{now:yyyyMMdd_HHmmss}.png";
                        var path = Path.Combine(_paths.CapturesDir, fileName);
                        shotPath = _capture(probe.MainWindowHwnd, path);
                    }
                    catch (Exception ex)
                    {
                        _auditLog.Diagnostic($"Failed to capture screenshot for hung pid {entry.Pid}", ex);
                    }
                }

                var title = state.LastKnownTitle ?? probe.MainWindowTitle ?? "(none)";
                var images = shotPath != null && File.Exists(shotPath) ? new[] { shotPath } : null;
                var text = $"Revit {entry.Year} (pid {entry.Pid}) is not responding after {options.HangAfterSec}s (title: '{title}').";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _notifier.NotifyAsync(NotificationKind.RevitHang, text, images, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _auditLog.Diagnostic($"Failed to send RevitHang notification for pid {entry.Pid}", ex);
                    }
                }, ct);

                _events.RaiseStatus("revit_watchdog", $"Revit (pid {entry.Pid}) has not responded for {options.HangAfterSec}s.");

                var ev = new RevitWatchdogEvent(RevitWatchdogEventKind.Hang, entry.Pid, entry.Year, now);
                RecordEvent(ev);
                try
                {
                    WatchdogEvent?.Invoke(ev);
                }
                catch (Exception ex)
                {
                    _auditLog.Diagnostic("WatchdogEvent subscriber failed", ex);
                }

                state.NotifiedHang = true;
                state.LastEvent = "hang";
            }
        }
        else
        {
            // Responding normally
            if (state.NotifiedHang)
            {
                // Recovery from hang episode
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _notifier.NotifyAsync(NotificationKind.RevitHang, $"Revit (pid {entry.Pid}) is responding again.", ct: ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _auditLog.Diagnostic($"Failed to send Revit recovery notification for pid {entry.Pid}", ex);
                    }
                }, ct);

                _events.RaiseStatus("revit_watchdog", $"Revit (pid {entry.Pid}) is responding again.");

                var ev = new RevitWatchdogEvent(RevitWatchdogEventKind.Recovered, entry.Pid, entry.Year, now);
                RecordEvent(ev);
                try
                {
                    WatchdogEvent?.Invoke(ev);
                }
                catch (Exception ex)
                {
                    _auditLog.Diagnostic("WatchdogEvent subscriber failed", ex);
                }

                state.LastEvent = "recovered";
            }

            state.HungSince = null;
            state.NotifiedHang = false;
        }
    }

    private void RecordEvent(RevitWatchdogEvent ev)
    {
        lock (_eventsGate)
        {
            _lastEvents.Add(ev);
            if (_lastEvents.Count > MaxEventsHistory)
            {
                _lastEvents.RemoveAt(0);
            }
        }
    }

    public RevitWatchdogStatus GetStatus()
    {
        var options = WatchdogOptions.Load(_paths);
        var registered = _launched.ReadAllEntries();
        var now = _clock();

        var processes = new List<WatchedProcessInfo>();
        foreach (var entry in registered)
        {
            if (_states.TryGetValue(entry.Pid, out var state))
            {
                var hungSec = state.HungSince.HasValue ? (now - state.HungSince.Value).TotalSeconds : (double?)null;
                processes.Add(new WatchedProcessInfo(
                    Pid: entry.Pid,
                    Year: entry.Year ?? state.Year,
                    State: state.HungSince.HasValue ? "hung" : "normal",
                    HungSince: state.HungSince,
                    HungSeconds: hungSec.HasValue ? Math.Round(hungSec.Value, 1) : null,
                    LastKnownTitle: state.LastKnownTitle
                ));
            }
            else
            {
                processes.Add(new WatchedProcessInfo(
                    Pid: entry.Pid,
                    Year: entry.Year,
                    State: "normal",
                    HungSince: null,
                    HungSeconds: null,
                    LastKnownTitle: null
                ));
            }
        }

        IReadOnlyList<RevitWatchdogEvent> events;
        lock (_eventsGate)
        {
            events = _lastEvents.ToList();
        }

        return new RevitWatchdogStatus(
            Enabled: options.Enabled,
            HangAfterSec: options.HangAfterSec,
            WatchedPids: registered.Select(r => r.Pid).ToList(),
            Processes: processes,
            LastEvents: events
        );
    }

    private void OnAddinTrust(AddinTrustEvent e)
    {
        try
        {
            if (string.Equals(e.Decision, "do_not_load", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Decision, "Do Not Load", StringComparison.OrdinalIgnoreCase))
            {
                var key = $"{e.Pid}_{e.Name}";
                var now = _clock();
                if (_recentAddinBlocked.TryGetValue(key, out var lastSent) && (now - lastSent).TotalSeconds < 5)
                {
                    return;
                }
                _recentAddinBlocked[key] = now;

                var text = $"{e.Name}\n{e.Location}\nRevit pid {e.Pid}";
                var images = !string.IsNullOrWhiteSpace(e.ScreenshotPath) && File.Exists(e.ScreenshotPath)
                    ? new[] { e.ScreenshotPath }
                    : null;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _notifier.NotifyAsync(NotificationKind.AddinBlocked, text, images).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _auditLog.Diagnostic("Failed to send AddinBlocked notification", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _auditLog.Diagnostic("Error handling AddinTrust event", ex);
        }
    }

    private static readonly Regex AddinStatusRegex = new(@"pid\s+(\d+):\s+unsigned add-in\s+'([^']*)'\s+\(([^)]*)\)\s+→\s+(.*)", RegexOptions.Compiled);

    private void OnStatus(string category, string message)
    {
        if (!string.Equals(category, "addin_trust", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var match = AddinStatusRegex.Match(message);
            if (!match.Success)
            {
                return;
            }

            var pid = int.Parse(match.Groups[1].Value);
            var name = match.Groups[2].Value;
            var location = match.Groups[3].Value;
            var decision = match.Groups[4].Value;

            if (decision.Contains("Do Not Load", StringComparison.OrdinalIgnoreCase) ||
                decision.Contains("do_not_load", StringComparison.OrdinalIgnoreCase))
            {
                var key = $"{pid}_{name}";
                var now = _clock();
                if (_recentAddinBlocked.TryGetValue(key, out var lastSent) && (now - lastSent).TotalSeconds < 5)
                {
                    return;
                }
                _recentAddinBlocked[key] = now;

                var text = $"{name}\n{location}\nRevit pid {pid}";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _notifier.NotifyAsync(NotificationKind.AddinBlocked, text).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _auditLog.Diagnostic("Failed to send AddinBlocked notification from status", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _auditLog.Diagnostic("Error parsing addin_trust status", ex);
        }
    }

    private static ProcessProbeResult DefaultProbe(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            using (process)
            {
                if (process.HasExited)
                {
                    int? exitCode = null;
                    try { exitCode = process.ExitCode; } catch { }
                    return new ProcessProbeResult(true, true, exitCode, false, false, null, IntPtr.Zero);
                }

                var windows = Win32Windows.TopLevelWindows(pid);
                var main = Win32Windows.FindMainWindow(windows);
                var hung = main?.Hung ?? false;
                bool responding;
                try
                {
                    responding = process.Responding;
                }
                catch
                {
                    responding = !hung;
                }

                return new ProcessProbeResult(
                    Exists: true,
                    HasExited: false,
                    ExitCode: null,
                    Responding: responding && !hung,
                    Hung: hung || !responding,
                    MainWindowTitle: main?.Title,
                    MainWindowHwnd: main?.Hwnd ?? IntPtr.Zero
                );
            }
        }
        catch (ArgumentException)
        {
            return new ProcessProbeResult(false, true, null, false, false, null, IntPtr.Zero);
        }
        catch (Exception)
        {
            return new ProcessProbeResult(false, false, null, false, false, null, IntPtr.Zero);
        }
    }

    private static string? DefaultCapture(IntPtr hwnd, string path)
    {
        try
        {
            var bytes = WindowCapture.SavePng(hwnd, path);
            return bytes != null ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? DefaultJournalFinder(int? year)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var yearStr = year?.ToString() ?? "2024";
            var journalsDir = Path.Combine(localAppData, "Autodesk", "Revit", $"Autodesk Revit {yearStr}", "Journals");
            if (!Directory.Exists(journalsDir))
            {
                return null;
            }

            var files = Directory.GetFiles(journalsDir, "journal.*.txt");
            if (files.Length == 0)
            {
                files = Directory.GetFiles(journalsDir);
            }

            if (files.Length == 0)
            {
                return null;
            }

            return files
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _events.AddinTrust -= OnAddinTrust;
        _events.Status -= OnStatus;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
