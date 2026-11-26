using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RevitAgent.Core.Scenario;

/// <summary>Result of reading a scenario folder: the definition (null when unusable) plus findings.</summary>
public sealed class ScenarioLoadResult
{
    public ScenarioDefinition? Scenario { get; internal set; }

    public List<ScenarioIssue> Issues { get; } = new();

    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// Reads <c>scenario.yaml</c> with YamlDotNet's representation model (keeps line numbers for findings).
/// Plain YAML scalars become JSON numbers/booleans/null; quoted scalars stay strings, so
/// <c>'{dataset}\model.rvt'</c> and <c>"42"</c> are passed to tools exactly as written.
/// </summary>
public static class ScenarioLoader
{
    /// <summary>Keys RevitAgent's runner understands.</summary>
    public static readonly IReadOnlySet<string> SupportedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "description", "revit_version", "model", "keep_model_copy", "timeouts", "steps", "checks", "manual_steps",
        "retry_default", "retry_delay_sec", "hang_timeout_sec", "rerun_on_crash",
    };

    private static readonly HashSet<string> CheckKeys = new(StringComparer.Ordinal)
    {
        "id", "title", "expect", "severity", "note",
    };

    private static readonly Regex CheckIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

    private static readonly HashSet<string> StepKeys = new(StringComparer.Ordinal)
    {
        "tool", "args", "name", "on_error", "purpose", "why", "ui", "timeout_sec", "leave_open",
        "retry", "retry_delay_sec", "retry_safe",
    };

    private static readonly Regex IntPattern = new(@"^[-+]?\d+$", RegexOptions.Compiled);
    private static readonly Regex FloatPattern = new(@"^[-+]?(\d+\.\d*|\.\d+|\d+)([eE][-+]?\d+)?$", RegexOptions.Compiled);

    /// <summary>Accepts a scenario folder or the path of its scenario.yaml.</summary>
    public static ScenarioLoadResult Load(string scenarioDirOrFile)
    {
        var full = Path.GetFullPath(scenarioDirOrFile);
        var scenarioPath = File.Exists(full) ? full : Path.Combine(full, ScenarioDefinition.FileName);
        var dir = Path.GetDirectoryName(scenarioPath)!;
        if (!File.Exists(scenarioPath))
        {
            var missing = new ScenarioLoadResult();
            missing.Issues.Add(ScenarioIssue.Error("scenario-missing", $"No {ScenarioDefinition.FileName} in '{dir}'. A scenario is a folder with scenario.yaml (+ ui\\*.json UI scripts).", scenarioPath));
            return missing;
        }

        return Parse(File.ReadAllText(scenarioPath), dir, scenarioPath);
    }

    public static ScenarioLoadResult Parse(string yaml, string scenarioDir, string? scenarioPath = null)
    {
        scenarioPath ??= Path.Combine(scenarioDir, ScenarioDefinition.FileName);
        var result = new ScenarioLoadResult();
        var file = ScenarioDefinition.FileName;

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            result.Issues.Add(ScenarioIssue.Error(
                "yaml-syntax",
                $"YAML parse error at line {ex.Start.Line}, column {ex.Start.Column}: {message}. Windows paths and '{{...}}' values belong in single quotes, e.g. model: '{{dataset}}\\model.rvt'.",
                file,
                (int)ex.Start.Line));
            return result;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            result.Issues.Add(ScenarioIssue.Error("yaml-root", "scenario.yaml must be a YAML mapping (key: value lines).", file, 1));
            return result;
        }

        string? name = null, description = null, model = null;
        int? revitVersion = null;
        bool keepCopy = false;
        int retryDefault = 0;
        double retryDelaySec = 5;
        double hangTimeoutSec = 180;
        int rerunOnCrash = 0;
        var timeouts = new ScenarioTimeouts();
        var steps = new List<ScenarioStep>();
        var manual = new List<string>();
        var checks = new List<ScenarioCheck>();
        var sawSteps = false;

        foreach (var (keyNode, valueNode) in root.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
            var line = (int)keyNode.Start.Line;
            switch (key)
            {
                case "name":
                    name = Scalar(valueNode);
                    break;
                case "description":
                    description = Scalar(valueNode);
                    break;
                case "model":
                    model = Scalar(valueNode);
                    break;
                case "revit_version":
                    if (int.TryParse(Scalar(valueNode), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                    {
                        revitVersion = y;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(valueNode)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("revit-version", $"revit_version must be a year like 2024 (got '{Scalar(valueNode)}').", file, line));
                    }

                    break;
                case "keep_model_copy":
                    keepCopy = Bool(valueNode, result, file, key);
                    break;
                case "retry_default":
                    if (int.TryParse(Scalar(valueNode), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rd))
                    {
                        retryDefault = rd;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(valueNode)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("retry-default", $"retry_default must be an integer (got '{Scalar(valueNode)}').", file, line));
                    }

                    break;
                case "retry_delay_sec":
                    if (double.TryParse(Scalar(valueNode), NumberStyles.Float, CultureInfo.InvariantCulture, out var rds))
                    {
                        retryDelaySec = rds;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(valueNode)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("retry-delay", $"retry_delay_sec must be a number (got '{Scalar(valueNode)}').", file, line));
                    }

                    break;
                case "hang_timeout_sec":
                    if (double.TryParse(Scalar(valueNode), NumberStyles.Float, CultureInfo.InvariantCulture, out var hts))
                    {
                        hangTimeoutSec = hts;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(valueNode)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("hang-timeout", $"hang_timeout_sec must be a number (got '{Scalar(valueNode)}').", file, line));
                    }

                    break;
                case "rerun_on_crash":
                    if (int.TryParse(Scalar(valueNode), NumberStyles.Integer, CultureInfo.InvariantCulture, out var roc))
                    {
                        rerunOnCrash = roc;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(valueNode)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("rerun-on-crash", $"rerun_on_crash must be 0 or 1 (got '{Scalar(valueNode)}').", file, line));
                    }

                    break;
                case "retry_safe":
                    result.Issues.Add(ScenarioIssue.Error("retry-safe-scenario", "retry_safe is only allowed on steps, not at the scenario level.", file, line));
                    break;
                case "timeouts":
                    timeouts = ReadTimeouts(valueNode, result, file);
                    break;
                case "manual_steps":
                    if (valueNode is YamlSequenceNode ms)
                    {
                        manual.AddRange(ms.Children.Select(c => Scalar(c) ?? c.ToString()));
                    }

                    break;
                case "steps":
                    sawSteps = true;
                    if (valueNode is not YamlSequenceNode seq)
                    {
                        result.Issues.Add(ScenarioIssue.Error("steps-type", "steps must be a list ('- tool: ...' items).", file, line));
                        break;
                    }

                    foreach (var item in seq.Children)
                    {
                        var step = ReadStep(item, steps.Count, result, file);
                        if (step is not null)
                        {
                            steps.Add(step);
                        }
                    }

                    break;
                case "checks":
                    if (valueNode is YamlSequenceNode cseq)
                    {
                        foreach (var item in cseq.Children)
                        {
                            var check = ReadCheck(item, checks.Count, result, file);
                            if (check is not null)
                            {
                                checks.Add(check);
                            }
                        }
                    }
                    else if (!(valueNode is YamlScalarNode { Value: null or "" or "~" or "null" }))
                    {
                        result.Issues.Add(ScenarioIssue.Error("checks-type", "checks must be a list ('- id: C1' items).", file, line));
                    }

                    break;
                default:
                    result.Issues.Add(ScenarioIssue.Warning("unknown-key", $"Unknown key '{key}' (ignored). Supported: {string.Join(", ", SupportedKeys.OrderBy(k => k))}.", file, line));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            result.Issues.Add(ScenarioIssue.Error("scenario-name", "Missing 'name: <scenario name>' (by convention the folder name).", file, 1));
        }

        if (!sawSteps || steps.Count == 0)
        {
            result.Issues.Add(ScenarioIssue.Error("steps-empty", "scenario.yaml has no steps. Add '- tool: <gateway tool>' or '- ui: ui\\<script>.json' items under 'steps:'.", file, 1));
        }

        if (result.HasErrors && string.IsNullOrWhiteSpace(name))
        {
            return result;
        }

        result.Scenario = new ScenarioDefinition
        {
            Directory = scenarioDir,
            ScenarioPath = scenarioPath,
            Name = name ?? Path.GetFileName(scenarioDir),
            Description = description,
            RevitVersion = revitVersion,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            KeepModelCopy = keepCopy,
            RetryDefault = retryDefault,
            RetryDelaySec = retryDelaySec,
            HangTimeoutSec = hangTimeoutSec,
            RerunOnCrash = rerunOnCrash,
            Timeouts = timeouts,
            Steps = steps,
            Checks = checks,
            ManualSteps = manual,
        };
        return result;
    }

    /// <summary>Converts a YAML node into JSON (plain scalars typed, quoted scalars strings).</summary>
    public static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlScalarNode s => ScalarToJson(s),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToJson).ToArray()),
        YamlMappingNode map => ToJsonObject(map),
        _ => null,
    };

    private static JsonObject ToJsonObject(YamlMappingNode map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map.Children)
        {
            o[(k as YamlScalarNode)?.Value ?? k.ToString()] = ToJson(v);
        }

        return o;
    }

    private static JsonNode? ScalarToJson(YamlScalarNode s)
    {
        var v = s.Value ?? string.Empty;
        if (s.Style is not ScalarStyle.Plain and not ScalarStyle.Any)
        {
            return JsonValue.Create(v);
        }

        if (v.Length == 0 || v == "~" || v.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (v.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(true);
        }

        if (v.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(false);
        }

        if (IntPattern.IsMatch(v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (FloatPattern.IsMatch(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(v);
    }

    private static ScenarioStep? ReadStep(YamlNode item, int index, ScenarioLoadResult result, string file)
    {
        var line = (int)item.Start.Line;
        if (item is not YamlMappingNode map)
        {
            result.Issues.Add(ScenarioIssue.Error("step-type", $"Step {index + 1} must be a mapping ('- tool: ...').", file, line));
            return null;
        }

        string? tool = null, ui = null, name = null, why = null;
        string onError = "stop", purpose = "action";
        int? timeoutSec = null;
        bool leaveOpen = false;
        int? retry = null;
        double? retryDelaySec = null;
        bool retrySafe = false;
        var args = new JsonObject();

        foreach (var (k, v) in map.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? string.Empty;
            switch (key)
            {
                case "tool":
                    tool = Scalar(v);
                    break;
                case "ui":
                    ui = Scalar(v);
                    break;
                case "name":
                    name = Scalar(v);
                    break;
                case "why":
                    why = Scalar(v);
                    break;
                case "on_error":
                    onError = (Scalar(v) ?? "stop").Trim().ToLowerInvariant();
                    if (onError is not ("stop" or "continue"))
                    {
                        result.Issues.Add(ScenarioIssue.Error("step-on-error", $"Step {index + 1}: on_error must be 'stop' or 'continue' (got '{onError}').", file, (int)k.Start.Line));
                    }

                    break;
                case "purpose":
                    purpose = (Scalar(v) ?? "action").Trim().ToLowerInvariant();
                    if (purpose is not ("action" or "setup"))
                    {
                        result.Issues.Add(ScenarioIssue.Error("step-purpose", $"Step {index + 1}: purpose must be 'action' or 'setup' (got '{purpose}').", file, (int)k.Start.Line));
                    }

                    break;
                case "timeout_sec":
                    if (int.TryParse(Scalar(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0)
                    {
                        timeoutSec = t;
                    }
                    else
                    {
                        result.Issues.Add(ScenarioIssue.Error("step-timeout", $"Step {index + 1}: timeout_sec must be a positive integer.", file, (int)k.Start.Line));
                    }

                    break;
                case "leave_open":
                    leaveOpen = Bool(v, result, file, key);
                    break;
                case "retry":
                    if (int.TryParse(Scalar(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                    {
                        retry = r;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(v)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("step-retry", $"Step {index + 1}: retry must be an integer (got '{Scalar(v)}').", file, (int)k.Start.Line));
                    }

                    break;
                case "retry_delay_sec":
                    if (double.TryParse(Scalar(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var rd))
                    {
                        retryDelaySec = rd;
                    }
                    else if (!string.IsNullOrWhiteSpace(Scalar(v)))
                    {
                        result.Issues.Add(ScenarioIssue.Error("step-retry-delay", $"Step {index + 1}: retry_delay_sec must be a number (got '{Scalar(v)}').", file, (int)k.Start.Line));
                    }

                    break;
                case "retry_safe":
                    retrySafe = Bool(v, result, file, key);
                    break;
                case "args":
                    if (v is YamlMappingNode am)
                    {
                        args = ToJsonObject(am);
                    }
                    else if (!(v is YamlScalarNode { Value: null or "" or "~" or "null" }))
                    {
                        result.Issues.Add(ScenarioIssue.Error("step-args", $"Step {index + 1}: args must be a mapping, e.g. args: {{ category: Levels }}.", file, (int)k.Start.Line));
                    }

                    break;
                default:
                    if (!StepKeys.Contains(key))
                    {
                        result.Issues.Add(ScenarioIssue.Warning("step-unknown-key", $"Step {index + 1}: unknown key '{key}' (ignored).", file, (int)k.Start.Line));
                    }

                    break;
            }
        }

        if (tool is null == (ui is null))
        {
            result.Issues.Add(ScenarioIssue.Error("step-kind", $"Step {index + 1}: a step has exactly one of 'tool' (gateway tool call) or 'ui' (UI script).", file, line));
            return null;
        }

        return new ScenarioStep
        {
            Index = index,
            Line = line,
            Name = name,
            Tool = tool?.Trim(),
            Ui = ui?.Trim(),
            Args = args,
            OnError = onError is "continue" ? "continue" : "stop",
            Purpose = purpose is "setup" ? "setup" : "action",
            Why = why,
            TimeoutSec = timeoutSec,
            LeaveOpen = leaveOpen,
            Retry = retry,
            RetryDelaySec = retryDelaySec,
            RetrySafe = retrySafe,
        };
    }

    private static ScenarioCheck? ReadCheck(YamlNode item, int index, ScenarioLoadResult result, string file)
    {
        var line = (int)item.Start.Line;
        if (item is not YamlMappingNode map)
        {
            result.Issues.Add(ScenarioIssue.Error("check-type", $"Check {index + 1} must be a mapping ('- id: C1' with title, expect, severity, note).", file, line));
            return null;
        }

        string? id = null, title = null, expect = null, note = null;
        var severity = CheckSeverity.Fail;
        foreach (var (k, v) in map.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? string.Empty;
            switch (key)
            {
                case "id":
                    id = Scalar(v)?.Trim();
                    break;
                case "title":
                    title = Scalar(v);
                    break;
                case "expect":
                    expect = Scalar(v)?.Trim();
                    break;
                case "note":
                    note = Scalar(v)?.Trim();
                    break;
                case "severity":
                    severity = (Scalar(v) ?? CheckSeverity.Fail).Trim().ToLowerInvariant();
                    if (!CheckSeverity.IsValid(severity))
                    {
                        result.Issues.Add(ScenarioIssue.Error("check-severity", $"Check {id ?? (index + 1).ToString(CultureInfo.InvariantCulture)}: severity must be 'fail' or 'warn' (got '{severity}').", file, (int)k.Start.Line));
                        severity = CheckSeverity.Fail;
                    }

                    break;
                default:
                    if (!CheckKeys.Contains(key))
                    {
                        result.Issues.Add(ScenarioIssue.Warning("check-unknown-key", $"Check {index + 1}: unknown key '{key}' (ignored). Keys: {string.Join(", ", CheckKeys)}.", file, (int)k.Start.Line));
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id) || !CheckIdPattern.IsMatch(id))
        {
            result.Issues.Add(ScenarioIssue.Error("check-id", $"Check {index + 1}: 'id' is required (letters, digits, '-', '_', '.'; e.g. C1 or levels-roof).", file, line));
            return null;
        }

        if (string.IsNullOrWhiteSpace(expect))
        {
            result.Issues.Add(ScenarioIssue.Error("check-expect", $"Check {id}: 'expect' is required, e.g. expect: count(levels.json -> elements) >= 1.", file, line));
            return null;
        }

        return new ScenarioCheck
        {
            Id = id,
            Title = title?.Trim() ?? string.Empty,
            Expect = expect,
            Severity = severity,
            Note = string.IsNullOrWhiteSpace(note) ? null : note,
            Line = line,
        };
    }

    private static ScenarioTimeouts ReadTimeouts(YamlNode node, ScenarioLoadResult result, string file)
    {
        if (node is not YamlMappingNode map)
        {
            result.Issues.Add(ScenarioIssue.Error("timeouts", "timeouts must be a mapping { startup_min, open_model_min, step_min }.", file, (int)node.Start.Line));
            return new ScenarioTimeouts();
        }

        double startup = 5, open = 10, step = 30;
        foreach (var (k, v) in map.Children)
        {
            var key = (k as YamlScalarNode)?.Value ?? string.Empty;
            if (!double.TryParse(Scalar(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
            {
                result.Issues.Add(ScenarioIssue.Error("timeouts", $"timeouts.{key} must be a positive number of minutes.", file, (int)k.Start.Line));
                continue;
            }

            switch (key)
            {
                case "startup_min":
                    startup = minutes;
                    break;
                case "open_model_min":
                    open = minutes;
                    break;
                case "step_min":
                    step = minutes;
                    break;
                default:
                    result.Issues.Add(ScenarioIssue.Warning("timeouts", $"Unknown timeouts.{key} (ignored).", file, (int)k.Start.Line));
                    break;
            }
        }

        return new ScenarioTimeouts { StartupMin = startup, OpenModelMin = open, StepMin = step };
    }

    private static string? Scalar(YamlNode node) => (node as YamlScalarNode)?.Value;

    private static bool Bool(YamlNode node, ScenarioLoadResult result, string file, string key)
    {
        var s = Scalar(node);
        if (bool.TryParse(s, out var b))
        {
            return b;
        }

        if (!string.IsNullOrWhiteSpace(s))
        {
            result.Issues.Add(ScenarioIssue.Error("bool", $"'{key}' must be true or false (got '{s}').", file, (int)node.Start.Line));
        }

        return false;
    }
}
