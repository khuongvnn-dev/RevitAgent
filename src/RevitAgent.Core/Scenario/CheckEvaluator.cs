using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Scenario;

/// <summary>Run and check verdicts.</summary>
public static class Verdicts
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";

    /// <summary>The data a check reads is missing, unreadable or still a pending threshold. Never counted as PASS.</summary>
    public const string NotMeasurable = "NOT_MEASURABLE";

    public const string Suspect = "SUSPECT";
    public const string Error = "ERROR";
}

/// <summary>Allowed values of a check's <c>severity</c>.</summary>
public static class CheckSeverity
{
    /// <summary>A FAIL makes the run FAIL (default).</summary>
    public const string Fail = "fail";

    /// <summary>A FAIL only caps the run at SUSPECT.</summary>
    public const string Warn = "warn";

    public static bool IsValid(string? s) => s is Fail or Warn;
}

/// <summary>One entry of <c>checks:</c> in scenario.yaml.</summary>
public sealed class ScenarioCheck
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>The condition, in the expression language of <see cref="CheckExpression"/>.</summary>
    public required string Expect { get; init; }

    /// <summary><see cref="CheckSeverity.Fail"/> (default) or <see cref="CheckSeverity.Warn"/>.</summary>
    public string Severity { get; init; } = CheckSeverity.Fail;

    /// <summary>Free text: how the value is measured and why the threshold is what it is.</summary>
    public string? Note { get; init; }

    /// <summary>1-based line of the check in scenario.yaml (0 when unknown).</summary>
    public int Line { get; init; }

    public bool IsWarn => Severity == CheckSeverity.Warn;
}

/// <summary>One evaluated term of an expression (a comparison or a condition call).</summary>
public sealed record TermResult(string Text, string Verdict, string Detail);

public sealed class CheckEvaluation
{
    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Expect { get; init; } = string.Empty;

    public string Severity { get; init; } = CheckSeverity.Fail;

    public string? Note { get; init; }

    /// <summary>PASS, FAIL or NOT_MEASURABLE.</summary>
    public required string Verdict { get; init; }

    public IReadOnlyList<TermResult> Terms { get; init; } = Array.Empty<TermResult>();

    /// <summary>Data files the check read, relative to the run folder.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>SHA-256 of the check text and the content of the files it read (changes when either changes).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Terms' details joined, for compact output.</summary>
    public string Detail => string.Join("; ", Terms.Select(t => t.Detail));
}

/// <summary>Evaluates the <c>expect</c> expression of each check against the data a run wrote (<c>data\*.json</c>, <c>run.log</c>).</summary>
public sealed class CheckEvaluator
{
    private readonly string _runDir;
    private readonly string _dataDir;
    private readonly Dictionary<string, JsonElement> _json = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sha = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public CheckEvaluator(string runDir)
    {
        _runDir = Path.GetFullPath(runDir).TrimEnd('\\', '/');
        _dataDir = Path.Combine(_runDir, "data");
    }

    public IReadOnlyList<CheckEvaluation> EvaluateAll(IEnumerable<ScenarioCheck> checks) => checks.Select(Evaluate).ToArray();

    public CheckEvaluation Evaluate(ScenarioCheck check)
    {
        _files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<TermResult>();
        string verdict;
        try
        {
            var expr = CheckExpression.Parse(check.Expect);
            verdict = Eval(expr.Root, terms);
        }
        catch (CheckExpressionException ex)
        {
            verdict = Verdicts.NotMeasurable;
            terms.Add(new TermResult(check.Expect, verdict, "expression cannot be read: " + ex.Message));
        }

        return new CheckEvaluation
        {
            Id = check.Id,
            Title = check.Title,
            Expect = check.Expect,
            Severity = check.Severity,
            Note = check.Note,
            Verdict = verdict,
            Terms = terms,
            Files = _files.Select(Relative).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray(),
            Key = KeyOf(check),
        };
    }

    // ---- tree evaluation ---------------------------------------------------------------------

    /// <summary>Three-valued logic: PASS / FAIL / NOT_MEASURABLE. and: any FAIL wins, else any NOT_MEASURABLE; or: any PASS wins, else any NOT_MEASURABLE.</summary>
    private string Eval(CheckExpression.Node node, List<TermResult> terms)
    {
        switch (node)
        {
            case CheckExpression.Logic l:
            {
                var a = Eval(l.Left, terms);
                var b = Eval(l.Right, terms);
                if (l.IsAnd)
                {
                    return a == Verdicts.Fail || b == Verdicts.Fail ? Verdicts.Fail
                        : a == Verdicts.NotMeasurable || b == Verdicts.NotMeasurable ? Verdicts.NotMeasurable : Verdicts.Pass;
                }

                return a == Verdicts.Pass || b == Verdicts.Pass ? Verdicts.Pass
                    : a == Verdicts.NotMeasurable || b == Verdicts.NotMeasurable ? Verdicts.NotMeasurable : Verdicts.Fail;
            }

            case CheckExpression.Not n:
            {
                var v = Eval(n.Inner, terms);
                return v == Verdicts.Pass ? Verdicts.Fail : v == Verdicts.Fail ? Verdicts.Pass : v;
            }

            case CheckExpression.Literal { Value.Kind: CheckExpression.ValueKind.Pending } lit:
                return Term(terms, lit.Text, Verdicts.NotMeasurable, "threshold pending (TBD): a person has not approved it yet");
            case CheckExpression.Literal { Value.Kind: CheckExpression.ValueKind.Bool } lit:
                return Term(terms, lit.Text, lit.Value.Bool ? Verdicts.Pass : Verdicts.Fail, "constant");
            case CheckExpression.Call c:
                return Guard(terms, c.Text, () => EvalCondition(c));
            case CheckExpression.Compare cmp:
                return Guard(terms, cmp.Text, () => EvalCompare(cmp));
            default:
                return Term(terms, string.Empty, Verdicts.NotMeasurable, "unsupported expression");
        }
    }

    private static string Term(List<TermResult> terms, string text, string verdict, string detail)
    {
        terms.Add(new TermResult(text, verdict, detail));
        return verdict;
    }

    private static string Guard(List<TermResult> terms, string text, Func<(string Verdict, string Detail)> eval)
    {
        try
        {
            var (verdict, detail) = eval();
            return Term(terms, text, verdict, detail);
        }
        catch (MissingDataException ex)
        {
            return Term(terms, text, Verdicts.NotMeasurable, ex.Message);
        }
        catch (CheckExpressionException ex)
        {
            return Term(terms, text, Verdicts.NotMeasurable, ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return Term(terms, text, Verdicts.NotMeasurable, "regex timed out");
        }
    }

    private (string, string) EvalCondition(CheckExpression.Call c) => c.Name switch
    {
        "step_ok" => StepOk(Unquote(c.Args[0])),
        "has_line" => LogLine(c, want: true),
        "no_line" => LogLine(c, want: false),
        _ => throw new CheckExpressionException($"'{c.Name}' is not a condition"),
    };

    private (string, string) EvalCompare(CheckExpression.Compare cmp)
    {
        var left = EvalValue(cmp.Left);
        var right = EvalValue(cmp.Right);
        var tol = 0.0;
        if (cmp.Tolerance is { } t)
        {
            var tv = EvalValue(t);
            if (tv.Kind == CheckExpression.ValueKind.Pending)
            {
                return (Verdicts.NotMeasurable, "tolerance pending (TBD)");
            }

            tol = tv.AsNumber() ?? throw new CheckExpressionException("tolerance is not a number");
        }

        if (left.Kind == CheckExpression.ValueKind.Pending || right.Kind == CheckExpression.ValueKind.Pending)
        {
            return (Verdicts.NotMeasurable, "threshold pending (TBD): a person has not approved it yet");
        }

        var bothText = left.Kind == CheckExpression.ValueKind.Text && right.Kind == CheckExpression.ValueKind.Text;
        var a = bothText ? null : left.AsNumber();
        var b = bothText ? null : right.AsNumber();
        bool ok;
        if (a is not null && b is not null)
        {
            ok = cmp.Op switch
            {
                "==" => Math.Abs(a.Value - b.Value) <= tol + 1e-9,
                "!=" => Math.Abs(a.Value - b.Value) > tol + 1e-9,
                ">=" => a >= b,
                "<=" => a <= b,
                ">" => a > b,
                "<" => a < b,
                _ => throw new CheckExpressionException($"operator {cmp.Op}"),
            };
        }
        else if (cmp.Op is "==" or "!=")
        {
            var eq = string.Equals(left.AsText(), right.AsText(), StringComparison.Ordinal);
            ok = cmp.Op == "==" ? eq : !eq;
        }
        else
        {
            throw new CheckExpressionException($"'{left.AsText()}' {cmp.Op} '{right.AsText()}': ordering needs numbers");
        }

        var tolText = cmp.Tolerance is null ? string.Empty : $" +- {Num(tol)}";
        return (ok ? Verdicts.Pass : Verdicts.Fail, $"{Describe(cmp.Left, left)} {cmp.Op} {Describe(cmp.Right, right)}{tolText}");
    }

    private static string Describe(CheckExpression.Node node, CheckExpression.Value v) =>
        node is CheckExpression.Literal ? v.AsText() : $"{node.Text} = {v.AsText()}";

    private CheckExpression.Value EvalValue(CheckExpression.Node node)
    {
        switch (node)
        {
            case CheckExpression.Literal lit:
                return lit.Value;
            case CheckExpression.Call { Name: "count" } c:
                return CheckExpression.Value.Number(Select(c.Args[0]).Count);
            case CheckExpression.Call { Name: "value" } c:
            {
                var nodes = Select(c.Args[0]);
                if (nodes.Count != 1 || nodes[0].ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    throw new CheckExpressionException($"value({c.Args[0].Trim()}) selects {nodes.Count} items; it must select exactly one scalar");
                }

                var s = ToStr(nodes[0]);
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && nodes[0].ValueKind == JsonValueKind.Number
                    ? CheckExpression.Value.Number(d)
                    : CheckExpression.Value.Text(s);
            }

            default:
                throw new CheckExpressionException($"'{node.Text}' is not a value");
        }
    }

    // ---- conditions --------------------------------------------------------------------------

    private (string, string) LogLine(CheckExpression.Call c, bool want)
    {
        var name = Unquote(c.Args[0]);
        var file = FindFile(name) ?? throw new MissingDataException($"missing file {name}");
        _files.Add(file);
        var rx = new Regex(Unquote(c.Args[1]), RegexOptions.None, TimeSpan.FromSeconds(2));
        int n = 0, first = 0, i = 0;
        foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
        {
            i++;
            if (rx.IsMatch(line))
            {
                n++;
                if (first == 0)
                {
                    first = i;
                }
            }
        }

        var where = first > 0 ? $"{name}:{first}" : "-";
        var ok = want ? n > 0 : n == 0;
        return (ok ? Verdicts.Pass : Verdicts.Fail, $"{n} matching line(s) (first {where})");
    }

    private (string, string) StepOk(string code)
    {
        var file = FindFile("step_status.json") ?? throw new MissingDataException("missing data\\step_status.json");
        _files.Add(file);
        var root = ReadJson(file);
        var steps = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        if ((root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) || steps.Length == 0)
        {
            var msg = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) ? ToStr(m) : string.Empty;
            return (Verdicts.NotMeasurable, $"step status unreadable: {msg}");
        }

        var hit = steps.LastOrDefault(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("stepCode", out var sc) && ToStr(sc) == code);
        if (hit.ValueKind == JsonValueKind.Undefined)
        {
            return (Verdicts.Fail, $"step {code} did not run");
        }

        var status = hit.TryGetProperty("status", out var st) ? ToStr(st) : string.Empty;
        if (status == "BLOCKED")
        {
            return (Verdicts.NotMeasurable, $"step {code} is BLOCKED (a UI step could not reach the part under test)");
        }

        return (status == "SUCCEEDED" ? Verdicts.Pass : Verdicts.Fail, $"step {code} = {status}");
    }

    // ---- sources -----------------------------------------------------------------------------

    private List<JsonElement> Select(string arg)
    {
        var src = CheckExpression.ParseSource(arg);
        var file = FindFile(src.File) ?? throw new MissingDataException($"missing data file {src.File}");
        _files.Add(file);
        var root = ReadJson(file);
        if (src.Segments.Count == 0)
        {
            return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        }

        // An array root is flattened first, so its elements are what the first segment is looked up in.
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        foreach (var seg in src.Segments)
        {
            var next = new List<JsonElement>();
            var seen = false;
            foreach (var it in items)
            {
                if (!TryProperty(it, seg.Name, out var v))
                {
                    continue;
                }

                seen = true;
                if (seg.Brackets || v.ValueKind == JsonValueKind.Array)
                {
                    var elems = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : (IEnumerable<JsonElement>)new[] { v };
                    next.AddRange(elems.Where(e => Matches(e, seg.Conditions)));
                }
                else if (Matches(v, seg.Conditions))
                {
                    next.Add(v);
                }
            }

            // A misspelled field, at any depth, must not count as "0 found" (count(...) == 0 would PASS on a typo).
            // A field that exists but whose items are all filtered out is a real empty result.
            if (!seen && items.Count > 0)
            {
                throw new MissingDataException($"'{seg.Name}' is not in {Path.GetFileName(file)} (wrong field name?)");
            }

            items = next;
        }

        return items;
    }

    private static bool Matches(JsonElement node, IReadOnlyList<CheckExpression.Condition> conds)
    {
        foreach (var c in conds)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var v = TryProperty(node, c.Field, out var p) ? ToStr(p) : string.Empty;
            var ok = c.Op switch
            {
                "eq" => string.Equals(v, c.Value, StringComparison.Ordinal),
                "ne" => !string.Equals(v, c.Value, StringComparison.Ordinal),
                "starts" => v.StartsWith(c.Value, StringComparison.Ordinal),
                "contains" => v.Contains(c.Value, StringComparison.Ordinal),
                "notcontains" => !v.Contains(c.Value, StringComparison.Ordinal),
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Exact property name first, then case-insensitive.</summary>
    private static bool TryProperty(JsonElement node, string name, out JsonElement value)
    {
        value = default;
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (node.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var p in node.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        return false;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : s;
    }

    /// <summary>JSON value as text: numbers in shortest round-trip form (52347.80 becomes 52347.8), booleans lower-case.</summary>
    public static string ToStr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : v.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => v.GetRawText(),
    };

    private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    // ---- files -------------------------------------------------------------------------------

    private string? FindFile(string name)
    {
        var n = name.Trim().Replace('/', '\\');
        foreach (var dir in new[] { _dataDir, _runDir })
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(dir, n));
            }
            catch (ArgumentException)
            {
                return null;
            }

            if (!full.StartsWith(_runDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new CheckExpressionException($"file '{name}' is outside the run folder");
            }

            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private JsonElement ReadJson(string file)
    {
        if (_json.TryGetValue(file, out var cached))
        {
            return cached;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement.Clone();
            _json[file] = root;
            return root;
        }
        catch (JsonException ex)
        {
            throw new MissingDataException($"invalid JSON in {Relative(file)}: {ex.Message}");
        }
    }

    private string Relative(string full) =>
        full.StartsWith(_runDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full[(_runDir.Length + 1)..] : full;

    private string KeyOf(ScenarioCheck check)
    {
        var sb = new StringBuilder(check.Id).Append('\n').Append(check.Expect);
        foreach (var f in _files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!_sha.TryGetValue(f, out var h))
            {
                h = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
                _sha[f] = h;
            }

            sb.Append('\n').Append(Relative(f)).Append('=').Append(h);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private sealed class MissingDataException : Exception
    {
        public MissingDataException(string message)
            : base(message)
        {
        }
    }
}
