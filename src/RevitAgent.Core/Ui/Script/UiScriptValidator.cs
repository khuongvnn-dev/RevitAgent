using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Ui.Script;

/// <summary>One problem found in a script. <see cref="Forbidden"/> = safety rule (PERMISSION_DENIED), else format error.</summary>
public sealed record UiScriptIssue(string Message, bool Forbidden);

/// <summary>Validation result: errors reject the script, warnings are reported.</summary>
public sealed class UiScriptValidation
{
    public List<UiScriptIssue> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<DialogRule> Rules { get; } = new();

    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Static validation of a UI script:
/// no <c>launch</c>/<c>run</c>, no <c>close</c> or Alt+F4 without a window, no typing into password/token fields, no
/// dialog rule that answers security / unsigned / license / recovery / delete / overwrite / replace dialogs.
/// </summary>
public static class UiScriptValidator
{
    /// <summary>Commands valid in a script, plus sleep.</summary>
    public static readonly string[] KnownVerbs =
    {
        "windows", "use", "snapshot", "find", "inspect", "text", "point", "screenshot", "click", "dblclick", "rightclick",
        "hover", "type", "keys", "focus", "toggle", "expand", "collapse", "select", "scroll", "drag", "close", "wait", "expect", "table",
        "dialogs", "sleep", "launch", "run",
    };

    /// <summary>Banned in a UI step: the host app is started/stopped by the runner, and scripts do not nest.</summary>
    public static readonly string[] BannedVerbs = { "launch", "run" };

    /// <summary>Dialog titles/texts never answered automatically.</summary>
    public static readonly string[] NeverAutoAnswer =
    {
        "security", "unsigned", "bao mat", "bảo mật", "licens", "recover", "khôi phục", "delete", "overwrite", "replace",
        "xóa", "xoá", "ghi đè", "ghi de",
    };

    /// <summary>Application names that match every dialog of the host: not a specific enough rule title.</summary>
    private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "autodesk revit", "revit", "autocad", "autodesk autocad", "autodesk",
    };

    private static readonly Regex SecretField = new("(?i)pass(word)?|mat ?khau|mật ?khẩu|token|secret", RegexOptions.Compiled);
    private static readonly Regex SaveOrOpen = new("\\^\\+?[sSoO]", RegexOptions.Compiled);

    public static UiScriptValidation Validate(UiScript script)
    {
        var v = new UiScriptValidation();
        if (script.Steps.Count == 0)
        {
            v.Errors.Add(new UiScriptIssue("the script has no steps", false));
        }

        if (script.Window is { Length: > 0 } win && !Regex.IsMatch(win, "^pid:\\$\\{env:REVITAGENT_UI_HOST_PID\\}$"))
        {
            v.Warnings.Add($"window '{win}' — leave it empty to start at the main window of the target pid");
        }

        var shots = UiScript.Str(script.Root, "screenshots");
        if (shots is { Length: > 0 } && shots.ToLowerInvariant() is not ("fail" or "each" or "none"))
        {
            v.Errors.Add(new UiScriptIssue($"screenshots '{shots}' — only fail | each | none", false));
        }

        foreach (var st in script.Steps)
        {
            var n = st.Number;
            var verb = st.Do;
            var w = st.Raw("window");
            if (verb.Length == 0)
            {
                v.Errors.Add(new UiScriptIssue($"steps[{n}] has no 'do'", false));
                continue;
            }

            var role = st.Raw("role");
            if (role is { Length: > 0 } && role is not ("nav" or "check"))
            {
                v.Errors.Add(new UiScriptIssue($"steps[{n}] role '{role}' — only nav (navigating the host app UI) | check (acting/checking on an add-in window)", false));
            }

            if (!KnownVerbs.Contains(verb))
            {
                v.Errors.Add(new UiScriptIssue($"steps[{n}] do '{verb}' is not a UI script command ({string.Join(", ", KnownVerbs.Except(BannedVerbs))})", false));
                continue;
            }

            if (BannedVerbs.Contains(verb))
            {
                v.Errors.Add(new UiScriptIssue($"steps[{n}] do '{verb}' is forbidden in a UI script — the host app is launched by the runner (launch) and scripts do not nest (run)", true));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(w) && RevitAgent.Core.Revit.DialogSafety.FindForbiddenCategory(w) is { } wcat)
            {
                v.Errors.Add(new UiScriptIssue($"steps[{n}] window '{w}' is a '{wcat}' dialog - security / license / sign-in / recovery / delete / overwrite / replace dialogs are never acted on by a script; a human must answer them", true));
            }

            if (verb == "close" && string.IsNullOrWhiteSpace(w))
            {
                v.Errors.Add(new UiScriptIssue($"steps[{n}] close without 'window' would close the host application's main window — set window = the dialog title to close", true));
            }

            if (verb == "keys")
            {
                var k = st.Raw("text") is { Length: > 0 } t ? t : st.Raw("target") ?? string.Empty;
                if (k.Contains("%{F4}", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(w))
                {
                    v.Errors.Add(new UiScriptIssue($"steps[{n}] keys Alt+F4 without 'window' would close the host application", true));
                }

                if (SaveOrOpen.IsMatch(k))
                {
                    v.Warnings.Add($"steps[{n}] keys '{k}' (Ctrl+S / Ctrl+O) — only save/open copies inside the run folder");
                }
            }

            if (verb == "type")
            {
                var t = (st.Raw("target") ?? string.Empty) + " " + (st.Raw("name") ?? string.Empty);
                if (SecretField.IsMatch(t))
                {
                    v.Errors.Add(new UiScriptIssue($"steps[{n}] type into '{t.Trim()}' — scripts must not type passwords / secrets", true));
                }
            }
        }

        v.Rules.AddRange(LoadRules(script, v));
        foreach (var r in v.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Title) || (GenericTitles.Contains(Regex.Replace(r.Title.Trim(), @"\s*(19|20)\d\d$", string.Empty)) && string.IsNullOrWhiteSpace(r.Text)))
            {
                v.Errors.Add(new UiScriptIssue($"dialogs: rule '{r.Name ?? r.Title}' has {(string.IsNullOrWhiteSpace(r.Title) ? "no title" : $"only the generic title '{r.Title}'")} - it would match any dialog; give a specific title or a 'text' pattern", true));
            }

            var cat = RevitAgent.Core.Revit.DialogSafety.FindForbiddenCategory(r.Title + " " + r.Text);
            var bad = ForbiddenWord(r.Title + " " + r.Text) ?? cat;
            if (bad is not null)
            {
                v.Errors.Add(new UiScriptIssue($"dialogs: rule '{r.Name ?? r.Title}' would auto-answer a '{bad}' dialog — security / license / recovery / delete / overwrite dialogs are never answered automatically", true));
            }
        }

        return v;
    }

    /// <summary>The first never-auto-answer word contained in <paramref name="text"/> (case-insensitive), or null.</summary>
    public static string? ForbiddenWord(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lower = text.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
        return NeverAutoAnswer.FirstOrDefault(b => lower.Contains(b, StringComparison.Ordinal));
    }

    private static IEnumerable<DialogRule> LoadRules(UiScript script, UiScriptValidation v)
    {
        if (script.Dialogs is not { } d)
        {
            yield break;
        }

        if (d.ValueKind == JsonValueKind.String)
        {
            foreach (var part in (d.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var file = Path.IsPathRooted(part) ? part : Path.Combine(script.Directory ?? Environment.CurrentDirectory, part);
                if (!File.Exists(file))
                {
                    v.Errors.Add(new UiScriptIssue($"dialogs: rules file not found '{part}'", false));
                    continue;
                }

                JsonElement parsed;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    parsed = doc.RootElement.Clone();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    v.Errors.Add(new UiScriptIssue($"dialogs: '{part}' is not valid JSON ({ex.Message})", false));
                    continue;
                }

                if (UiScript.Get(parsed, "rules") is { } inner)
                {
                    parsed = inner;
                }

                foreach (var r in RulesOf(parsed))
                {
                    yield return r;
                }
            }
        }
        else
        {
            foreach (var r in RulesOf(d))
            {
                yield return r;
            }
        }
    }

    private static IEnumerable<DialogRule> RulesOf(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return DialogRule.From(item);
                }
            }
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            yield return DialogRule.From(e);
        }
    }
}

/// <summary>
/// UI script dialog rule: <c>{ name, title (contains), text (optional contains), proc (optional), click (selector) | keys (SendKeys), mouse }</c>.
/// </summary>
public sealed record DialogRule(string? Name, string? Title, string? Text, string? Proc, string? Click, string? Keys, bool Mouse)
{
    public static DialogRule From(JsonElement e) => new(
        UiScript.Str(e, "name"),
        UiScript.Str(e, "title"),
        UiScript.Str(e, "text"),
        UiScript.Str(e, "proc"),
        UiScript.Str(e, "click"),
        UiScript.Str(e, "keys"),
        UiScript.Bool(e, "mouse"));
}
