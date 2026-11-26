using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario;

public sealed class ScenarioRunOptions
{
    /// <summary>Scenario folder or path of its scenario.yaml.</summary>
    public required string Scenario { get; init; }

    /// <summary>Use this already-running Revit (never closed by the run).</summary>
    public int? Pid { get; init; }

    /// <summary>Start Revit <c>revit_version</c> with revit_launch and close it at the end.</summary>
    public bool Launch { get; init; }

    /// <summary>Leave the document (and a launched Revit) open after the run, for inspection.</summary>
    public bool KeepOpen { get; init; }

    /// <summary>Folder substituted for <c>{dataset}</c>.</summary>
    public string? DatasetDir { get; init; }

    /// <summary>MCP session of the caller (inner calls run under "&lt;session&gt;/scenario-&lt;run&gt;").</summary>
    public string SessionId { get; init; } = "local";

    /// <summary>Trigger of the run (manual|schedule|agent|cli).</summary>
    public string Trigger { get; init; } = "manual";

    /// <summary>True if this run is an automatic rerun (prevents recursive reruns).</summary>
    public bool IsRerun { get; init; }

    /// <summary>If this run is a rerun, the run ID of the failed prior run.</summary>
    public string? PreviousRunId { get; init; }
}

/// <summary>One executed (or skipped) step, as logged in report.md and step_status.json.</summary>
public sealed class StepRecord
{
    public int Index { get; init; }

    public required string Code { get; init; }

    public required string Kind { get; init; }

    public required string Target { get; init; }

    public string Status { get; set; } = "SKIPPED";

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset? Started { get; set; }

    public long DurationMs { get; set; }

    /// <summary>Data file written for the step, relative to RUN_DIR.</summary>
    public string? DataFile { get; set; }

    public bool Setup { get; init; }

    /// <summary>Execution attempts for this step (recorded on retry).</summary>
    public List<StepAttemptRecord> Attempts { get; } = new();
}

/// <summary>Everything a finished run produced (returned to the agent in compact form).</summary>
public sealed class ScenarioRunSummary
{
    public required string ScenarioName { get; init; }

    public required string RunId { get; init; }

    public required string RunDir { get; init; }

    public required string Verdict { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<StepRecord> Steps { get; init; } = Array.Empty<StepRecord>();

    public IReadOnlyList<CheckEvaluation> Checks { get; init; } = Array.Empty<CheckEvaluation>();

    public int? Pid { get; init; }

    public bool Launched { get; init; }

    public string? PreviousRunId { get; init; }

    public string? RerunOf { get; init; }

    public required IReadOnlyDictionary<string, object?> Cleanup { get; init; }

    public object ToJson()
    {
        return new
        {
            scenario = ScenarioName,
            run_id = RunId,
            previous_run_id = PreviousRunId,
            rerun_of = RerunOf,
            verdict = Verdict,
            error = Error,
            checks = new
            {
                total = Checks.Count,
                pass = Checks.Count(r => r.Verdict == Verdicts.Pass),
                fail = Checks.Count(r => r.Verdict == Verdicts.Fail),
                not_measurable = Checks.Count(r => r.Verdict == Verdicts.NotMeasurable),
            },
            steps = new
            {
                total = Steps.Count,
                succeeded = Steps.Count(s => s.Status == "SUCCEEDED"),
                failed = Steps.Count(s => s.Status == "FAILED"),
                blocked = Steps.Count(s => s.Status == "BLOCKED"),
                error = Steps.Count(s => s.Status == "ERROR"),
                skipped = Steps.Count(s => s.Status == "SKIPPED"),
            },
            needs_attention = Checks.Where(r => r.Verdict != Verdicts.Pass).Take(10).Select(r => new
            {
                id = r.Id,
                severity = r.Severity,
                verdict = r.Verdict,
                detail = ResultFields.Clip(r.Detail, 300),
            }).ToArray(),
            failed_steps = Steps.Where(s => s.Status is "FAILED" or "ERROR").Take(5).Select(s => new { step = s.Code, status = s.Status, message = s.Message }).ToArray(),
            pid = Pid,
            launched_revit = Launched,
            run_dir = RunDir,
            reports = new
            {
                report_md = Path.Combine(RunDir, "report.md"),
                report_html = Path.Combine(RunDir, "report.html"),
                result_json = Path.Combine(RunDir, "result.json"),
                run_info = Path.Combine(RunDir, "run-info.json"),
                data = Path.Combine(RunDir, "data"),
            },
            cleanup = Cleanup,
            next = Verdict == Verdicts.Pass
                ? "All checks PASS. Keep the scenario (commit it) and replay it with scenario_run - no AI needed."
                : "Open report.md (section 'Needs attention') and data\\*.json; fix the add-in or the scenario, then run again.",
        };
    }
}

/// <summary>
/// Runs a scenario end to end: RUN_DIR in the sandbox → attach or
/// launch Revit → copy + open the model copy (verify it is inside RUN_DIR) → steps (readiness check before each, result
/// saved to data\&lt;name&gt;.json) → cleanup (ALWAYS: close the copy without saving, close Revit only if this run launched
/// it) → step_status.json, check evaluation, result.json, report.md, report.html, run-info.json.
/// Every gateway call goes through <see cref="ToolRegistry.InvokeAsync(ToolCall, CancellationToken)"/> (policy, audit).
/// </summary>
public sealed class ScenarioRunner
{
    public const string RunnerName = "scenario_run";

    internal static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] BusyStates = { "busy", "starting", "loading", "opening" };
    private static readonly string[] BlockedStates = { "blocked", "modal", "modal_blocked", "not_responding", "hung", "exited", "not_running", "crashed", "dead" };

    public sealed record RevitProcessLiveness(bool HasExited, int? ExitCode, bool Responding, string? MainWindowTitle);

    private readonly ToolRegistry _registry;
    private readonly AgentPaths _paths;
    private readonly GatewayPolicy _policy;
    private readonly RunStore.RunStore _runStore;

    public ScenarioRunner(ToolRegistry registry, AgentPaths paths, GatewayPolicy policy, RunStore.RunStore? runStore = null)
    {
        _registry = registry;
        _paths = paths;
        _policy = policy;
        _runStore = runStore ?? new RunStore.RunStore(paths, policy);
    }

    /// <summary>Delay between readiness probes while Revit reports busy (tests shorten it).</summary>
    public TimeSpan BusyProbeDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a busy Revit is waited for before the step is an ENV error.</summary>
    public TimeSpan BusyWaitLimit { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Optional test hook for probing Revit process liveness (exit, response, title).</summary>
    public Func<int, RevitProcessLiveness>? ProcessLivenessProbe { get; init; }

    /// <summary>Optional test hook for locating the newest Revit journal path.</summary>
    public Func<int, string?>? JournalPathFinder { get; init; }

    /// <summary>Optional test hook for capturing screenshot bytes on hang.</summary>
    public Func<int, byte[]?>? ScreenshotCapture { get; init; }

    /// <summary>Optional test hook for clock time.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    private DateTimeOffset Now => Clock?.Invoke() ?? DateTimeOffset.Now;

    public async Task<ScenarioRunSummary> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var validation = ScenarioValidator.Validate(options.Scenario, _registry, options.DatasetDir);
        if (!validation.Ok || validation.Scenario is null)
        {
            var errors = validation.Errors.ToArray();
            throw GatewayException.Validation(
                $"Scenario has {errors.Length} error(s); nothing was run. Fix them (scenario_validate shows the same list) and retry.",
                new { errors = errors.Take(20).Select(e => e.ToJson()).ToArray() });
        }

        var scenario = validation.Scenario;
        if (options.Pid is not null && options.Launch)
        {
            throw GatewayException.Validation("Pass either pid (use a running Revit) or launch: true (start a new one), not both.");
        }

        if (options.Launch && scenario.RevitVersion is null)
        {
            throw GatewayException.Validation("launch: true needs 'revit_version: <year>' in scenario.yaml.");
        }

        var needsRevit = scenario.Model is not null || scenario.Steps.Any(s => s.IsUi);
        if (needsRevit && options.Pid is null && !options.Launch)
        {
            throw GatewayException.Validation("This scenario opens a model or drives the UI: pass pid (from revit_list_instances) or launch: true.");
        }

        var started = Now;
        var (runDir, runId, sandboxRunId) = await CreateRunDirAsync(scenario, options, cancellationToken).ConfigureAwait(false);
        var ctx = new RunContext(scenario, options, runDir, runId, sandboxRunId, started);
        ctx.Log($"scenario_run {scenario.Name} — run {runId} in {runDir}");
        SnapshotScenario(ctx);
        WriteRunInfo(ctx, final: false);

        try
        {
            await AttachRevitAsync(ctx, cancellationToken).ConfigureAwait(false);
            if (scenario.Model is not null)
            {
                await OpenModelAsync(ctx, cancellationToken).ConfigureAwait(false);
            }

            await RunStepsAsync(ctx, cancellationToken).ConfigureAwait(false);
        }
        catch (ScenarioAbortException ex)
        {
            ctx.Abort(ex.Class, ex.Message);
        }
        catch (OperationCanceledException)
        {
            ctx.Abort("ENV", "Run cancelled (client cancelled or scenario_run exceeded its time budget).");
        }
        catch (Exception ex)
        {
            ctx.Abort("UNKNOWN", $"Runner failure ({ex.GetType().Name}): {ResultFields.Clip(ex.Message, 300)}");
        }
        finally
        {
            ctx.SkipRemaining();
            await CleanupAsync(ctx).ConfigureAwait(false);
        }

        var summary = Finish(ctx);

        if (ctx.EnvErrorClass == "revit_crashed" && ctx.Scenario.RerunOnCrash == 1 && !options.IsRerun)
        {
            ctx.Log($"rerun_on_crash: 1 → triggering rerun of scenario {scenario.Name} into a new run folder");
            var rerunOptions = new ScenarioRunOptions
            {
                Scenario = options.Scenario,
                Pid = null,
                Launch = true,
                KeepOpen = options.KeepOpen,
                DatasetDir = options.DatasetDir,
                SessionId = options.SessionId,
                Trigger = options.Trigger,
                IsRerun = true,
                PreviousRunId = ctx.RunId,
            };
            return await RunAsync(rerunOptions, cancellationToken).ConfigureAwait(false);
        }

        return summary;
    }

    // ---- 1. RUN_DIR -------------------------------------------------------------------------------

    private async Task<(string RunDir, string RunId, string? SandboxRunId)> CreateRunDirAsync(ScenarioDefinition scenario, ScenarioRunOptions options, CancellationToken ct)
    {
        string runDir;
        string runId;
        string? sandboxRunId = null;
        if (_registry.TryGet("sandbox_create_run", out var create))
        {
            var args = new JsonObject();
            if (ResultFields.HasArgument(create, "name"))
            {
                args["name"] = scenario.Name;
            }

            if (options.PreviousRunId is not null && ResultFields.HasArgument(create, "previous_run_id"))
            {
                args["previous_run_id"] = options.PreviousRunId;
                args["rerun_of"] = options.PreviousRunId;
            }

            var r = await _registry.InvokeAsync(new ToolCall("sandbox_create_run", JsonSerializer.SerializeToElement(args), options.SessionId + "/scenario"), ct).ConfigureAwait(false);
            if (r.IsError)
            {
                throw GatewayException.Dependency($"sandbox_create_run failed: {ResultFields.Message(r)}");
            }

            runDir = ResultFields.FindString(r.Structured, ResultFields.RunDir)
                ?? throw GatewayException.Dependency("sandbox_create_run returned no run folder path.");
            sandboxRunId = ResultFields.FindString(r.Structured, ResultFields.RunId);
            runId = sandboxRunId ?? Path.GetFileName(runDir.TrimEnd('\\', '/'));
        }
        else
        {
            var storeRun = _runStore.CreateRun(scenario.Name, trigger: options.Trigger, purpose: "scenario_run", sessionId: options.SessionId, revitYear: scenario.RevitVersion, previousRunId: options.PreviousRunId, rerunOf: options.PreviousRunId);
            runDir = storeRun.FolderPath;
            runId = storeRun.Id;
            sandboxRunId = runId;
        }

        runDir = Path.GetFullPath(runDir);
        if (_policy.EnforceSandbox && !_policy.IsInSandbox(Path.Combine(runDir, "x"), _paths))
        {
            throw new GatewayException(GatewayErrorCodes.SandboxViolation, $"Run folder '{runDir}' is outside the gateway sandbox ({_runStore.RootDir}).");
        }

        Directory.CreateDirectory(Path.Combine(runDir, "data"));
        return (runDir, runId, sandboxRunId);
    }

    private static void SnapshotScenario(RunContext ctx)
    {
        try
        {
            var dst = Path.Combine(ctx.RunDir, "scenario");
            Directory.CreateDirectory(dst);
            File.Copy(ctx.Scenario.ScenarioPath, Path.Combine(dst, ScenarioDefinition.FileName), true);

            var ui = Path.Combine(ctx.Scenario.Directory, "ui");
            if (Directory.Exists(ui))
            {
                Directory.CreateDirectory(Path.Combine(dst, "ui"));
                foreach (var f in Directory.EnumerateFiles(ui, "*.json"))
                {
                    File.Copy(f, Path.Combine(dst, "ui", Path.GetFileName(f)), true);
                }
            }
        }
        catch (IOException ex)
        {
            ctx.Log($"warning: could not snapshot the scenario files ({ex.Message})");
        }
    }

    // ---- 2. Revit ---------------------------------------------------------------------------------

    private async Task AttachRevitAsync(RunContext ctx, CancellationToken ct)
    {
        var startup = TimeSpan.FromMinutes(ctx.Scenario.Timeouts.StartupMin);
        if (ctx.Options.Launch)
        {
            if (!_registry.TryGet("revit_launch", out var launchTool))
            {
                throw new ScenarioAbortException("ENV", "revit_launch is not available in this gateway (process module missing).");
            }

            // Arm the cleanup BEFORE the call: remember which Revit processes exist now, so that a launch that fails,
            // times out or is cancelled after Revit actually started can be reconciled (new gateway-launched pid → closed).
            ctx.PreLaunchPids = await ListRevitInstancesAsync(ctx).ConfigureAwait(false) is { } before
                ? before.Select(i => i.Pid).ToHashSet()
                : null;
            ctx.LaunchAttempted = true;
            var launchArgs = new JsonObject { ["year"] = ctx.Scenario.RevitVersion };
            if (ResultFields.HasArgument(launchTool, "escalate_dialogs"))
            {
                launchArgs["escalate_dialogs"] = true; // the run is unattended: forbidden dialogs of its own Revit go to the owner on Telegram
            }

            // The owner's Telegram answer time comes on top of the startup budget (the tool's own budget grows the same way).
            var ownerWait = launchTool.TimeoutFor(new ToolCall("revit_launch", JsonSerializer.SerializeToElement(launchArgs), ctx.Options.SessionId)) - launchTool.Timeout;
            var r = await CallAsync(ctx, "revit_launch", launchArgs, startup + TimeSpan.FromMinutes(1) + (ownerWait > TimeSpan.Zero ? ownerWait : TimeSpan.Zero), ct).ConfigureAwait(false);
            if (r.IsError)
            {
                throw new ScenarioAbortException("ENV", $"revit_launch failed: {ResultFields.Message(r)}");
            }

            var pid = ResultFields.FindInt(r.Structured, ResultFields.Pid)
                ?? throw new ScenarioAbortException("ENV", "revit_launch returned no pid.");
            ctx.Pid = pid;
            ctx.Launched = true;
            ctx.Vars.Set("pid", pid.ToString(CultureInfo.InvariantCulture));
            ctx.Log($"Revit {ctx.Scenario.RevitVersion} launched by this run: pid {pid}");

            if (_registry.TryGet("revit_wait_ready", out var wait))
            {
                var args = new JsonObject { ["pid"] = pid };
                if (ResultFields.HasArgument(wait, "timeout_sec"))
                {
                    args["timeout_sec"] = (int)startup.TotalSeconds;
                }

                var w = await CallAsync(ctx, "revit_wait_ready", args, startup + TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                if (w.IsError)
                {
                    throw new ScenarioAbortException("ENV", $"Revit pid {pid} not ready within {ctx.Scenario.Timeouts.StartupMin:0.#} min (startup dialog?): {ResultFields.Message(w)}");
                }
            }

            return;
        }

        if (ctx.Options.Pid is not { } existing)
        {
            return;
        }

        ctx.Pid = existing;
        ctx.Vars.Set("pid", existing.ToString(CultureInfo.InvariantCulture));
        if (_registry.TryGet("revit_list_instances", out _))
        {
            var r = await CallAsync(ctx, "revit_list_instances", new JsonObject(), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!r.IsError && !ResultFields.ContainsNumber(r.Structured, "pid", existing))
            {
                throw new ScenarioAbortException("ENV", $"Revit pid {existing} is not among the running instances (revit_list_instances).");
            }
        }

        ctx.Log($"using the running Revit pid {existing} (it will not be closed by this run)");
    }

    // ---- 3. model copy ----------------------------------------------------------------------------

    private async Task OpenModelAsync(RunContext ctx, CancellationToken ct)
    {
        string source;
        try
        {
            source = ctx.Vars.ResolveFile(ctx.Scenario.Model!);
        }
        catch (InvalidOperationException ex)
        {
            throw new ScenarioAbortException("INPUT", ex.Message);
        }

        if (!File.Exists(source))
        {
            throw new ScenarioAbortException("INPUT", $"Model not found: {source}");
        }

        ctx.ModelSource = source;
        ctx.ModelShaBefore = Sha256File(source);
        ctx.Log($"model {source} sha256 {ctx.ModelShaBefore}");

        string copy;
        if (_registry.TryGet("sandbox_copy_model", out _) && ctx.SandboxRunId is not null)
        {
            var r = await CallAsync(ctx, "sandbox_copy_model", new JsonObject { ["source_path"] = source, ["run_id"] = ctx.SandboxRunId }, TimeSpan.FromMinutes(ctx.Scenario.Timeouts.OpenModelMin), ct).ConfigureAwait(false);
            if (r.IsError)
            {
                throw new ScenarioAbortException("ENV", $"sandbox_copy_model failed: {ResultFields.Message(r)}");
            }

            copy = ResultFields.FindString(r.Structured, ResultFields.CopyPath)
                ?? throw new ScenarioAbortException("ENV", "sandbox_copy_model returned no copy_path.");
        }
        else
        {
            copy = Path.Combine(ctx.RunDir, Path.GetFileName(source));
            File.Copy(source, copy, overwrite: false);
        }

        copy = Path.GetFullPath(copy);
        if (!IsUnder(copy, ctx.RunDir))
        {
            throw new ScenarioAbortException("ENV", $"Model copy '{copy}' is not inside RUN_DIR — refusing to open it.");
        }

        ctx.ModelCopy = copy;
        ctx.Vars.Set("model", copy);

        if (!_registry.TryGet("doc_open", out _))
        {
            throw new ScenarioAbortException("ENV", "doc_open is not available (RevitAgent Bridge tools missing) — cannot open the model copy.");
        }

        if (!ctx.Launched && _registry.TryGet("doc_info", out _))
        {
            // The document the user had active, so the cleanup can switch back to it before closing the copy (doc_close activate_other).
            var before = await CallAsync(ctx, "doc_info", WithPid(ctx, "doc_info", new JsonObject()), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!before.IsError && ResultFields.FindString(before.Structured, ResultFields.DocumentPath) is { Length: > 0 } activePath && !IsUnder(activePath, ctx.RunDir))
            {
                ctx.PreviousActiveDocument = activePath;
            }
        }

        // Arm the cleanup BEFORE the call: a doc_open that times out or errors may still have opened the copy in Revit.
        ctx.DocOpened = true;
        var open = await CallAsync(ctx, "doc_open", WithPid(ctx, "doc_open", new JsonObject { ["path"] = copy }), TimeSpan.FromMinutes(ctx.Scenario.Timeouts.OpenModelMin), ct).ConfigureAwait(false);
        if (open.IsError)
        {
            throw new ScenarioAbortException("ENV", $"doc_open of the sandbox copy failed: {ResultFields.Message(open)}");
        }

        var check = await VerifyDocumentAsync(ctx, ct).ConfigureAwait(false);
        if (check.Problem is not null)
        {
            throw new ScenarioAbortException("ENV", "After doc_open: " + check.Problem);
        }

        ctx.Log($"opened the sandbox copy {check.Path}");
    }

    /// <summary>doc_info → the active document must be inside RUN_DIR (the run must never touch the original model).</summary>
    private async Task<(string? Path, string? Problem)> VerifyDocumentAsync(RunContext ctx, CancellationToken ct)
    {
        if (!_registry.TryGet("doc_info", out _))
        {
            return (null, "doc_info is not available — cannot confirm the open document is the sandbox copy.");
        }

        var r = await CallAsync(ctx, "doc_info", WithPid(ctx, "doc_info", new JsonObject()), TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (r.IsError)
        {
            return (null, $"doc_info failed ({ResultFields.Message(r)}) — a modal dialog may be open.");
        }

        var path = ResultFields.FindString(r.Structured, ResultFields.DocumentPath);
        if (path is null)
        {
            return (null, "doc_info returned no document path — cannot confirm the open document is the sandbox copy.");
        }

        return IsUnder(path, ctx.RunDir) ? (path, null) : (path, $"the active document '{path}' is not the sandbox copy inside RUN_DIR.");
    }

    // ---- 4. steps ---------------------------------------------------------------------------------

    private bool IsSafeToRetry(ScenarioStep step)
    {
        if (step.RetrySafe)
        {
            return true;
        }

        if (step.IsUi)
        {
            return false;
        }

        if (step.Tool is not null && _registry.TryGet(step.Tool, out var tool))
        {
            return tool.Permission == ToolPermission.Read;
        }

        return false;
    }

    private async Task CheckRevitLivenessAsync(RunContext ctx)
    {
        if (!ctx.Launched || ctx.Pid is not { } pid)
        {
            return;
        }

        var liveness = ProcessLivenessProbe?.Invoke(pid) ?? DefaultProcessLiveness(pid);
        if (liveness.HasExited)
        {
            var exitCode = liveness.ExitCode ?? -1;
            var title = liveness.MainWindowTitle ?? ctx.LastMainWindowTitle ?? "unknown";
            var journalPath = JournalPathFinder?.Invoke(ctx.Scenario.RevitVersion ?? 0) ?? FindNewestRevitJournal(ctx.Scenario.RevitVersion ?? 0);
            string journalCopiedMsg = "no journal found";
            if (journalPath is not null && File.Exists(journalPath))
            {
                try
                {
                    Directory.CreateDirectory(ctx.LogsDir);
                    var destJournal = Path.Combine(ctx.LogsDir, Path.GetFileName(journalPath));
                    File.Copy(journalPath, destJournal, overwrite: true);
                    journalCopiedMsg = $"journal copied to logs\\{Path.GetFileName(destJournal)}";
                    ctx.Log($"evidence: journal copied from {journalPath} to {destJournal}");
                }
                catch (Exception ex)
                {
                    journalCopiedMsg = $"journal copy failed: {ex.Message}";
                }
            }

            throw new ScenarioAbortException("revit_crashed", $"Revit crashed (exit code {exitCode}, last title '{title}', {journalCopiedMsg}).");
        }

        if (liveness.MainWindowTitle is not null)
        {
            ctx.LastMainWindowTitle = liveness.MainWindowTitle;
        }

        if (liveness.Responding)
        {
            ctx.UnresponsiveSince = null;
        }
        else
        {
            ctx.UnresponsiveSince ??= Now;
            var hangSec = (Now - ctx.UnresponsiveSince.Value).TotalSeconds;
            if (hangSec >= ctx.Scenario.HangTimeoutSec)
            {
                byte[]? shotBytes = null;
                try
                {
                    shotBytes = ScreenshotCapture?.Invoke(pid) ?? DefaultScreenshotCapture();
                }
                catch
                {
                }

                if (shotBytes is { Length: > 0 })
                {
                    try
                    {
                        Directory.CreateDirectory(ctx.EvidenceDir);
                        var shotName = $"revit_hung_{Now:yyyyMMdd_HHmmss}.png";
                        var shotPath = Path.Combine(ctx.EvidenceDir, shotName);
                        File.WriteAllBytes(shotPath, shotBytes);
                        ctx.Log($"evidence: screenshot saved to {shotPath}");
                    }
                    catch (Exception ex)
                    {
                        ctx.Log($"warning: failed to save hang screenshot: {ex.Message}");
                    }
                }

                throw new ScenarioAbortException("revit_hung", $"Revit main window (pid {pid}) not responding for >= {ctx.Scenario.HangTimeoutSec:0}s.");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task DelayWithLivenessAsync(RunContext ctx, TimeSpan delay, CancellationToken ct)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            var chunk = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
            await Task.Delay(chunk, ct).ConfigureAwait(false);
            remaining -= chunk;
            await CheckRevitLivenessAsync(ctx).ConfigureAwait(false);
        }
    }

    private static RevitProcessLiveness DefaultProcessLiveness(int pid)
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p.HasExited)
            {
                return new RevitProcessLiveness(true, p.ExitCode, false, null);
            }

            p.Refresh();
            return new RevitProcessLiveness(false, null, p.Responding, p.MainWindowTitle);
        }
        catch (ArgumentException)
        {
            return new RevitProcessLiveness(true, -1, false, null);
        }
        catch
        {
            return new RevitProcessLiveness(false, null, true, null);
        }
    }

    private static string? FindNewestRevitJournal(int year)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var baseDir = Path.Combine(localAppData, "Autodesk", "Revit");
            if (!Directory.Exists(baseDir))
            {
                return null;
            }

            var candidates = Directory.EnumerateFiles(baseDir, "journal.*.txt", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return candidates?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DefaultScreenshotCapture()
    {
        try
        {
            return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        }
        catch
        {
            return null;
        }
    }

    private async Task RunStepsAsync(RunContext ctx, CancellationToken ct)
    {
        foreach (var step in ctx.Scenario.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var rec = new StepRecord
            {
                Index = step.Index,
                Code = step.Code,
                Kind = step.IsUi ? "ui" : "tool",
                Target = step.IsUi ? step.Ui! : step.Tool!,
                Setup = step.IsSetup,
                Started = Now,
            };
            ctx.Records.Add(rec);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Log($"step {step.Index + 1} {step.Code} ({rec.Kind} {rec.Target})");

            int maxRetries = IsSafeToRetry(step) ? (step.Retry ?? ctx.Scenario.RetryDefault) : 0;
            double delaySec = step.RetryDelaySec ?? ctx.Scenario.RetryDelaySec;
            int attempt = 0;

            while (true)
            {
                attempt++;
                var attemptSw = System.Diagnostics.Stopwatch.StartNew();
                var attemptStarted = Now;
                ct.ThrowIfCancellationRequested();

                try
                {
                    await CheckRevitLivenessAsync(ctx).ConfigureAwait(false);
                    await CheckReadyAsync(ctx, ct).ConfigureAwait(false);

                    if (step.IsUi)
                    {
                        await RunUiStepAsync(ctx, step, rec, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RunToolStepAsync(ctx, step, rec, ct).ConfigureAwait(false);
                    }

                    await CheckRevitLivenessAsync(ctx).ConfigureAwait(false);
                }
                catch (ScenarioAbortException ex)
                {
                    rec.Status = "ERROR";
                    rec.ErrorCode ??= ex.Class;
                    rec.Message ??= ex.Message;
                    rec.Attempts.Add(new StepAttemptRecord
                    {
                        Attempt = attempt,
                        Status = rec.Status,
                        ErrorCode = rec.ErrorCode,
                        Error = rec.Message,
                        Started = attemptStarted,
                        DurationMs = attemptSw.ElapsedMilliseconds,
                    });
                    throw;
                }

                bool isCheckVerdictFail = rec.Status == "FAILED" && (rec.ErrorCode is null or "FAIL" or "CHECK_FAILED");
                bool isErrorOrTimeout = !isCheckVerdictFail && (rec.Status is "ERROR" or "FAILED" || rec.ErrorCode is not null);
                var attemptStatus = rec.ErrorCode == GatewayErrorCodes.Timeout
                    ? "TIMEOUT"
                    : (isCheckVerdictFail ? "FAILED" : (rec.Status == "SUCCEEDED" ? "SUCCEEDED" : "ERROR"));

                rec.Attempts.Add(new StepAttemptRecord
                {
                    Attempt = attempt,
                    Status = attemptStatus,
                    ErrorCode = rec.ErrorCode,
                    Error = rec.Message,
                    Started = attemptStarted,
                    DurationMs = attemptSw.ElapsedMilliseconds,
                });

                if (isErrorOrTimeout && attempt <= maxRetries)
                {
                    ctx.Log($"step {step.Index + 1} {step.Code} failed attempt {attempt} ({rec.Status}: {rec.Message}); retrying in {delaySec:0}s (attempt {attempt + 1}/{maxRetries + 1})...");
                    await DelayWithLivenessAsync(ctx, TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            rec.DurationMs = sw.ElapsedMilliseconds;
            ctx.Log($"step {step.Index + 1} {step.Code}: {rec.Status}{(rec.Message is null ? string.Empty : " — " + rec.Message)}{(rec.Attempts.Count > 1 ? $" ({rec.Attempts.Count} attempts)" : string.Empty)}");

            if (rec.Status is "FAILED" or "ERROR")
            {
                ctx.FirstToolError ??= $"{step.Code}: {rec.Message}";
                if (!step.ContinueOnError)
                {
                    ctx.StoppedOnError = true;
                    ctx.Log("on_error: stop → remaining steps skipped");
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task CheckReadyAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.Pid is not { } pid || !_registry.TryGet("revit_get_state", out _))
        {
            return;
        }

        var deadline = Now + BusyWaitLimit;
        while (true)
        {
            await CheckRevitLivenessAsync(ctx).ConfigureAwait(false);
            var r = await CallAsync(ctx, "revit_get_state", new JsonObject { ["pid"] = pid }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            await CheckRevitLivenessAsync(ctx).ConfigureAwait(false);
            var state = InterpretState(r);
            if (state.Blocked)
            {
                throw new ScenarioAbortException("ENV", $"Revit pid {pid} is not ready: {state.Reason}");
            }

            if (!state.Busy)
            {
                return;
            }

            if (Now >= deadline)
            {
                throw new ScenarioAbortException("ENV", $"Revit pid {pid} stayed busy for {BusyWaitLimit.TotalSeconds:0}s: {state.Reason}");
            }

            await DelayWithLivenessAsync(ctx, BusyProbeDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads revit_get_state tolerantly: blocked when the call fails with MODAL_BLOCKED / TARGET_NOT_READY / NOT_FOUND,
    /// or the result says blocked/modal/not responding/exited (bool flags, a state/status word, or non-empty
    /// blocking_dialogs / modal_dialogs); busy/starting → wait and re-probe.
    /// </summary>
    public static (bool Blocked, bool Busy, string Reason) InterpretState(ToolResult r)
    {
        if (r.IsError)
        {
            return r.ErrorCode is GatewayErrorCodes.ModalBlocked or GatewayErrorCodes.TargetNotReady or GatewayErrorCodes.NotFound
                ? (true, false, ResultFields.Message(r))
                : (false, false, "revit_get_state failed: " + ResultFields.Message(r));
        }

        if (r.Structured is not { ValueKind: JsonValueKind.Object } s)
        {
            return (false, false, "no state");
        }

        foreach (var flag in new[] { "blocked", "modal", "has_modal", "modal_blocked", "hung", "not_responding", "exited" })
        {
            if (s.TryGetProperty(flag, out var f) && f.ValueKind == JsonValueKind.True)
            {
                return (true, false, $"{flag} = true{DialogTitles(s)}");
            }
        }

        foreach (var flag in new[] { "responding", "alive", "running" })
        {
            if (s.TryGetProperty(flag, out var f) && f.ValueKind == JsonValueKind.False)
            {
                return (true, false, $"{flag} = false");
            }
        }

        foreach (var arr in new[] { "blocking_dialogs", "modal_dialogs" })
        {
            if (s.TryGetProperty(arr, out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0)
            {
                return (true, false, $"modal dialog open{DialogTitles(s)}");
            }
        }

        foreach (var key in new[] { "state", "status" })
        {
            if (s.TryGetProperty(key, out var st) && st.ValueKind == JsonValueKind.String)
            {
                var w = (st.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                if (BlockedStates.Contains(w))
                {
                    return (true, false, $"{key} = {w}{DialogTitles(s)}");
                }

                if (BusyStates.Contains(w))
                {
                    return (false, true, $"{key} = {w}");
                }
            }
        }

        return (false, false, "ready");
    }

    private static string DialogTitles(JsonElement s)
    {
        foreach (var arr in new[] { "blocking_dialogs", "modal_dialogs", "dialogs" })
        {
            if (s.TryGetProperty(arr, out var a) && a.ValueKind == JsonValueKind.Array)
            {
                var titles = a.EnumerateArray().Select(d => ResultFields.FindString(d, "title", "name", "caption")).Where(t => t is not null).Take(3).ToArray();
                if (titles.Length > 0)
                {
                    return " (dialog: " + string.Join(" | ", titles) + ")";
                }
            }
        }

        return string.Empty;
    }

    private async Task RunToolStepAsync(RunContext ctx, ScenarioStep step, StepRecord rec, CancellationToken ct)
    {
        var name = step.Tool!;
        if (!_registry.TryGet(name, out var tool))
        {
            throw new ScenarioAbortException("INPUT", $"Unknown tool '{name}'.");
        }

        if (tool.Permission == ToolPermission.Admin || ScenarioValidator.ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
        {
            throw new ScenarioAbortException("INPUT", $"'{name}' cannot be called from a scenario step.");
        }

        JsonObject args;
        try
        {
            args = WithPid(ctx, name, ctx.Vars.ExpandArgs(step.Args));
        }
        catch (InvalidOperationException ex)
        {
            throw new ScenarioAbortException("INPUT", ex.Message);
        }

        if (step.IsSetup)
        {
            if (!ctx.DocOpened)
            {
                throw new ScenarioAbortException("INPUT", "A 'purpose: setup' step needs the sandbox model copy open (scenario 'model').");
            }

            var check = await VerifyDocumentAsync(ctx, ct).ConfigureAwait(false);
            if (check.Problem is not null)
            {
                throw new ScenarioAbortException("ENV", "Setup step refused: " + check.Problem);
            }

            WriteJson(Path.Combine(ctx.DataDir, "setup-guard.json"), new JsonObject
            {
                ["pid"] = ctx.Pid,
                ["pathName"] = check.Path,
                ["tool"] = name,
                ["why"] = step.Why,
                ["at"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            });
            ctx.Log($"setup step {name}: document {check.Path}, why: {step.Why}");
        }

        var budget = TimeSpan.FromSeconds(step.TimeoutSec ?? ctx.Scenario.Timeouts.StepMin * 60);
        var r = await CallAsync(ctx, name, args, budget, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        rec.DataFile = SaveResult(ctx, step.Code, r);
        if (r.IsError)
        {
            rec.Status = "FAILED";
            rec.ErrorCode = r.ErrorCode;
            rec.Message = ResultFields.Message(r);
            if (r.ErrorCode == GatewayErrorCodes.ModalBlocked)
            {
                throw new ScenarioAbortException("ENV", $"{name}: Revit is blocked by a modal dialog ({rec.Message}).");
            }
        }
        else
        {
            rec.Status = "SUCCEEDED";
            rec.ErrorCode = null;
            rec.Message = null;
        }
    }

    private async Task RunUiStepAsync(RunContext ctx, ScenarioStep step, StepRecord rec, CancellationToken ct)
    {
        if (!_registry.TryGet("ui_run_script", out _))
        {
            throw new ScenarioAbortException("ENV", "ui_run_script is not available (UI module missing) — cannot run ui steps.");
        }

        if (ctx.Pid is not { } pid)
        {
            throw new ScenarioAbortException("INPUT", "A ui step needs a Revit process (pid or launch: true).");
        }

        string script;
        try
        {
            script = ctx.Vars.ResolveFile(step.Ui!);
        }
        catch (InvalidOperationException ex)
        {
            throw new ScenarioAbortException("INPUT", ex.Message);
        }

        if (!File.Exists(script))
        {
            throw new ScenarioAbortException("INPUT", $"UI script not found: {script}");
        }

        var outDir = Path.Combine(ctx.DataDir, "ui", $"{step.Index + 1:D2}-{SafeName(step.Code)}");
        Directory.CreateDirectory(outDir);
        var before = await ListWindowsAsync(ctx, ct).ConfigureAwait(false);

        var budget = TimeSpan.FromSeconds(step.TimeoutSec ?? 600);
        var r = await CallAsync(ctx, "ui_run_script", new JsonObject { ["script_path"] = script, ["pid"] = pid, ["out_dir"] = outDir }, budget, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var (status, cls) = InterpretUi(r);
        rec.Status = status;
        if (r.IsError)
        {
            rec.ErrorCode = r.ErrorCode;
            rec.Message = ResultFields.Message(r);
        }

        if (status == "BLOCKED")
        {
            // A BLOCKED ui step (could not reach the part under test) writes no data\<name>.json → checks reading it are NOT_MEASURABLE.
            rec.DataFile = SaveResult(ctx, step.Code + ".blocked", r);
            rec.Message ??= "UI step BLOCKED (navigation/layout) — not an add-in failure; fix the script's selectors.";
            ctx.Log("ui step BLOCKED: continuing with the next step");
        }
        else
        {
            rec.DataFile = SaveResult(ctx, step.Code, r);
            if (status == "FAILED" && rec.Message is null)
            {
                rec.Message = "UI script reported failed steps — see " + Relative(ctx, outDir);
            }
        }

        if (cls is not null)
        {
            throw new ScenarioAbortException(cls, $"ui_run_script could not run ({rec.Message}).");
        }

        if (status == "SUCCEEDED" && !step.LeaveOpen && before is not null)
        {
            var after = await ListWindowsAsync(ctx, ct).ConfigureAwait(false);
            var leftover = after?.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToArray() ?? Array.Empty<string>();
            if (leftover.Length > 0)
            {
                rec.Status = "FAILED";
                rec.Message = $"UI step left {leftover.Length} new window(s) open ({string.Join(" | ", leftover.Take(3))}) — a modal dialog freezes every later call; close it in the script or set leave_open: true.";
            }
        }

        if (ctx.DocOpened)
        {
            var check = await VerifyDocumentAsync(ctx, ct).ConfigureAwait(false);
            if (check.Problem is not null)
            {
                throw new ScenarioAbortException("ENV", "After ui step: " + check.Problem);
            }
        }
    }

    /// <summary>ui_run_script outcome → SUCCEEDED / FAILED / BLOCKED, plus ENV/INPUT when the script could not run at all.</summary>
    public static (string Status, string? AbortClass) InterpretUi(ToolResult r)
    {
        if (r.IsError)
        {
            return r.ErrorCode switch
            {
                GatewayErrorCodes.DependencyFailed or GatewayErrorCodes.TargetNotReady or GatewayErrorCodes.ModalBlocked => ("ERROR", "ENV"),
                GatewayErrorCodes.ValidationFailed => ("ERROR", "INPUT"),
                _ => ("ERROR", null),
            };
        }

        if (r.Structured is not { ValueKind: JsonValueKind.Object } s)
        {
            return ("SUCCEEDED", null);
        }

        foreach (var key in new[] { "status", "verdict", "result" })
        {
            if (s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var w = (v.GetString() ?? string.Empty).ToUpperInvariant();
                if (w.Contains("BLOCK", StringComparison.Ordinal))
                {
                    return ("BLOCKED", null);
                }

                if (w.Contains("FAIL", StringComparison.Ordinal) || w.Contains("ERROR", StringComparison.Ordinal))
                {
                    return ("FAILED", null);
                }

                if (w.Contains("PASS", StringComparison.Ordinal) || w.Contains("SUCCE", StringComparison.Ordinal) || w == "OK")
                {
                    return ("SUCCEEDED", null);
                }
            }
        }

        if (s.TryGetProperty("blocked", out var b) && ((b.ValueKind == JsonValueKind.Number && b.GetDouble() > 0) || b.ValueKind == JsonValueKind.True))
        {
            return ("BLOCKED", null);
        }

        if ((s.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Number && f.GetDouble() > 0) ||
            (s.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False))
        {
            return ("FAILED", null);
        }

        return ("SUCCEEDED", null);
    }

    /// <summary>Titled windows of the Revit process by handle (null when ui_list_windows is missing or says nothing usable).</summary>
    private async Task<Dictionary<string, string>?> ListWindowsAsync(RunContext ctx, CancellationToken ct)
    {
        if (!_registry.TryGet("ui_list_windows", out _))
        {
            return null;
        }

        var r = await CallAsync(ctx, "ui_list_windows", WithPid(ctx, "ui_list_windows", new JsonObject()), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (r.IsError || r.Structured is not { } s)
        {
            return null;
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<JsonElement>();
        stack.Push(s);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray())
                {
                    stack.Push(x);
                }
            }
            else if (e.ValueKind == JsonValueKind.Object)
            {
                var id = ResultFields.FindString(e, "hwnd", "handle", "window_handle", "native_window_handle");
                var title = ResultFields.FindString(e, "title", "name");
                if (id is not null && !string.IsNullOrWhiteSpace(title))
                {
                    found[id] = title!;
                }

                foreach (var p in e.EnumerateObject())
                {
                    stack.Push(p.Value);
                }
            }
        }

        return found.Count > 0 ? found : null;
    }

    // ---- 5. cleanup (always) ---------------------------------------------------------------------

    private async Task CleanupAsync(RunContext ctx)
    {
        var c = ctx.Cleanup;
        try
        {
            if (ctx.DocOpened && !ctx.Options.KeepOpen && ctx.Pid is not null)
            {
                if (_registry.TryGet("doc_close", out var close))
                {
                    // Only ever the sandbox copy, identified explicitly: the default (active document) could be the user's.
                    var args = WithPid(ctx, "doc_close", new JsonObject());
                    var selector = new[] { "path", "document", "title" }.FirstOrDefault(n => ResultFields.HasArgument(close, n));
                    if (ctx.ModelCopy is null || selector is null)
                    {
                        c["doc_closed"] = false;
                        c["doc_close_error"] = "doc_close was not called: the copy cannot be identified explicitly (no path/document/title argument) - never closes the active document.";
                        ctx.CleanupWarning ??= $"the sandbox copy {ctx.ModelCopy} may still be open in Revit - close it manually.";
                    }
                    else
                    {
                        args[selector] = selector == "title" ? Path.GetFileNameWithoutExtension(ctx.ModelCopy) : ctx.ModelCopy;
                        if (ResultFields.HasArgument(close, "save"))
                        {
                            args["save"] = false;
                        }

                        foreach (var discard in new[] { "discard", "discard_changes" })
                        {
                            if (ResultFields.HasArgument(close, discard))
                            {
                                args[discard] = true;
                            }
                        }

                        if (ResultFields.HasArgument(close, "activate_other"))
                        {
                            // Revit cannot close the active document: switch to an already-open one (the user's, when known) first.
                            args["activate_other"] = true;
                            if (ctx.PreviousActiveDocument is not null && ResultFields.HasArgument(close, "activate_document"))
                            {
                                args["activate_document"] = ctx.PreviousActiveDocument;
                            }
                        }

                        var r = await SafeCallAsync(ctx, "doc_close", args, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                        c["doc_closed"] = r is { IsError: false };
                        if (r is { IsError: false } && ResultFields.FindString(r.Structured, "activated.path") is { } reactivated)
                        {
                            c["doc_close_note"] = $"the copy was the active document: switched to the already-open {reactivated} first";
                        }

                        if (r is { IsError: true })
                        {
                            var msg = ResultFields.Message(r);
                            if (r.ErrorCode == GatewayErrorCodes.NotFound || msg.Contains("not open", StringComparison.OrdinalIgnoreCase))
                            {
                                c["doc_close_note"] = "the sandbox copy was not open (nothing to close)";
                            }
                            else
                            {
                                c["doc_close_error"] = msg;
                                if (r.ErrorCode == GatewayErrorCodes.Conflict)
                                {
                                    ctx.CleanupWarning ??= ctx.Launched
                                        ? $"WARNING: the sandbox copy {ctx.ModelCopy} is the only/active document and could not be closed (CONFLICT); it is closed together with Revit (revit_close, discard_changes)."
                                        : $"WARNING: the sandbox copy {ctx.ModelCopy} could not be closed in Revit pid {ctx.Pid} (not launched by this run): it is the active document and no other open document could be made active (CONFLICT). It is still open - close it manually. No other document was closed.";
                                    c["WARNING"] = ctx.CleanupWarning;
                                }
                                else
                                {
                                    ctx.CleanupWarning ??= $"WARNING: closing the sandbox copy failed ({msg}); it may still be open in Revit.";
                                }
                            }
                        }
                    }
                }
                else
                {
                    c["doc_closed"] = false;
                    c["doc_close_error"] = "doc_close not available";
                }
            }

            if (ctx.Launched && ctx.Pid is { } pid)
            {
                if (ctx.Options.KeepOpen)
                {
                    c["revit_closed"] = false;
                    c["revit_note"] = $"keep_open: Revit pid {pid} launched by this run is still running — close it with revit_close when done.";
                }
                else
                {
                    var liveness = ProcessLivenessProbe?.Invoke(pid) ?? DefaultProcessLiveness(pid);
                    if (liveness.HasExited)
                    {
                        c["revit_closed"] = true;
                        c["revit_note"] = "Revit had already exited.";
                    }
                    else if (_registry.TryGet("revit_close", out _))
                    {
                        var closeArgs = new JsonObject { ["pid"] = pid };
                        if (_registry.TryGet("revit_close", out var rc) && ResultFields.HasArgument(rc, "discard_changes"))
                        {
                            closeArgs["discard_changes"] = true; // the sandbox copy may hold unsaved changes: never wait on a save prompt
                        }

                        var r = await SafeCallAsync(ctx, "revit_close", closeArgs, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
                        c["revit_closed"] = r is { IsError: false };
                        if (r is { IsError: true })
                        {
                            c["revit_close_error"] = ResultFields.Message(r);
                            try
                            {
                                var p = System.Diagnostics.Process.GetProcessById(pid);
                                p.Kill();
                                c["revit_killed"] = true;
                                c["revit_closed"] = true;
                            }
                            catch
                            {
                            }
                        }
                    }
                    else
                    {
                        c["revit_closed"] = false;
                        c["revit_close_error"] = "revit_close not available";
                    }
                }
            }
            else if (ctx.Pid is not null)
            {
                c["revit_closed"] = false;
                c["revit_note"] = "Revit was not launched by this run — left running.";
            }
            else if (ctx.LaunchAttempted && ctx.PreLaunchPids is not null)
            {
                await ReconcileFailedLaunchAsync(ctx).ConfigureAwait(false);
            }

            if (ctx.ModelCopy is not null)
            {
                // Deliverable 3: model copies are always kept in the run data store.
                c["model_copy_deleted"] = false;
            }

            if (ctx.ModelSource is not null && ctx.ModelShaBefore is not null && File.Exists(ctx.ModelSource))
            {
                var after = Sha256File(ctx.ModelSource);
                ctx.ModelChanged = !string.Equals(after, ctx.ModelShaBefore, StringComparison.Ordinal);
                c["original_model_unchanged"] = !ctx.ModelChanged;
            }
        }
        catch (Exception ex)
        {
            c["cleanup_error"] = $"{ex.GetType().Name}: {ResultFields.Clip(ex.Message, 200)}";
        }

        ctx.Log("cleanup: " + string.Join(", ", c.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    /// <summary>Running Revit instances (pid + launched_by_gateway) via revit_list_instances; null when unavailable.</summary>
    private async Task<List<(int Pid, bool Gateway)>?> ListRevitInstancesAsync(RunContext ctx)
    {
        if (!_registry.TryGet("revit_list_instances", out _))
        {
            return null;
        }

        var r = await SafeCallAsync(ctx, "revit_list_instances", new JsonObject(), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (r is not { IsError: false, Structured: { } root } || !ResultFields.TryPath(root, "instances", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<(int, bool)>();
        foreach (var i in arr.EnumerateArray())
        {
            if (i.ValueKind == JsonValueKind.Object && i.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid))
            {
                list.Add((pid, i.TryGetProperty("launched_by_gateway", out var g) && g.ValueKind == JsonValueKind.True));
            }
        }

        return list;
    }

    /// <summary>revit_launch failed, timed out or was cancelled: close any NEW gateway-launched Revit that appeared meanwhile.</summary>
    private async Task ReconcileFailedLaunchAsync(RunContext ctx)
    {
        var c = ctx.Cleanup;
        var now = await ListRevitInstancesAsync(ctx).ConfigureAwait(false);
        var strays = now?.Where(i => i.Gateway && !ctx.PreLaunchPids!.Contains(i.Pid)).Select(i => i.Pid).ToArray() ?? Array.Empty<int>();
        if (strays.Length == 0)
        {
            c["launch_reconcile"] = "no new gateway-launched Revit found after the failed launch";
            return;
        }

        if (ctx.Options.KeepOpen)
        {
            c["launch_reconcile"] = $"keep_open: Revit pid(s) {string.Join(",", strays)} started by the failed launch are still running - close them with revit_close.";
            return;
        }

        var closed = new List<int>();
        foreach (var stray in strays)
        {
            var args = new JsonObject { ["pid"] = stray };
            if (_registry.TryGet("revit_close", out var rc) && ResultFields.HasArgument(rc, "discard_changes"))
            {
                args["discard_changes"] = true;
            }

            var r = _registry.TryGet("revit_close", out _) ? await SafeCallAsync(ctx, "revit_close", args, TimeSpan.FromMinutes(3)).ConfigureAwait(false) : null;
            if (r is { IsError: false })
            {
                closed.Add(stray);
            }
            else
            {
                ctx.CleanupWarning ??= $"WARNING: Revit pid {stray} started by a failed revit_launch could not be closed - close it manually.";
            }
        }

        c["launch_reconcile"] = $"closed Revit pid(s) {string.Join(",", closed)} left by the failed launch";
    }

    private async Task<ToolResult?> SafeCallAsync(RunContext ctx, string tool, JsonObject args, TimeSpan budget)
    {
        try
        {
            return await CallAsync(ctx, tool, args, budget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Log($"cleanup {tool} failed: {ex.GetType().Name}");
            return null;
        }
    }

    // ---- 6. grading + reports ---------------------------------------------------------------------

    private ScenarioRunSummary Finish(RunContext ctx)
    {
        var ended = DateTimeOffset.Now;
        IReadOnlyList<CheckEvaluation> evaluations = Array.Empty<CheckEvaluation>();
        var verdict = Verdicts.Error;
        string? reportError = null;
        try
        {
            WriteStepStatus(ctx);
            var gradingStarted = DateTimeOffset.Now;
            evaluations = new CheckEvaluator(ctx.RunDir).EvaluateAll(ctx.Scenario.Checks);
            ctx.GradingWindow = (gradingStarted, DateTimeOffset.Now);
            verdict = ComputeVerdict(ctx, evaluations);
            ScenarioReports.WriteAll(ctx.ToReportModel(evaluations, verdict, ended));
        }
        catch (Exception ex)
        {
            reportError = $"Writing the reports failed ({ex.GetType().Name}: {ResultFields.Clip(ex.Message, 200)}).";
            ctx.Log(reportError);
            verdict = Verdicts.Error;
            try
            {
                WriteJson(Path.Combine(ctx.RunDir, "result.json"), new JsonArray(new JsonObject { ["sheet"] = "*", ["verdict"] = verdict, ["error"] = reportError, ["checks"] = new JsonArray() }));
            }
            catch (IOException)
            {
            }
        }

        ctx.Verdict = verdict;
        ctx.Ended = ended;
        WriteRunInfo(ctx, final: true);

        try
        {
            var countsObj = new
            {
                steps = new
                {
                    total = ctx.Records.Count,
                    succeeded = ctx.Records.Count(r => r.Status == "SUCCEEDED"),
                    failed = ctx.Records.Count(r => r.Status == "FAILED" || r.Status == "ERROR"),
                    blocked = ctx.Records.Count(r => r.Status == "BLOCKED"),
                    skipped = ctx.Records.Count(r => r.Status == "SKIPPED"),
                },
                checks = new
                {
                    total = evaluations.Count,
                    pass = evaluations.Count(r => r.Verdict == Verdicts.Pass),
                    fail = evaluations.Count(r => r.Verdict == Verdicts.Fail),
                    not_measurable = evaluations.Count(r => r.Verdict == Verdicts.NotMeasurable),
                },
            };

            var finishInfo = new RunFinishInfo
            {
                EndedAt = ended,
                Verdict = verdict,
                Pid = ctx.Pid,
                RevitYear = ctx.Scenario.RevitVersion,
                ModelSource = ctx.ModelSource,
                ModelCopy = ctx.ModelCopy,
                ModelShaBefore = ctx.ModelShaBefore,
                ModelShaAfter = ctx.ModelSource is { Length: > 0 } && File.Exists(ctx.ModelSource) ? Sha256File(ctx.ModelSource) : ctx.ModelShaBefore,
                ModelUnchanged = !ctx.ModelChanged,
                SessionId = ctx.Options.SessionId,
                PreviousRunId = ctx.Options.PreviousRunId,
                RerunOf = ctx.Options.PreviousRunId,
                Counts = countsObj,
            };

            _runStore.FinishRun(ctx.RunDir, finishInfo);
        }
        catch (Exception ex)
        {
            ctx.Log($"warning: RunStore.FinishRun failed: {ex.Message}");
        }

        ctx.Log($"END: {verdict}");
        return new ScenarioRunSummary
        {
            ScenarioName = ctx.Scenario.Name,
            RunId = ctx.RunId,
            RunDir = ctx.RunDir,
            Verdict = verdict,
            Error = ctx.ModelChanged ? "ERROR: the original model file changed during the run!" : ctx.EnvError ?? reportError ?? ctx.CleanupWarning,
            Steps = ctx.Records,
            Checks = evaluations,
            Pid = ctx.Pid,
            Launched = ctx.Launched,
            Cleanup = ctx.Cleanup,
            PreviousRunId = ctx.Options.PreviousRunId,
            RerunOf = ctx.Options.PreviousRunId,
        };
    }

    /// <summary>
    /// Run verdict: ERROR (stopped early / environment / original model changed) → FAIL (a severity-fail check FAILs) →
    /// SUSPECT (anything not measured, a warn check FAILs, manual steps, no checks) → PASS.
    /// </summary>
    public static string ComputeVerdict(bool aborted, bool stoppedOnError, bool modelChanged, int manualSteps, IReadOnlyList<CheckEvaluation> checks)
    {
        if (aborted || stoppedOnError || modelChanged)
        {
            return Verdicts.Error;
        }

        if (checks.Any(c => c.Verdict == Verdicts.Fail && !string.Equals(c.Severity, CheckSeverity.Warn, StringComparison.Ordinal)))
        {
            return Verdicts.Fail;
        }

        if (checks.Count == 0 || checks.Any(c => c.Verdict != Verdicts.Pass) || manualSteps > 0)
        {
            return Verdicts.Suspect;
        }

        return Verdicts.Pass;
    }

    private static string ComputeVerdict(RunContext ctx, IReadOnlyList<CheckEvaluation> checks) =>
        ComputeVerdict(ctx.EnvError is not null, ctx.StoppedOnError, ctx.ModelChanged, ctx.Scenario.ManualSteps.Count, checks);

    private static void WriteStepStatus(RunContext ctx)
    {
        var ran = ctx.Records.Where(r => r.Status != "SKIPPED").ToArray();
        var steps = new JsonArray();
        foreach (var r in ran)
        {
            var o = new JsonObject { ["stepCode"] = r.Code, ["status"] = r.Status == "ERROR" ? "FAILED" : r.Status };
            if (r.Kind == "ui")
            {
                o["kind"] = "ui";
            }

            if (r.Attempts.Count > 0)
            {
                var attemptsArray = new JsonArray();
                foreach (var a in r.Attempts)
                {
                    attemptsArray.Add(new JsonObject
                    {
                        ["attempt"] = a.Attempt,
                        ["status"] = a.Status,
                        ["error"] = a.Error,
                        ["error_code"] = a.ErrorCode,
                        ["duration_ms"] = a.DurationMs,
                    });
                }

                o["attempts"] = attemptsArray;
            }

            steps.Add(o);
        }

        WriteJson(Path.Combine(ctx.DataDir, "step_status.json"), new JsonObject
        {
            ["externalRunId"] = string.Empty,
            ["source"] = "steps",
            ["ok"] = ran.Length > 0,
            ["message"] = ran.Length == 0 ? (ctx.EnvError ?? "no step ran") : string.Empty,
            ["steps"] = steps,
        });
    }

    private void WriteRunInfo(RunContext ctx, bool final)
    {
        var info = new JsonObject
        {
            ["runner"] = RunnerName,
            ["scenario"] = ctx.Scenario.Name,
            ["name"] = ctx.Scenario.Name,
            ["run"] = ctx.RunId,
            ["run_dir"] = ctx.RunDir,
            ["scenario_dir"] = ctx.Scenario.Directory,
            ["scenario_sha256"] = Sha256File(ctx.Scenario.ScenarioPath),
            ["purpose"] = "adhoc",
            ["session"] = ctx.Options.SessionId,
            ["machine"] = Environment.MachineName,
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["host"] = "Revit",
            ["host_version"] = ctx.Scenario.RevitVersion?.ToString(CultureInfo.InvariantCulture),
            ["gateway"] = new JsonObject { ["name"] = Mcp.McpGatewayServer.ServerName, ["version"] = Mcp.McpGatewayServer.Version },
            ["revit"] = new JsonObject { ["year"] = ctx.Scenario.RevitVersion, ["pid"] = ctx.Pid, ["launched_by_run"] = ctx.Launched },
            ["model"] = ctx.ModelSource,
            ["model_copy"] = ctx.ModelCopy,
            ["model_sha256"] = ctx.ModelShaBefore,
            ["dataset"] = ctx.Options.DatasetDir,
            ["started"] = ctx.Started.ToString("O", CultureInfo.InvariantCulture),
        };
        if (ctx.Options.PreviousRunId is not null)
        {
            info["previous_run_id"] = ctx.Options.PreviousRunId;
            info["rerun_of"] = ctx.Options.PreviousRunId;
        }

        if (final)
        {
            info["ended"] = ctx.Ended?.ToString("O", CultureInfo.InvariantCulture);
            info["duration_s"] = ctx.Ended is { } e ? Math.Round((e - ctx.Started).TotalSeconds, 1) : null;
            info["verdict"] = ctx.Verdict;
            info["error"] = ctx.EnvError;
            info["error_class"] = ctx.EnvErrorClass;
            info["grading"] = ctx.GradingWindow is { } g
                ? new JsonObject { ["started"] = g.Started.ToString("O", CultureInfo.InvariantCulture), ["ended"] = g.Ended.ToString("O", CultureInfo.InvariantCulture), ["evaluator"] = "revitagent-checks" }
                : null;
            info["cleanup"] = JsonSerializer.SerializeToNode(ctx.Cleanup);
        }

        try
        {
            WriteJson(Path.Combine(ctx.RunDir, "run-info.json"), info);
            var runJsonPath = Path.Combine(ctx.RunDir, "run.json");
            JsonObject node;
            if (File.Exists(runJsonPath))
            {
                node = (JsonNode.Parse(File.ReadAllText(runJsonPath)) as JsonObject) ?? new JsonObject();
            }
            else
            {
                node = new JsonObject
                {
                    ["id"] = ctx.RunId,
                    ["suite"] = ctx.Scenario.Name, // run.json key read by the run store (its schema still says "suite")
                    ["trigger"] = "manual",
                    ["machine"] = Environment.MachineName,
                    ["user"] = Environment.UserName,
                    ["revit"] = new JsonObject { ["year"] = ctx.Scenario.RevitVersion, ["pid"] = ctx.Pid },
                    ["gateway_version"] = Mcp.McpGatewayServer.Version,
                    ["started_at"] = ctx.Started.ToString("O", CultureInfo.InvariantCulture),
                    ["ended_at"] = null,
                    ["verdict"] = null,
                    ["counts"] = null,
                };
            }

            if (ctx.Options.PreviousRunId is not null)
            {
                node["previous_run_id"] = ctx.Options.PreviousRunId;
                node["rerun_of"] = ctx.Options.PreviousRunId;
            }

            WriteJson(runJsonPath, node);
        }
        catch (IOException ex)
        {
            ctx.Log($"warning: run-info.json not written ({ex.Message})");
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<ToolResult> CallAsync(RunContext ctx, string tool, JsonObject args, TimeSpan budget, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        ctx.Log($"  → {tool} {ResultFields.Clip(args.ToJsonString(), 300)}");

        var work = _registry.InvokeAsync(new ToolCall(tool, JsonSerializer.SerializeToElement(args), ctx.InnerSession), cts.Token);
        if (ctx.Launched && ctx.Pid is not null)
        {
            while (!work.IsCompleted)
            {
                var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5), cts.Token)).ConfigureAwait(false);
                if (finished == work)
                {
                    break;
                }

                await CheckRevitLivenessAsync(ctx).ConfigureAwait(false);
            }
        }

        var r = await work.ConfigureAwait(false);
        ctx.Log($"  ← {tool} {(r.IsError ? "ERROR " + ResultFields.Message(r) : "ok")}");
        return r;
    }

    /// <summary>Adds <c>pid</c> when the tool takes one and the step did not set it.</summary>
    private JsonObject WithPid(RunContext ctx, string tool, JsonObject args)
    {
        if (ctx.Pid is { } pid && !args.ContainsKey("pid") && _registry.TryGet(tool, out var t) && ResultFields.HasArgument(t, "pid"))
        {
            args["pid"] = pid;
        }

        return args;
    }

    private static string SaveResult(RunContext ctx, string code, ToolResult r)
    {
        var safe = SafeName(code);
        var name = r.IsError && !code.EndsWith(".blocked", StringComparison.Ordinal) ? safe + ".error.json" : safe + ".json";
        var path = Path.Combine(ctx.DataDir, name);
        JsonNode? payload = r.Structured is { } s
            ? JsonNode.Parse(s.GetRawText())
            : new JsonObject { ["text"] = string.Join("\n", r.Content.OfType<TextContent>().Select(t => t.Text)) };
        WriteJson(path, payload);

        var k = 0;
        foreach (var img in r.Content.OfType<ImageContent>())
        {
            k++;
            var ext = img.MimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            File.WriteAllBytes(Path.Combine(ctx.DataDir, $"{safe}-{k}{ext}"), img.Data);
        }

        return Relative(ctx, path);
    }

    internal static void WriteJson(string path, JsonNode? node) =>
        File.WriteAllText(path, node?.ToJsonString(FileJson) ?? "null", new UTF8Encoding(false));

    private static string Relative(RunContext ctx, string full) =>
        full.StartsWith(ctx.RunDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full[(ctx.RunDir.Length + 1)..] : full;

    public static string SafeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var r = sb.ToString().Trim('.', '-');
        return r.Length == 0 ? "step" : ResultFields.Clip(r, 80).TrimEnd('…');
    }

    public static bool IsUnder(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    // ---- state ------------------------------------------------------------------------------------

    private sealed class ScenarioAbortException : Exception
    {
        public ScenarioAbortException(string cls, string message)
            : base(message) => Class = cls;

        public string Class { get; }
    }

    private sealed class RunContext
    {
        private readonly object _logGate = new();

        public RunContext(ScenarioDefinition scenario, ScenarioRunOptions options, string runDir, string runId, string? sandboxRunId, DateTimeOffset started)
        {
            Scenario = scenario;
            Options = options;
            RunDir = runDir.TrimEnd('\\', '/');
            RunId = runId;
            SandboxRunId = sandboxRunId;
            Started = started;
            DataDir = Path.Combine(RunDir, "data");
            LogsDir = Path.Combine(RunDir, "logs");
            EvidenceDir = Path.Combine(RunDir, "evidence");
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(EvidenceDir);
            InnerSession = $"{options.SessionId}/scenario-{runId}";
            Vars = new ScenarioVariables(scenario.Directory).Set("run_dir", RunDir).Set("dataset", options.DatasetDir);
        }

        public ScenarioDefinition Scenario { get; }

        public ScenarioRunOptions Options { get; }

        public string RunDir { get; }

        public string RunId { get; }

        public string? SandboxRunId { get; }

        public string DataDir { get; }

        public string LogsDir { get; }

        public string EvidenceDir { get; }

        public DateTimeOffset? UnresponsiveSince { get; set; }

        public string? LastMainWindowTitle { get; set; }

        public string InnerSession { get; }

        public ScenarioVariables Vars { get; }

        public DateTimeOffset Started { get; }

        public DateTimeOffset? Ended { get; set; }

        public (DateTimeOffset Started, DateTimeOffset Ended)? GradingWindow { get; set; }

        public int? Pid { get; set; }

        public bool Launched { get; set; }

        public bool DocOpened { get; set; }

        /// <summary>revit_launch was started (it may have started Revit even when it then failed or was cancelled).</summary>
        public bool LaunchAttempted { get; set; }

        /// <summary>Revit pids that existed before the launch (null when revit_list_instances was unavailable).</summary>
        public HashSet<int>? PreLaunchPids { get; set; }

        /// <summary>Something the cleanup could not do (reported in the summary and report.md).</summary>
        public string? CleanupWarning { get; set; }

        public string? ModelSource { get; set; }

        public string? ModelCopy { get; set; }

        /// <summary>Path of the document that was active in an attached Revit before the copy was opened (null: none, or unknown).</summary>
        public string? PreviousActiveDocument { get; set; }

        public string? ModelShaBefore { get; set; }

        public bool ModelChanged { get; set; }

        public string? EnvError { get; private set; }

        public string? EnvErrorClass { get; private set; }

        public string? FirstToolError { get; set; }

        public bool StoppedOnError { get; set; }

        public string? Verdict { get; set; }

        public List<StepRecord> Records { get; } = new();

        public Dictionary<string, object?> Cleanup { get; } = new(StringComparer.Ordinal);

        public void Abort(string cls, string message)
        {
            EnvErrorClass ??= cls;
            EnvError ??= $"{cls}: {message}";
            Log("STOP " + EnvError);
        }

        public void SkipRemaining()
        {
            foreach (var step in Scenario.Steps.Where(s => Records.All(r => r.Index != s.Index)))
            {
                Records.Add(new StepRecord { Index = step.Index, Code = step.Code, Kind = step.IsUi ? "ui" : "tool", Target = step.IsUi ? step.Ui! : step.Tool!, Setup = step.IsSetup });
            }

            Records.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        public void Log(string line)
        {
            lock (_logGate)
            {
                try
                {
                    File.AppendAllText(Path.Combine(RunDir, "run.log"), $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}", new UTF8Encoding(false));
                }
                catch (IOException)
                {
                }
            }
        }

        public ScenarioReportModel ToReportModel(IReadOnlyList<CheckEvaluation> checks, string verdict, DateTimeOffset ended) => new()
        {
            Scenario = Scenario,
            RunDir = RunDir,
            RunId = RunId,
            Verdict = verdict,
            Started = Started,
            Ended = ended,
            Pid = Pid,
            Launched = Launched,
            ModelSource = ModelSource,
            ModelCopy = ModelCopy,
            ModelChanged = ModelChanged,
            EnvError = EnvError,
            FirstToolError = FirstToolError,
            Steps = Records,
            Checks = checks,
            Cleanup = Cleanup,
        };
    }
}
