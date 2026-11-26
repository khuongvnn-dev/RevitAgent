using System.Text.Json.Nodes;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// A scenario = a folder with <c>scenario.yaml</c> (metadata, steps, checks) and optionally <c>ui\*.json</c> UI scripts,
/// <c>input\</c> and <c>baseline\</c>. See docs/agent-guide/scenarios.md.
/// </summary>
public sealed class ScenarioDefinition
{
    public const string FileName = "scenario.yaml";

    public required string Directory { get; init; }

    /// <summary>Full path of scenario.yaml.</summary>
    public required string ScenarioPath { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public int? RevitVersion { get; init; }

    /// <summary>Original model (relative to the scenario folder, absolute, or with <c>{dataset}</c>). Never opened directly.</summary>
    public string? Model { get; init; }

    /// <summary>Informational: the model copy is always kept in the run data store.</summary>
    public bool KeepModelCopy { get; init; }

    /// <summary>Default retry count for safe-to-repeat steps (0-5, default 0).</summary>
    public int RetryDefault { get; init; } = 0;

    /// <summary>Default delay in seconds between retries (default 5).</summary>
    public double RetryDelaySec { get; init; } = 5;

    /// <summary>Maximum seconds Revit main window can be non-responding before aborting with revit_hung (default 180).</summary>
    public double HangTimeoutSec { get; init; } = 180;

    /// <summary>Rerun scenario once on revit_crashed into a new run folder (0 or 1, default 0).</summary>
    public int RerunOnCrash { get; init; } = 0;

    public ScenarioTimeouts Timeouts { get; init; } = new();

    public IReadOnlyList<ScenarioStep> Steps { get; init; } = Array.Empty<ScenarioStep>();

    public IReadOnlyList<ScenarioCheck> Checks { get; init; } = Array.Empty<ScenarioCheck>();

    /// <summary><c>manual_steps</c>: things a person must do or look at; non-empty caps the verdict at SUSPECT.</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = Array.Empty<string>();
}

public sealed class ScenarioTimeouts
{
    public double StartupMin { get; init; } = 5;

    public double OpenModelMin { get; init; } = 10;

    public double StepMin { get; init; } = 30;
}

/// <summary>One step: either a gateway tool call (<see cref="Tool"/>) or a UI script (<see cref="Ui"/>).</summary>
public sealed class ScenarioStep
{
    public int Index { get; init; }

    /// <summary>1-based line in scenario.yaml (0 when unknown).</summary>
    public int Line { get; init; }

    public string? Name { get; init; }

    public string? Tool { get; init; }

    /// <summary>Path of a UI script JSON (relative to the scenario folder; format in docs/agent-guide/ui-automation.md).</summary>
    public string? Ui { get; init; }

    public JsonObject Args { get; init; } = new();

    /// <summary>stop (default) | continue.</summary>
    public string OnError { get; init; } = "stop";

    /// <summary>action (default) | setup.</summary>
    public string Purpose { get; init; } = "action";

    public string? Why { get; init; }

    public int? TimeoutSec { get; init; }

    public bool LeaveOpen { get; init; }

    /// <summary>Per-step retry count override (0–5, default from the scenario RetryDefault).</summary>
    public int? Retry { get; init; }

    /// <summary>Per-step retry delay override in seconds (default from the scenario RetryDelaySec).</summary>
    public double? RetryDelaySec { get; init; }

    /// <summary>True if an action step is explicitly safe to retry on error/timeout.</summary>
    public bool RetrySafe { get; init; }

    public bool IsUi => Ui is not null;

    public bool ContinueOnError => string.Equals(OnError, "continue", StringComparison.OrdinalIgnoreCase);

    public bool IsSetup => string.Equals(Purpose, "setup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Step code (<c>stepCode</c> in step_status.json) and data file name: name, else tool, else ui-NN.</summary>
    public string Code => !string.IsNullOrWhiteSpace(Name) ? Name! : Tool ?? $"ui-{Index + 1:D2}";
}

/// <summary>One attempt to execute a step (recorded on retry).</summary>
public sealed class StepAttemptRecord
{
    public int Attempt { get; init; }

    public required string Status { get; init; }

    public string? ErrorCode { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset Started { get; init; }

    public long DurationMs { get; init; }
}

public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>One lint finding of <c>scenario_validate</c> (also used for load errors).</summary>
public sealed record ScenarioIssue(IssueSeverity Severity, string Code, string Message, string? File = null, int? Line = null)
{
    public static ScenarioIssue Error(string code, string message, string? file = null, int? line = null) => new(IssueSeverity.Error, code, message, file, line);

    public static ScenarioIssue Warning(string code, string message, string? file = null, int? line = null) => new(IssueSeverity.Warning, code, message, file, line);

    public object ToJson() => new
    {
        severity = Severity == IssueSeverity.Error ? "error" : "warning",
        code = Code,
        message = Message,
        file = File,
        line = Line,
    };
}
