using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Revit;

/// <summary>
/// One informational-dialog rule: <c>{ "id", "description"?, "process"?, "titleContains", "bodyPattern"?, "press" }</c>.
/// When a dialog of process <c>process</c> has a title containing <c>titleContains</c> (case-insensitive) and, if given,
/// its texts match the <c>bodyPattern</c> regex, the gateway presses <c>press</c> (<c>id=CommandButton_8</c>, <c>id=6</c>,
/// <c>name=Close</c> or a bare label).
/// Rules can never answer forbidden dialogs: such rules are rejected at load and forbidden dialogs are skipped at run time.
/// </summary>
public sealed record DialogRule
{
    public string Id { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Process { get; init; }

    public string TitleContains { get; init; } = string.Empty;

    public string? BodyPattern { get; init; }

    public string Press { get; init; } = string.Empty;

    /// <summary>"builtin:builtin-dialog-rules.json" or the user rules file path.</summary>
    public string Source { get; init; } = string.Empty;

    [JsonIgnore]
    public Regex? BodyRegex { get; init; }

    public bool Matches(string? processName, string title, IReadOnlyList<string> texts)
    {
        if (!string.IsNullOrWhiteSpace(Process) && !string.Equals(StripExe(Process), StripExe(processName), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(title) || title.IndexOf(TitleContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (BodyRegex is null)
        {
            return true;
        }

        try
        {
            return BodyRegex.IsMatch(string.Join("\n", texts));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string StripExe(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }
}

/// <summary>Loaded rules plus the problems found while loading them (bad JSON, forbidden titles, missing fields...).</summary>
public sealed class DialogRuleSet
{
    public DialogRuleSet(IReadOnlyList<DialogRule> rules, IReadOnlyList<string> errors)
    {
        Rules = rules;
        Errors = errors;
    }

    public static DialogRuleSet Empty { get; } = new(Array.Empty<DialogRule>(), Array.Empty<string>());

    public IReadOnlyList<DialogRule> Rules { get; }

    public IReadOnlyList<string> Errors { get; }

    public DialogRule? Match(string? processName, string title, IReadOnlyList<string> texts) =>
        Rules.FirstOrDefault(r => r.Matches(processName, title, texts));

    public DialogRuleSet Merge(DialogRuleSet other) =>
        new(Rules.Concat(other.Rules).ToArray(), Errors.Concat(other.Errors).ToArray());

    /// <summary>Parses a rules document: a JSON array of rules, or <c>{ "rules": [...] }</c>.</summary>
    public static DialogRuleSet Parse(string json, string source)
    {
        var rules = new List<DialogRule>();
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return new DialogRuleSet(rules, new[] { $"{source}: invalid JSON ({ex.Message})" });
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var inner))
            {
                root = inner;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return new DialogRuleSet(rules, new[] { $"{source}: expected a JSON array of rules or {{\"rules\": [...]}}" });
            }

            var index = 0;
            foreach (var r in root.EnumerateArray())
            {
                index++;
                if (r.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{source}: rule #{index} is not an object");
                    continue;
                }

                var id = Str(r, "id") ?? $"rule #{index}";
                var title = Str(r, "titleContains");
                var press = Str(r, "press");
                var text = Str(r, "bodyPattern");
                var label = $"{source}: rule '{id}'";

                if (string.IsNullOrWhiteSpace(title))
                {
                    errors.Add($"{label} has no 'titleContains'");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(press))
                {
                    errors.Add($"{label} has no 'press' (the gateway only presses one button: id=... or name=...)");
                    continue;
                }

                if (DialogSafety.FindForbiddenCategory(title) is { } category)
                {
                    errors.Add($"{label} would auto-answer a forbidden dialog ('{category}': security / license / sign-in / recovery / delete / overwrite / replace) — rejected, a human must answer those");
                    continue;
                }

                if (DialogSafety.Classify(title, null).IsSavePrompt)
                {
                    errors.Add($"{label} would auto-answer a save-changes prompt — rejected (use revit_close with discard_changes:true)");
                    continue;
                }

                Regex? pattern = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        pattern = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add($"{label} has an invalid 'bodyPattern' regex ({ex.Message})");
                        continue;
                    }
                }

                rules.Add(new DialogRule
                {
                    Id = id,
                    Description = Str(r, "description"),
                    Process = Str(r, "process"),
                    TitleContains = title.Trim(),
                    BodyPattern = text,
                    Press = press.Trim(),
                    Source = source,
                    BodyRegex = pattern,
                });
            }
        }

        return new DialogRuleSet(rules, errors);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Supplies the dialog rules: built-in defaults (embedded <c>Revit/Rules/builtin-dialog-rules.json</c>) followed by the user's file <c>%REVITAGENT_HOME%\config\dialog-rules.json</c> (re-read when it changes).
/// </summary>
public sealed class DialogRuleProvider
{
    public const string UserFileName = "dialog-rules.json";
    public const string BuiltinResource = "RevitAgent.Core.Revit.Rules.builtin-dialog-rules.json";

    private readonly AgentPaths _paths;
    private readonly Lazy<DialogRuleSet> _builtin = new(LoadBuiltin);
    private readonly object _gate = new();
    private DateTime _userStamp;
    private DialogRuleSet _user = DialogRuleSet.Empty;

    public DialogRuleProvider(AgentPaths paths) => _paths = paths;

    public string UserRulesPath => Path.Combine(_paths.ConfigDir, UserFileName);

    public DialogRuleSet Builtin => _builtin.Value;

    public DialogRuleSet Load()
    {
        var path = UserRulesPath;
        lock (_gate)
        {
            DateTime stamp;
            try
            {
                stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (IOException)
            {
                stamp = DateTime.MinValue;
            }

            if (stamp != _userStamp)
            {
                _userStamp = stamp;
                _user = stamp == DateTime.MinValue ? DialogRuleSet.Empty : ReadUserFile(path);
            }

            return _builtin.Value.Merge(_user);
        }
    }

    private static DialogRuleSet ReadUserFile(string path)
    {
        try
        {
            return DialogRuleSet.Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DialogRuleSet(Array.Empty<DialogRule>(), new[] { $"{path}: cannot read ({ex.GetType().Name})" });
        }
    }

    private static DialogRuleSet LoadBuiltin()
    {
        using var stream = typeof(DialogRuleProvider).Assembly.GetManifestResourceStream(BuiltinResource);
        if (stream is null)
        {
            return new DialogRuleSet(Array.Empty<DialogRule>(), new[] { $"built-in rules resource '{BuiltinResource}' is missing" });
        }

        using var reader = new StreamReader(stream);
        return DialogRuleSet.Parse(reader.ReadToEnd(), "builtin:builtin-dialog-rules.json");
    }
}

/// <summary>Resolves a button reference (label, control id, TaskDialog id, AutomationId, or rule selector) on a dialog.</summary>
public static class ButtonMatcher
{
    /// <summary>
    /// Buttons matching <paramref name="query"/>: <c>id=X</c> matches ids only, <c>name=X</c>/<c>text=X</c> labels only;
    /// a bare value matches an id first, then a label (case-insensitive, &amp; mnemonics and trailing ':' '.' ignored).
    /// </summary>
    public static IReadOnlyList<DialogButton> Find(DialogDetails details, string query)
    {
        var q = (query ?? string.Empty).Trim();
        var mode = "any";
        if (q.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
        {
            mode = "id";
            q = q[3..].Trim();
        }
        else if (q.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
        {
            mode = "text";
            q = q[5..].Trim();
        }
        else if (q.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
        {
            mode = "text";
            q = q[5..].Trim();
        }

        q = q.Trim('"', '\'');
        if (q.Length == 0)
        {
            return Array.Empty<DialogButton>();
        }

        if (mode is "id" or "any")
        {
            var byId = details.Buttons.Where(b => b.Id is not null && string.Equals(b.Id, q, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length > 0 || mode == "id")
            {
                return byId;
            }
        }

        var label = NormalizeLabel(q);
        return details.Buttons.Where(b => NormalizeLabel(b.Text) == label).ToArray();
    }

    public static string NormalizeLabel(string? text) =>
        Win32Windows.StripMnemonic(text).Split('\n')[0].Trim().TrimEnd(':', '.', '…').Trim().ToLowerInvariant();
}
