using System.Diagnostics;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Machine;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Progress information dispatched during batch execution.
/// </summary>
public sealed class BatchProgressEventArgs : EventArgs
{
    public string? CurrentScenario { get; init; }

    public int ScenarioIndex { get; init; }

    public int TotalScenarios { get; init; }

    public string? CurrentStep { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? StatusMessage { get; init; }

    public ScenarioRunSummary? LastRunSummary { get; init; }
}

/// <summary>
/// Request parameters for a batch execution.
/// </summary>
public sealed class BatchRequest
{
    public required IReadOnlyList<string> ScenarioDirs { get; init; }

    public string Trigger { get; init; } = "manual";

    public int? RevitYear { get; init; }

    public bool IgnoreReadinessErrors { get; init; }

    public string SessionId { get; init; } = "local";
}

/// <summary>
/// Outcome of a batch execution.
/// </summary>
public sealed class BatchResult
{
    public bool Success { get; init; }

    public bool SkippedByReadiness { get; init; }

    public bool Cancelled { get; init; }

    public string? ErrorMessage { get; init; }

    public MachineReadinessReport? ReadinessReport { get; init; }

    public IReadOnlyList<ScenarioRunSummary> Runs { get; init; } = Array.Empty<ScenarioRunSummary>();
}

/// <summary>
/// Sequentially executes a batch of scenarios through ScenarioRunner, enforces single-concurrency,
/// runs machine readiness checks before execution, emits progress events, and sends Telegram notifications per run.
/// </summary>
public sealed class ScenarioBatchRunner
{
    public SemaphoreSlim ConcurrencyGate { get; } = new(1, 1);

    private readonly ToolRegistry _registry;
    private readonly AgentPaths _paths;
    private readonly GatewayPolicy _policy;
    private readonly RunStore.RunStore _runStore;
    private readonly INotifier _notifier;
    private readonly MachineReadiness _machineReadiness;

    private readonly object _stateLock = new();
    private CancellationTokenSource? _currentCts;
    private Stopwatch? _batchStopwatch;
    private string? _currentScenarioName;
    private int _currentScenarioIndex;
    private int _totalScenarios;

    public ScenarioBatchRunner(
        ToolRegistry registry,
        AgentPaths paths,
        GatewayPolicy policy,
        RunStore.RunStore runStore,
        INotifier notifier,
        MachineReadiness? machineReadiness = null)
    {
        _registry = registry;
        _paths = paths;
        _policy = policy;
        _runStore = runStore;
        _notifier = notifier;
        _machineReadiness = machineReadiness ?? new MachineReadiness(paths);
    }

    public bool IsRunning { get; private set; }

    public event EventHandler<BatchProgressEventArgs>? Progress;

    public void Cancel()
    {
        lock (_stateLock)
        {
            _currentCts?.Cancel();
        }
    }

    public async Task<BatchResult> RunBatchAsync(BatchRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || request.ScenarioDirs == null || request.ScenarioDirs.Count == 0)
        {
            return new BatchResult { Success = true, ErrorMessage = "No scenarios provided." };
        }

        if (!await ConcurrencyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BatchResult
            {
                Success = false,
                ErrorMessage = "Another scenario run or batch is already in progress on this gateway.",
            };
        }

        CancellationTokenSource linkedCts;
        lock (_stateLock)
        {
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts = _currentCts;
            IsRunning = true;
            _batchStopwatch = Stopwatch.StartNew();
            _totalScenarios = request.ScenarioDirs.Count;
            _currentScenarioIndex = 0;
            _currentScenarioName = null;
        }

        var runs = new List<ScenarioRunSummary>();
        var ct = linkedCts.Token;

        try
        {
            // 1. Machine Readiness Check before batch execution
            var readinessYear = request.RevitYear ?? 2024;
            var report = await _machineReadiness.CheckAsync(new MachineCheckOptions { Year = readinessYear }, ct).ConfigureAwait(false);
            var failedItems = report.Items
                .Where(i => string.Equals(i.Status, MachineCheckStatus.Fail, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (failedItems.Count > 0)
            {
                if (string.Equals(request.Trigger, "schedule", StringComparison.OrdinalIgnoreCase))
                {
                    // Skip schedule batch, notify Telegram, record skipped in run store index
                    var failedDetails = string.Join("\n", failedItems.Select(f => $"- {f.Title}: {f.Detail}"));
                    var notifyText = $"Skipped the nightly run: the machine is not ready\n{failedDetails}";

                    try
                    {
                        await _notifier.NotifyAsync(NotificationKind.RunResult, notifyText, null, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Notification failure should not throw
                    }

                    var batchName = request.ScenarioDirs.Count == 1
                        ? Path.GetFileName(request.ScenarioDirs[0].TrimEnd('\\', '/'))
                        : $"nightly_batch_{request.ScenarioDirs.Count}";

                    _runStore.RecordSkippedRun(batchName, trigger: "schedule", reason: failedDetails, revitYear: readinessYear);

                    return new BatchResult
                    {
                        Success = false,
                        SkippedByReadiness = true,
                        ReadinessReport = report,
                        ErrorMessage = "Skipped the nightly run: the machine is not ready",
                    };
                }
                else if (!request.IgnoreReadinessErrors)
                {
                    return new BatchResult
                    {
                        Success = false,
                        SkippedByReadiness = true,
                        ReadinessReport = report,
                        ErrorMessage = "Machine readiness checks failed; manual confirmation required.",
                    };
                }
            }

            // 2. Sequential execution
            for (var i = 0; i < request.ScenarioDirs.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var scenarioDir = request.ScenarioDirs[i];
                var scenarioName = Path.GetFileName(scenarioDir.TrimEnd('\\', '/'));

                lock (_stateLock)
                {
                    _currentScenarioIndex = i + 1;
                    _currentScenarioName = scenarioName;
                }

                RaiseProgress(new BatchProgressEventArgs
                {
                    CurrentScenario = scenarioName,
                    ScenarioIndex = i + 1,
                    TotalScenarios = request.ScenarioDirs.Count,
                    CurrentStep = "Starting",
                    Elapsed = _batchStopwatch.Elapsed,
                    StatusMessage = $"Starting scenario {i + 1}/{request.ScenarioDirs.Count}: {scenarioName}",
                });

                var runner = new ScenarioRunner(_registry, _paths, _policy, _runStore);
                var options = new ScenarioRunOptions
                {
                    Scenario = scenarioDir,
                    Launch = true,
                    KeepOpen = false,
                    Trigger = request.Trigger,
                    SessionId = request.SessionId,
                };

                ScenarioRunSummary summary;
                var scenarioStopwatch = Stopwatch.StartNew();

                try
                {
                    summary = await runner.RunAsync(options, ct).ConfigureAwait(false);
                    scenarioStopwatch.Stop();
                    runs.Add(summary);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    scenarioStopwatch.Stop();
                    RaiseProgress(new BatchProgressEventArgs
                    {
                        CurrentScenario = scenarioName,
                        ScenarioIndex = i + 1,
                        TotalScenarios = request.ScenarioDirs.Count,
                        CurrentStep = "Failed",
                        Elapsed = _batchStopwatch.Elapsed,
                        StatusMessage = $"Scenario {scenarioName} failed: {ex.Message}",
                    });
                    continue;
                }

                // Format notification per run: "<scenario>: <verdict> (pass/fail counts, duration)\n<run folder>"
                var passCount = summary.Checks.Count(r => r.Verdict == Verdicts.Pass);
                var failCount = summary.Checks.Count(r => r.Verdict == Verdicts.Fail);
                var countsText = $"{passCount} pass, {failCount} fail";
                var durText = $"{Math.Round(scenarioStopwatch.Elapsed.TotalSeconds, 1)}s";
                var notifyText = $"{summary.ScenarioName}: {summary.Verdict} ({countsText}, {durText})\n{summary.RunDir}";

                // Evidence screenshots: up to 2
                IReadOnlyList<string>? screenshots = null;
                var evidenceDir = Path.Combine(summary.RunDir, "evidence");
                if (Directory.Exists(evidenceDir))
                {
                    try
                    {
                        screenshots = Directory.EnumerateFiles(evidenceDir, "*.png")
                            .Concat(Directory.EnumerateFiles(evidenceDir, "*.jpg"))
                            .Take(2)
                            .ToList();
                    }
                    catch
                    {
                        // Best effort
                    }
                }

                try
                {
                    await _notifier.NotifyAsync(NotificationKind.RunResult, notifyText, screenshots, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore notification exceptions
                }

                RaiseProgress(new BatchProgressEventArgs
                {
                    CurrentScenario = summary.ScenarioName,
                    ScenarioIndex = i + 1,
                    TotalScenarios = request.ScenarioDirs.Count,
                    CurrentStep = "Finished",
                    Elapsed = _batchStopwatch.Elapsed,
                    StatusMessage = $"Finished {summary.ScenarioName}: {summary.Verdict}",
                    LastRunSummary = summary,
                });
            }

            var wasCancelled = ct.IsCancellationRequested;
            return new BatchResult
            {
                Success = !wasCancelled,
                Cancelled = wasCancelled,
                ReadinessReport = report,
                Runs = runs,
            };
        }
        finally
        {
            lock (_stateLock)
            {
                IsRunning = false;
                _batchStopwatch?.Stop();
                _currentCts?.Dispose();
                _currentCts = null;
            }
            ConcurrencyGate.Release();
        }
    }

    public void OnCompleted(ToolCall call, IGatewayTool tool, ToolResult result, TimeSpan duration)
    {
        if (IsRunning && call.SessionId.Contains("/scenario-", StringComparison.OrdinalIgnoreCase))
        {
            var outcome = result.IsError ? "FAIL" : "OK";
            RaiseProgress(new BatchProgressEventArgs
            {
                CurrentScenario = _currentScenarioName,
                ScenarioIndex = _currentScenarioIndex,
                TotalScenarios = _totalScenarios,
                CurrentStep = $"{tool.Name} ({outcome})",
                Elapsed = _batchStopwatch?.Elapsed ?? TimeSpan.Zero,
                StatusMessage = $"Tool {tool.Name} -> {outcome} ({duration.TotalMilliseconds:F0}ms)",
            });
        }
    }

    private void RaiseProgress(BatchProgressEventArgs args)
    {
        try
        {
            Progress?.Invoke(this, args);
        }
        catch
        {
            // Subscribers must not crash the runner
        }
    }
}
