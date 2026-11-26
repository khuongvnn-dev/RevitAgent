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
        "Run a UI test script against one process: {name, timeout (s per step, default 10), screenshots: fail|each|none, continueOnError, vars, dialogs (rules file(s) or array), " +
        "steps: [{do, target (ref or selector), window, text, is, match, exact, count, keyboard, verify, append, clear, mouse, out, timeout, continueOnError, role, name}]}. " +
        "Verbs: windows use snapshot find inspect text point screenshot click dblclick rightclick hover type keys focus toggle expand collapse select scroll drag close wait expect table dialogs sleep. " +
        "Starts at the main window of 'pid' (${env:REVITAGENT_UI_HOST_PID} = pid, ${env:REVITAGENT_UI_OUT} = out dir, ${dir}, ${var}); every window must belong to that pid. " +
        "Rejected before running (PERMISSION_DENIED) when it contains launch/run, close or Alt+F4 without a window, typing into password/token fields, or dialog rules that would answer " +
        "security/unsigned/license/recovery/delete/overwrite/replace dialogs. Writes report.json (report_path), report.md, NN_fail.png and NN_fail_snapshot.txt under out_dir. " +
        "Returns status PASSED | FAILED (a 'check' step on an add-in window failed = product bug) | BLOCKED (a 'nav' step on the host main window or a layout problem failed = not measurable), " +
        "passed/failed/blocked counts, verdict, and per-step results with role nav|check. Takes as long as the script (budget timeout_s, default 600).";

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
        var runDir = ResolveOutDir(call.GetString("out_dir"), script.Name);
        var overall = Seconds(call, "timeout_s", 600, 1800);
        var vars = ReadVars(call);
        var screenshots = call.GetString("screenshots");
        var session = "script-" + call.CallId;

        var report = await Ui.RunAsync(session, overall + TimeSpan.FromSeconds(60), input: true, cancellationToken, op =>
            new UiScriptRunner(op, script, validation.Rules, pid, runDir, screenshots, overall, vars).Run(validation.Warnings)).ConfigureAwait(false);

        return ToolResult.Json(new
        {
            // status/failed follow these rules: only a failing 'check' step (add-in misbehaved) is FAILED;
            // a failing navigation/layout step makes the run BLOCKED (not measurable), optional ones only WARN.
            status = report.Measured switch { "SUCCEEDED" => "PASSED", var m => m },
            passed = report.Passed,
            failed = report.Steps.Count(s => s.Outcome == "FAIL"),
            blocked = report.Steps.Count(s => s.Outcome == "BLOCK"),
            warned = report.Steps.Count(s => s.Outcome == "WARN"),
            skipped = report.Skipped,
            report_path = Path.Combine(report.RunDir, "report.json"),
            report_md = Path.Combine(report.RunDir, "report.md"),
            out_dir = report.RunDir,
            verdict = report.Verdict,
            failed_steps = report.Failed,
            measured = report.Measured,
            name = report.Name,
            seconds = report.Seconds,
            warnings = report.Warnings.Count > 0 ? report.Warnings : null,
            steps = report.Steps.Select(r => new
            {
                n = r.N,
                step = r.Step,
                status = r.Status,
                ms = r.Ms,
                role = r.Role,
                outcome = r.Outcome != r.Status ? r.Outcome : null,
                output = r.Output.Length > 0 ? UiText.Clip(r.Output, 300) : null,
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

    private string ResolveOutDir(string? requested, string name)
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

            dir = Path.Combine(Ui.Paths.RunsDir, "ui-scripts", $"{safe}_{DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}");
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
