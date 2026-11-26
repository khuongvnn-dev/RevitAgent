using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui.Script;

namespace RevitAgent.Core.Ui.Tools;

/// <summary>ui_run_script — run a UI test script against one process.</summary>
public sealed class UiRunScriptTool : UiToolBase
{
    public UiRunScriptTool(UiAutomationService ui)
        : base(ui)
    {
    }

    public override string Name => "ui_run_script";

    public override ToolPermission Permission => ToolPermission.Interact;

    public override TimeSpan Timeout => TimeSpan.FromSeconds(1900);

    public override string Description =>
        "Run a UI script against one process: {name, timeout (s per step, default 10), screenshots: fail|each|none, vars, dialogs (rules file(s) or array), " +
        "steps: [{action, name, optional, window, ref|selector, timeout_s, ...that tool's arguments}]}. action = a ui_* tool name without the prefix " +
        "(list_windows snapshot find inspect screenshot click type select toggle expand keys scroll wait expect read_table), plus attach (window = the current window) and pause (ms). " +
        "Steps run in order against pid; every step is required unless optional:true; the first failed or blocked required step stops the run. " +
        "Variables: ${var}, ${env:REVITAGENT_UI_HOST_PID}, ${env:REVITAGENT_UI_OUT}, ${dir}. Rejected up front (PERMISSION_DENIED): launch/run/close, Alt+F4 without a window, typing into password/token fields, " +
        "security/license/recovery/delete/overwrite dialogs. Returns status passed | failed | blocked (a forbidden dialog or safety refusal), counts, failed_steps and per-step " +
        "status passed|failed|skipped|blocked with observed/expected/error; report.json, report.md and failure screenshots go under out_dir. See docs/agent-guide/ui-automation.md.";

    protected override Schema BuildSchema() => Schema.Object()
        .Integer("pid", "Process to drive (Revit pid). Required: the script may only touch windows of this process.", required: true)
        .String("script_path", "Path of the script JSON file (relative paths are resolved against the gateway home).")
        .Raw("script", new System.Text.Json.Nodes.JsonObject
        {
            ["description"] = "Inline script: a JSON object (or a JSON string) in the UI script format. Alternative to script_path.",
            ["type"] = new System.Text.Json.Nodes.JsonArray("object", "string"),
        })
        .String("out_dir", "Result folder (inside the gateway runs or captures folder; relative = under runs). Default: <home>\\runs\\ui-scripts\\<name>_<time>.")
        .AnyObject("vars", "Extra ${name} variables (override the script's vars).")
        .String("screenshots", "Override the script's screenshots setting.", enumValues: new[] { "fail", "each", "none" })
        .Boolean("validate_only", "Only validate the script (no UI is touched).", defaultValue: false)
        .Number("timeout_s", "Overall time budget in seconds (default 600, max 1800).", defaultValue: 600);

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = call.RequireInt("pid");
        var script = LoadScript(call);
        var validation = UiScriptValidator.Validate(script);
        if (!validation.Ok)
        {
            var forbidden = validation.Errors.Any(e => e.Forbidden);
            throw new GatewayException(
                forbidden ? GatewayErrorCodes.PermissionDenied : GatewayErrorCodes.ValidationFailed,
                $"Script rejected ({validation.Errors.Count} problem(s)): {string.Join("; ", validation.Errors.Select(e => e.Message))}",
                new { errors = validation.Errors.Select(e => new { message = e.Message, forbidden = e.Forbidden }), warnings = validation.Warnings });
        }

        if (call.GetBool("validate_only"))
        {
            return ToolResult.Json(new
            {
                valid = true,
                name = script.Name,
                steps = script.Steps.Count,
                dialog_rules = validation.Rules.Count,
                warnings = validation.Warnings,
            });
        }

        WindowSpec.EnsureProcessAlive(pid);
        UiEnvironment.EnsureDesktop();
        UiEnvironment.EnsureNotElevatedAboveUs(pid);
        var runDir = ResolveOutDir(call.GetString("out_dir"), script.Name, call.SessionId, pid);
        var overall = Seconds(call, "timeout_s", 600, 1800);
        var vars = ReadVars(call);
        var screenshots = call.GetString("screenshots");
        var session = "script-" + call.CallId;

        var report = await Ui.RunAsync(session, overall + TimeSpan.FromSeconds(60), input: true, cancellationToken, op =>
            new UiScriptRunner(op, script, validation.Rules, pid, runDir, screenshots, overall, vars).Run(validation.Warnings)).ConfigureAwait(false);

        Ui.Evidence.RecordDirectory(report.RunDir, call.Name, call.SessionId, pid);
        return ToolResult.Json(new
        {
            status = report.Verdict,
            passed = report.Passed,
            failed = report.Failed,
            blocked = report.Blocked,
            optional_failed = report.OptionalFailed,
            skipped = report.Skipped,
            failed_steps = report.Steps.Where(r => r.Status is "failed" or "blocked" && !r.Optional).Select(r => r.N).ToArray(),
            report_path = Path.Combine(report.RunDir, "report.json"),
            report_md = Path.Combine(report.RunDir, "report.md"),
            out_dir = report.RunDir,
            name = report.Name,
            seconds = report.Seconds,
            warnings = report.Warnings.Count > 0 ? report.Warnings : null,
            steps = report.Steps.Select(r => new
            {
                n = r.N,
                step = r.Step,
                status = r.Status,
                optional = r.Optional ? true : (bool?)null,
                ms = r.Ms,
                observed = r.Observed.Length > 0 ? UiText.Clip(r.Observed, 300) : null,
                expected = r.Expected.Length > 0 ? r.Expected : null,
                error = r.Error.Length > 0 ? r.Error : null,
                screenshot = r.Screenshot.Length > 0 ? r.Screenshot : null,
            }),
        });
    }

    private UiScript LoadScript(ToolCall call)
    {
        var path = call.GetString("script_path");
        var inline = call.Raw("script");
        if (path is null == inline is null)
        {
            throw GatewayException.Validation("Pass exactly one of 'script_path' or 'script'.");
        }

        if (path is not null)
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Ui.Paths.Root, path);
            if (!File.Exists(full))
            {
                throw GatewayException.NotFound($"Script file not found: {full}");
            }

            return UiScript.Load(full);
        }

        var v = inline!.Value;
        return v.ValueKind == JsonValueKind.String ? UiScript.Parse(v.GetString() ?? string.Empty) : UiScript.FromElement(v.Clone());
    }

    private string ResolveOutDir(string? requested, string name, string sessionId, int pid)
    {
        var root = Path.GetFullPath(Ui.Paths.Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string dir;
        if (string.IsNullOrWhiteSpace(requested))
        {
            var safe = Regex.Replace(name, "[^\\w\\-]+", "_");
            if (safe.Length > 60)
            {
                safe = safe[..60];
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            dir = Ui.Evidence.EvidenceDirFor(sessionId, pid) is { } evidence
                ? Path.Combine(evidence, "ui-scripts", $"{safe}_{stamp}")
                : Path.Combine(Ui.Paths.RunsDir, "ui-scripts", $"{safe}_{stamp}");
        }
        else
        {
            dir = UiPaths.Evidence(Ui.Paths, requested, Ui.Paths.RunsDir);
        }

        if (!(dir + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw GatewayException.Denied($"out_dir must be inside the gateway home ({root}); got '{dir}'.");
        }

        return dir;
    }

    private static Dictionary<string, string>? ReadVars(ToolCall call)
    {
        if (call.Raw("vars") is not { ValueKind: JsonValueKind.Object } v)
        {
            return null;
        }

        return v.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.GetRawText(),
            StringComparer.OrdinalIgnoreCase);
    }
}
