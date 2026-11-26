using System.IO;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Scenarios;

public class ScenarioResilienceTests
{
    private const string BaseSteps = """
        name: resilience-test
        revit_version: 2024
        retry_default: 0
        retry_delay_sec: 0
        hang_timeout_sec: 10
        rerun_on_crash: 0
        steps:
          - name: query
            tool: model_query_elements
            args: { category: Levels }
        """;

    private const string BaseChecks = """
        checks:
          - id: C1
            title: Pass rule
            expect: 'count(query.json -> elements) == 3'
        """;

    /// <summary>scenario.yaml = metadata + steps, then the checks (default: one count check on the query result).</summary>
    private static string CreateResilienceScenario(string? steps = null, string? checks = null) =>
        ScenarioHost.NewScenario("resilience-test",
            ("scenario.yaml", (steps ?? BaseSteps) + "\n" + (checks ?? BaseChecks)));

    [Fact]
    public async Task StepRetry_OnTransientError_RetriesUpToN_AndLogsAttempts()
    {
        await using var host = new ScenarioHost();
        var stepsYaml = """
            name: resilience-test
            revit_version: 2024
            retry_default: 0
            steps:
              - name: flaky
                tool: flaky_tool
                retry: 2
                retry_delay_sec: 0
            """;
        var checks = """
            checks:
              - id: C1
                title: Step ok
                expect: 'step_ok(flaky)'
            """;
        var dir = CreateResilienceScenario(stepsYaml, checks);

        int callCount = 0;
        host.Revit.FlakyHandler = _ =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new GatewayException(GatewayErrorCodes.Timeout, "temporary timeout");
            }

            return new { ok = true, count = 3 };
        };

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, pid = FakeRevit.ExistingPid });

        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        Assert.Equal(2, callCount);
        Assert.Equal(1, s.GetProperty("steps").GetProperty("succeeded").GetInt32());

        var runDir = s.GetProperty("run_dir").GetString()!;
        var statusPath = Path.Combine(runDir, "data", "step_status.json");
        Assert.True(File.Exists(statusPath));

        using var statusDoc = JsonDocument.Parse(File.ReadAllText(statusPath));
        var steps = statusDoc.RootElement.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Single(steps);
        var attempts = steps[0].GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(1, attempts[0].GetProperty("attempt").GetInt32());
        Assert.Equal("TIMEOUT", attempts[0].GetProperty("status").GetString());
        Assert.Contains("temporary timeout", attempts[0].GetProperty("error").GetString());
        Assert.Equal(2, attempts[1].GetProperty("attempt").GetInt32());
        Assert.Equal("SUCCEEDED", attempts[1].GetProperty("status").GetString());

        var md = File.ReadAllText(Path.Combine(runDir, "report.md"));
        Assert.Contains("Step retries", md);
        Assert.Contains("Step `flaky` (2 attempts)", md);
        Assert.Contains("Attempt 1: TIMEOUT", md);
        Assert.Contains("Attempt 2: SUCCEEDED", md);
    }

    [Fact]
    public async Task StepRetry_ExhaustsAttempts_StopsAndReportsFailed()
    {
        await using var host = new ScenarioHost();
        var steps = """
            name: resilience-test
            revit_version: 2024
            steps:
              - name: flaky
                tool: flaky_tool
                retry: 2
                retry_delay_sec: 0
            """;
        var dir = CreateResilienceScenario(steps);

        int callCount = 0;
        host.Revit.FlakyHandler = _ =>
        {
            callCount++;
            throw new GatewayException(GatewayErrorCodes.Internal, $"failure attempt {callCount}");
        };

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, pid = FakeRevit.ExistingPid });

        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Equal(3, callCount); // 1 initial + 2 retries
        Assert.Equal(1, s.GetProperty("steps").GetProperty("failed").GetInt32());

        var runDir = s.GetProperty("run_dir").GetString()!;
        var statusPath = Path.Combine(runDir, "data", "step_status.json");
        using var statusDoc = JsonDocument.Parse(File.ReadAllText(statusPath));
        var attempts = statusDoc.RootElement.GetProperty("steps")[0].GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.Equal(1, attempts[0].GetProperty("attempt").GetInt32());
        Assert.Equal(2, attempts[1].GetProperty("attempt").GetInt32());
        Assert.Equal(3, attempts[2].GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task NoRetry_OnCheckVerdictFail()
    {
        await using var host = new ScenarioHost();
        var steps = """
            name: resilience-test
            revit_version: 2024
            steps:
              - name: check_step
                tool: flaky_tool
                retry: 3
                retry_delay_sec: 0
            """;
        var dir = CreateResilienceScenario(steps);

        int callCount = 0;
        host.Revit.FlakyHandler = _ =>
        {
            callCount++;
            // Check step returning a FAIL verdict
            return ToolResult.Error(new GatewayException("FAIL", "check assertion failed"));
        };

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, pid = FakeRevit.ExistingPid });

        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        Assert.Equal(1, callCount); // MUST NOT RETRY on FAIL verdict!
        Assert.Equal(1, s.GetProperty("steps").GetProperty("failed").GetInt32());

        var runDir = s.GetProperty("run_dir").GetString()!;
        var statusPath = Path.Combine(runDir, "data", "step_status.json");
        using var statusDoc = JsonDocument.Parse(File.ReadAllText(statusPath));
        var attempts = statusDoc.RootElement.GetProperty("steps")[0].GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Single(attempts);
        Assert.Equal("FAILED", attempts[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task ActionStep_WithoutRetrySafe_IsNotRetried_WithRetrySafe_IsRetried()
    {
        await using var host = new ScenarioHost();

        // 1. Action step without retry_safe: false
        var steps1 = """
            name: resilience-test
            revit_version: 2024
            steps:
              - name: action
                tool: flaky_action_tool
                retry: 3
                retry_delay_sec: 0
            """;
        var dir1 = CreateResilienceScenario(steps1);

        int calls1 = 0;
        host.Revit.FlakyHandler = _ =>
        {
            calls1++;
            throw new GatewayException(GatewayErrorCodes.Timeout, "timeout");
        };

        var s1 = await host.CallOkAsync("scenario_run", new { scenario_dir = dir1, pid = FakeRevit.ExistingPid });
        Assert.Equal(1, calls1); // Not retried because retry_safe is false by default for action steps!

        // 2. Action step with retry_safe: true
        var steps2 = """
            name: resilience-test
            revit_version: 2024
            steps:
              - name: action
                tool: flaky_action_tool
                retry: 2
                retry_delay_sec: 0
                retry_safe: true
            """;
        var checks2 = """
            checks:
              - id: C1
                title: Action step ok
                expect: 'step_ok(action)'
            """;
        var dir2 = CreateResilienceScenario(steps2, checks2);

        int calls2 = 0;
        host.Revit.FlakyHandler = _ =>
        {
            calls2++;
            if (calls2 == 1)
            {
                throw new GatewayException(GatewayErrorCodes.Timeout, "timeout");
            }

            return new { ok = true, count = 3 };
        };

        var s2 = await host.CallOkAsync("scenario_run", new { scenario_dir = dir2, pid = FakeRevit.ExistingPid });
        Assert.Equal("PASS", s2.GetProperty("verdict").GetString());
        Assert.Equal(2, calls2); // Retried because retry_safe: true
    }

    [Fact]
    public async Task CrashDetection_ExitedMidStep_AbortsWithRevitCrashed_AndCopiesJournal()
    {
        await using var host = new ScenarioHost();
        var dir = CreateResilienceScenario();

        // Prepare fake journal file
        var journalSrc = Path.Combine(host.Paths.RunsDir, "journal.0099.txt");
        File.WriteAllText(journalSrc, "'C 25-Nov-2026 Revit fatal crash log line\n");
        host.Revit.JournalPath = journalSrc;

        // Configure process exit on step liveness check
        host.Revit.Crashed = true;
        host.Revit.ExitCode = 137;
        host.Revit.MainWindowTitle = "Revit Fatal Error Dialog";

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, launch = true });

        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        var err = s.GetProperty("error").GetString()!;
        Assert.Contains("revit_crashed", err);
        Assert.Contains("137", err);
        Assert.Contains("Revit Fatal Error Dialog", err);

        var runDir = s.GetProperty("run_dir").GetString()!;
        var copiedJournal = Path.Combine(runDir, "logs", "journal.0099.txt");
        Assert.True(File.Exists(copiedJournal), "Journal must be copied into logs folder");
        Assert.Equal(File.ReadAllText(journalSrc), File.ReadAllText(copiedJournal));
        Assert.True(File.Exists(journalSrc), "Source journal must not be deleted or moved");

        // Verify result.json and report.md
        var res = File.ReadAllText(Path.Combine(runDir, "result.json"));
        Assert.Contains("revit_crashed", res);
        var md = File.ReadAllText(Path.Combine(runDir, "report.md"));
        Assert.Contains("revit_crashed", md);
    }

    [Fact]
    public async Task HangDetection_NotResponding_TakesScreenshotAndAbortsWithRevitHung()
    {
        await using var host = new ScenarioHost();
        var steps = """
            name: resilience-test
            revit_version: 2024
            hang_timeout_sec: 2
            steps:
              - name: query
                tool: model_query_elements
                args: { category: Levels }
            """;
        var dir = CreateResilienceScenario(steps);

        var baseTime = DateTimeOffset.Now;
        var fakeScreenshot = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        host.Revit.Screenshot = fakeScreenshot;

        int probeCount = 0;
        host.Revit.Now = () => baseTime.AddSeconds(probeCount * 3); // Advance 3s each probe
        host.Revit.ProcessLiveness = pid =>
        {
            probeCount++;
            return new ScenarioRunner.RevitProcessLiveness(
                HasExited: false,
                ExitCode: null,
                Responding: false, // Unresponsive!
                MainWindowTitle: "Autodesk Revit (Not Responding)");
        };

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, launch = true });

        Assert.Equal("ERROR", s.GetProperty("verdict").GetString());
        var err = s.GetProperty("error").GetString()!;
        Assert.Contains("revit_hung", err);

        var runDir = s.GetProperty("run_dir").GetString()!;
        var evidenceDir = Path.Combine(runDir, "evidence");
        var shots = Directory.GetFiles(evidenceDir, "revit_hung_*.png");
        Assert.NotEmpty(shots);
        Assert.Equal(fakeScreenshot, File.ReadAllBytes(shots[0]));
    }

    [Fact]
    public async Task RerunOnCrash_CreatesSecondRunLinkedToFirst()
    {
        await using var host = new ScenarioHost();
        var steps = """
            name: resilience-test
            revit_version: 2024
            rerun_on_crash: 1
            steps:
              - name: query
                tool: model_query_elements
                args: { category: Levels }
            """;
        var dir = CreateResilienceScenario(steps);

        int runIndex = 0;
        host.Revit.ProcessLiveness = pid =>
        {
            // First run crashes, second run is healthy
            if (runIndex == 0)
            {
                runIndex = 1;
                return new ScenarioRunner.RevitProcessLiveness(true, -1, false, "Crashed");
            }

            return new ScenarioRunner.RevitProcessLiveness(false, null, true, "Ready");
        };

        var s = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, launch = true });

        // Second run passed!
        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        var secondRunId = s.GetProperty("run_id").GetString()!;
        var secondRunDir = s.GetProperty("run_dir").GetString()!;
        var prevRunId = s.GetProperty("previous_run_id").GetString()!;
        var rerunOf = s.GetProperty("rerun_of").GetString()!;

        Assert.NotEmpty(prevRunId);
        Assert.Equal(prevRunId, rerunOf);
        Assert.NotEqual(secondRunId, prevRunId);

        // Check run.json of second run
        var runJsonPath = Path.Combine(secondRunDir, "run.json");
        Assert.True(File.Exists(runJsonPath), $"run.json must exist at {runJsonPath}");
        var runJsonText = File.ReadAllText(runJsonPath);
        using var runJsonDoc = JsonDocument.Parse(runJsonText);
        Assert.True(runJsonDoc.RootElement.TryGetProperty("previous_run_id", out var pElem), $"run.json missing previous_run_id. Content: {runJsonText}");
        Assert.Equal(prevRunId, pElem.GetString());
        Assert.Equal(prevRunId, runJsonDoc.RootElement.GetProperty("rerun_of").GetString());
    }

    [Fact]
    public async Task Validation_RejectsInvalidKeysAndRanges()
    {
        await using var host = new ScenarioHost();

        // 1. Scenario-level retry_safe is rejected
        var v1 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v1",
            ("scenario.yaml", "name: v1\nretry_safe: true\nsteps:\n  - tool: doc_info\n")), host.Registry);
        Assert.False(v1.Ok);
        Assert.Contains(v1.Errors, e => e.Code == "retry-safe-scenario");

        // 2. Scenario-level retry_default out of range
        var v2 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v2",
            ("scenario.yaml", "name: v2\nretry_default: 6\nsteps:\n  - tool: doc_info\n")), host.Registry);
        Assert.False(v2.Ok);
        Assert.Contains(v2.Errors, e => e.Code == "retry-default-range");

        // 3. Scenario-level retry_delay_sec negative
        var v3 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v3",
            ("scenario.yaml", "name: v3\nretry_delay_sec: -1\nsteps:\n  - tool: doc_info\n")), host.Registry);
        Assert.False(v3.Ok);
        Assert.Contains(v3.Errors, e => e.Code == "retry-delay-range");

        // 4. Scenario-level hang_timeout_sec <= 0
        var v4 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v4",
            ("scenario.yaml", "name: v4\nhang_timeout_sec: 0\nsteps:\n  - tool: doc_info\n")), host.Registry);
        Assert.False(v4.Ok);
        Assert.Contains(v4.Errors, e => e.Code == "hang-timeout-range");

        // 5. Scenario-level rerun_on_crash not 0 or 1
        var v5 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v5",
            ("scenario.yaml", "name: v5\nrerun_on_crash: 2\nsteps:\n  - tool: doc_info\n")), host.Registry);
        Assert.False(v5.Ok);
        Assert.Contains(v5.Errors, e => e.Code == "rerun-on-crash-range");

        // 6. Step-level retry out of range
        var v6 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v6",
            ("scenario.yaml", "name: v6\nsteps:\n  - tool: doc_info\n    retry: 7\n")), host.Registry);
        Assert.False(v6.Ok);
        Assert.Contains(v6.Errors, e => e.Code == "step-retry-range");

        // 7. Step-level retry_delay_sec negative
        var v7 = ScenarioValidator.Validate(ScenarioHost.NewScenario("v7",
            ("scenario.yaml", "name: v7\nsteps:\n  - tool: doc_info\n    retry_delay_sec: -3\n")), host.Registry);
        Assert.False(v7.Ok);
        Assert.Contains(v7.Errors, e => e.Code == "step-retry-delay-range");
    }
}
