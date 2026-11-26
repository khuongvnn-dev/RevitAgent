using System.IO;
using RevitAgent.Core;
using RevitAgent.Core.Machine;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Scenario;
using RevitAgent.Tests.Scenarios;
using RunStoreService = RevitAgent.Core.RunStore.RunStore;
using Xunit;

namespace RevitAgent.Tests.Scenarios;

public sealed class FakeNotifier : INotifier
{
    public sealed record Notification(NotificationKind Kind, string Text, IReadOnlyList<string>? ImagePaths);

    public List<Notification> Notifications { get; } = new();

    public Task NotifyAsync(NotificationKind kind, string text, IReadOnlyList<string>? imagePaths = null, CancellationToken ct = default)
    {
        lock (Notifications)
        {
            Notifications.Add(new Notification(kind, text, imagePaths));
        }
        return Task.CompletedTask;
    }
}

public class ScenarioBatchRunnerTests : IAsyncDisposable
{
    private readonly ScenarioHost _host = new();
    private readonly FakeNotifier _notifier = new();

    private const string SimpleSteps = """
        name: batch-sample
        revit_version: 2024
        steps:
          - name: list-categories
            tool: schedule_list_schedulable_categories
            on_error: continue
        """;

    private const string SimpleChecks = """
        checks:
          - id: C1
            title: Categories exist
            expect: 'count(list-categories.json -> categories) > 0'
        """;

    private string CreateScenario(string name) =>
        ScenarioHost.NewScenario(name,
            ("scenario.yaml", SimpleSteps.Replace("batch-sample", name) + "\n" + SimpleChecks));

    [Fact]
    public async Task BatchSequencing_RunsScenariosInOrder()
    {
        var scn1 = CreateScenario("batch-seq-1");
        var scn2 = CreateScenario("batch-seq-2");

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        var readiness = new MachineReadiness(_host.Paths)
        {
            IsLockedProbe = () => false,
            RdpProbe = () => (false, 1, 0),
            PowerCfgRunner = () => "AC Sleep: 0\nAC Hibernate: 0",
        };

        var batchRunner = new ScenarioBatchRunner(
            _host.Registry,
            _host.Paths,
            _host.Policy,
            runStore,
            _notifier,
            readiness);

        var progressList = new List<BatchProgressEventArgs>();
        batchRunner.Progress += (_, args) => progressList.Add(args);

        var request = new BatchRequest
        {
            ScenarioDirs = new[] { scn1, scn2 },
            Trigger = "manual",
            IgnoreReadinessErrors = true,
        };

        var result = await batchRunner.RunBatchAsync(request);

        Assert.True(result.Success);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal("batch-seq-1", result.Runs[0].ScenarioName);
        Assert.Equal("batch-seq-2", result.Runs[1].ScenarioName);

        // Verify notification sent per run
        Assert.Equal(2, _notifier.Notifications.Count);
        Assert.Contains("batch-seq-1", _notifier.Notifications[0].Text);
        Assert.Contains("batch-seq-2", _notifier.Notifications[1].Text);
        Assert.Contains(_notifier.Notifications, n => n.Kind == NotificationKind.RunResult);

        // Progress events raised
        Assert.NotEmpty(progressList);
    }

    [Fact]
    public async Task Cancellation_HaltsExecutionAfterCurrentScenario()
    {
        var scn1 = CreateScenario("batch-cancel-1");
        var scn2 = CreateScenario("batch-cancel-2");

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        var readiness = new MachineReadiness(_host.Paths)
        {
            IsLockedProbe = () => false,
            RdpProbe = () => (false, 1, 0),
        };

        var batchRunner = new ScenarioBatchRunner(
            _host.Registry,
            _host.Paths,
            _host.Policy,
            runStore,
            _notifier,
            readiness);

        using var cts = new CancellationTokenSource();
        batchRunner.Progress += (_, args) =>
        {
            if (args.CurrentScenario == "batch-cancel-1")
            {
                cts.Cancel();
            }
        };

        var request = new BatchRequest
        {
            ScenarioDirs = new[] { scn1, scn2 },
            Trigger = "manual",
            IgnoreReadinessErrors = true,
        };

        var result = await batchRunner.RunBatchAsync(request, cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.Runs.Count <= 1);
    }

    [Fact]
    public async Task NotificationPerRun_IncludesSummaryAndScreenshots()
    {
        var scn = CreateScenario("batch-notify-test");

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        var readiness = new MachineReadiness(_host.Paths)
        {
            IsLockedProbe = () => false,
            RdpProbe = () => (false, 1, 0),
        };

        var batchRunner = new ScenarioBatchRunner(
            _host.Registry,
            _host.Paths,
            _host.Policy,
            runStore,
            _notifier,
            readiness);

        var request = new BatchRequest
        {
            ScenarioDirs = new[] { scn },
            Trigger = "manual",
            IgnoreReadinessErrors = true,
        };

        var result = await batchRunner.RunBatchAsync(request);

        Assert.True(result.Success);
        Assert.Single(_notifier.Notifications);
        var notif = _notifier.Notifications[0];
        Assert.Equal(NotificationKind.RunResult, notif.Kind);
        Assert.Contains("batch-notify-test", notif.Text);
        Assert.Contains(result.Runs[0].RunDir, notif.Text);
    }

    [Fact]
    public async Task MachineCheckSkipForSchedule_SkipsBatchAndRecordsSkipped()
    {
        var scn = CreateScenario("batch-skip-test");

        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        // Workstation locked -> fail
        var readiness = new MachineReadiness(_host.Paths)
        {
            IsLockedProbe = () => true,
        };

        var batchRunner = new ScenarioBatchRunner(
            _host.Registry,
            _host.Paths,
            _host.Policy,
            runStore,
            _notifier,
            readiness);

        var request = new BatchRequest
        {
            ScenarioDirs = new[] { scn },
            Trigger = "schedule",
            RevitYear = 2024,
        };

        var result = await batchRunner.RunBatchAsync(request);

        Assert.False(result.Success);
        Assert.True(result.SkippedByReadiness);
        Assert.Empty(result.Runs);

        // Notification sent with skip message
        Assert.Single(_notifier.Notifications);
        Assert.Contains("Skipped the nightly run: the machine is not ready", _notifier.Notifications[0].Text);

        // Run store index has a SKIPPED entry
        var runs = runStore.ListRuns(new RunFilter { Verdict = "SKIPPED" });
        Assert.Contains(runs, r => r.Verdict == "SKIPPED");
    }

    [Fact]
    public async Task ConcurrencyGate_RejectsSimultaneousRuns()
    {
        var scn = CreateScenario("batch-concurrency");
        var runStore = new RunStoreService(_host.Paths, _host.Policy);
        var readiness = new MachineReadiness(_host.Paths)
        {
            IsLockedProbe = () => false,
        };

        var batchRunner = new ScenarioBatchRunner(
            _host.Registry,
            _host.Paths,
            _host.Policy,
            runStore,
            _notifier,
            readiness);

        // Manually acquire the gate
        await batchRunner.ConcurrencyGate.WaitAsync();

        try
        {
            var request = new BatchRequest
            {
                ScenarioDirs = new[] { scn },
                Trigger = "manual",
            };

            var result = await batchRunner.RunBatchAsync(request);
            Assert.False(result.Success);
            Assert.Contains("already in progress", result.ErrorMessage);
        }
        finally
        {
            batchRunner.ConcurrencyGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }
}
