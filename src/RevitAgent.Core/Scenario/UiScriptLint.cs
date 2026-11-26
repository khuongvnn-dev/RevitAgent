using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Lint of a UI step script (the UI script JSON of docs/agent-guide/ui-automation.md):
/// known <c>do</c> commands only; no <c>launch</c>/<c>run</c> (the runner owns Revit, no nested files); <c>close</c> and Alt+F4
/// need an explicit <c>window</c>; never type into password/token fields; dialog rules must never auto-answer
/// security / unsigned / license / recovery / delete / overwrite / replace dialogs. RevitAgent adds: a step acting inside a
/// window titled like a security/license/recovery dialog is an error (delete/overwrite titles: warning, add-in windows may be named so).
/// </summary>
public static class UiScriptLint
{
    public static readonly IReadOnlySet<string> KnownCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "windows", "use", "snapshot", "find", "inspect", "text", "point", "screenshot", "click", "dblclick", "rightclick",
        "hover", "type", "keys", "focus", "toggle", "expand", "collapse", "select", "scroll", "drag", "close", "wait", "expect",
        "table", "dialogs", "sleep", "launch", "run",
    };

    /// <summary>Dialog title fragments that must never be answered automatically (English and Vietnamese Revit UI strings).</summary>
    public static readonly IReadOnlyList<string> NeverAutoAnswer = new[]
    {
        "security", "unsigned", "bao mat", "bảo mật", "licens", "licenc", "recover", "khôi phục", "khoi phuc",
        "delete", "overwrite", "replace", "xóa", "xoá", "ghi đè", "ghi de",
    };

    private static readonly string[] HardForbiddenTitles = { "security", "unsigned", "bảo mật", "bao mat", "licens", "licenc", "recover", "khôi phục", "khoi phuc" };
    private static readonly HashSet<string> Acting = new(StringComparer.Ordinal) { "click", "dblclick", "rightclick", "keys", "type", "select", "toggle", "close", "expand" };

    public static List<ScenarioIssue> Lint(string scriptPath, string displayName)
    {
        var issues = new List<ScenarioIssue>();
        if (!File.Exists(scriptPath))
        {
            issues.Add(ScenarioIssue.Error("ui-missing", $"UI script not found: {scriptPath}", displayName));
            return issues;
        }

        JsonElement spec;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(scriptPath, Encoding.UTF8), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            spec = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            issues.Add(ScenarioIssue.Error("ui-json", $"UI script is not valid JSON: {ex.Message}", displayName, (int?)(ex.LineNumber + 1)));
            return issues;
        }

        if (spec.ValueKind != JsonValueKind.Object || !spec.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
        {
            issues.Add(ScenarioIssue.Error("ui-steps", "UI script needs a non-empty \"steps\" array (see docs/agent-guide/ui-automation.md).", displayName));
            return issues;
        }

        if (Str(spec, "window") is { Length: > 0 } w0)
        {
            issues.Add(ScenarioIssue.Warning("ui-window", $"Top-level window '{w0}': leave it empty so the script starts on the run's Revit main window.", displayName));
        }

        if (Str(spec, "screenshots") is { Length: > 0 } shots && shots.ToLowerInvariant() is not ("fail" or "each" or "none"))
        {
            issues.Add(ScenarioIssue.Error("ui-screenshots", $"screenshots '{shots}' must be fail | each | none.", displayName));
        }

        var n = 0;
        foreach (var st in steps.EnumerateArray())
        {
            n++;
            var at = $"steps[{n}]";
            var cmd = (Str(st, "do") ?? string.Empty).ToLowerInvariant();
            var window = Str(st, "window") ?? string.Empty;
            if (cmd.Length == 0)
            {
                issues.Add(ScenarioIssue.Error("ui-do", $"{at} has no \"do\".", displayName));
                continue;
            }

            if (Str(st, "role") is { Length: > 0 } role && role is not ("nav" or "check"))
            {
                issues.Add(ScenarioIssue.Error("ui-role", $"{at} role '{role}' must be nav | check.", displayName));
            }

            if (!KnownCommands.Contains(cmd))
            {
                issues.Add(ScenarioIssue.Error("ui-do", $"{at} do '{cmd}' is not a UI script command (see docs/agent-guide/ui-automation.md).", displayName));
                continue;
            }

            if (cmd is "launch" or "run")
            {
                issues.Add(ScenarioIssue.Error("ui-forbidden", $"{at} do '{cmd}' is forbidden in a scenario UI step: the runner starts/stops Revit (no launch) and scripts are not nested (no run).", displayName));
                continue;
            }

            if (cmd == "close" && window.Length == 0)
            {
                issues.Add(ScenarioIssue.Error("ui-forbidden", $"{at} close without \"window\" would close Revit's main window — set window to the dialog title.", displayName));
            }

            if (cmd == "keys")
            {
                var k = Str(st, "text") ?? Str(st, "target") ?? string.Empty;
                if (k.Contains("%{F4}", StringComparison.OrdinalIgnoreCase) && window.Length == 0)
                {
                    issues.Add(ScenarioIssue.Error("ui-forbidden", $"{at} Alt+F4 without \"window\" would close Revit.", displayName));
                }

                if (Regex.IsMatch(k, @"\^\+?[sSoO]"))
                {
                    issues.Add(ScenarioIssue.Warning("ui-save", $"{at} keys '{k}' (Ctrl+S / Ctrl+O): only the sandbox copy may be saved/opened; the runner checks doc_info after the step.", displayName));
                }
            }

            if (cmd == "type")
            {
                var t = (Str(st, "target") ?? string.Empty) + " " + (Str(st, "name") ?? string.Empty);
                if (Regex.IsMatch(t, @"(?i)pass(word)?|m[aậ]t ?kh[aẩ]u|token|secret"))
                {
                    issues.Add(ScenarioIssue.Error("ui-secret", $"{at} types into '{t.Trim()}': never type passwords/secrets in a UI step.", displayName));
                }
            }

            if (window.Length > 0 && Acting.Contains(cmd))
            {
                var lw = window.ToLowerInvariant();
                var hit = NeverAutoAnswer.FirstOrDefault(b => lw.Contains(b, StringComparison.Ordinal));
                if (hit is not null)
                {
                    var hard = HardForbiddenTitles.Any(b => lw.Contains(b, StringComparison.Ordinal));
                    var msg = $"{at} acts ('{cmd}') in window '{window}' — never answer security/license/recovery/delete/overwrite dialogs in a scenario (stop and ask the user).";
                    issues.Add(hard ? ScenarioIssue.Error("ui-forbidden-dialog", msg, displayName) : ScenarioIssue.Warning("ui-forbidden-dialog", msg + " If this is an add-in window, ignore this warning.", displayName));
                }
            }
        }

        foreach (var rule in DialogRules(spec, scriptPath, displayName, issues))
        {
            var text = ((Str(rule, "title") ?? string.Empty) + " " + (Str(rule, "text") ?? string.Empty)).ToLowerInvariant();
            var bad = NeverAutoAnswer.FirstOrDefault(b => text.Contains(b, StringComparison.Ordinal));
            if (bad is not null)
            {
                issues.Add(ScenarioIssue.Error("ui-dialog-rule", $"dialogs rule '{Str(rule, "name")}' auto-answers a '{bad}' dialog — security/license/recovery/delete/overwrite dialogs are never answered automatically.", displayName));
            }
        }

        return issues;
    }

    private static IEnumerable<JsonElement> DialogRules(JsonElement spec, string scriptPath, string displayName, List<ScenarioIssue> issues)
    {
        if (!spec.TryGetProperty("dialogs", out var d))
        {
            yield break;
        }

        if (d.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in d.EnumerateArray())
            {
                yield return r;
            }

            yield break;
        }

        if (d.ValueKind != JsonValueKind.String)
        {
            yield break;
        }

        foreach (var part in (d.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = Path.IsPathRooted(part) ? part : Path.Combine(Path.GetDirectoryName(scriptPath)!, part);
            if (!File.Exists(p))
            {
                issues.Add(ScenarioIssue.Error("ui-dialogs", $"dialogs file not found: '{part}'.", displayName));
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p, Encoding.UTF8));
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                issues.Add(ScenarioIssue.Error("ui-dialogs", $"dialogs file '{part}' is not valid JSON.", displayName));
                continue;
            }

            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };
            foreach (var r in items)
            {
                yield return r;
            }
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;
}
