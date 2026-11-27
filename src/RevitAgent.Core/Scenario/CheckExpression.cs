using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Scenario;

/// <summary>An <c>expect</c> expression that cannot be parsed or evaluated.</summary>
public sealed class CheckExpressionException : Exception
{
    public CheckExpressionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Parsed form of a check's <c>expect</c> expression. Grammar (case-insensitive keywords):
/// <code>
/// expr      := term ( "or" term )*            // "and" binds tighter than "or"
/// term      := factor ( "and" factor )*
/// factor    := "not" factor | "(" expr ")" | comparison | condition | "true" | "false" | "TBD"
/// comparison:= value ( "==" | "!=" | "&lt;" | "&lt;=" | "&gt;" | "&gt;=" ) value [ "+-" value ]     // "+-" (tolerance) only after ==
/// value     := number | "text" | TBD | count(source) | value(source)
/// condition := step_ok(step_name) | has_line(file, "regex") | no_line(file, "regex")
/// source    := file -> field.field[Name="x", Other != "y", Name starts_with "z", Name contains "z", Name not_contains "z"]
/// </code>
/// <c>source</c> reads <c>data\&lt;file&gt;</c> of the run (or a file in the run folder, e.g. <c>run.log</c>); arrays are flattened at every step
/// and a quoted field (<c>"Room Name"</c>) allows spaces. <c>file.json</c> alone is the file's root.
/// TBD (or "pending") anywhere makes the check NOT_MEASURABLE: the threshold is not approved yet.
/// </summary>
public sealed class CheckExpression
{
    /// <summary>Longest accepted expression, in characters.</summary>
    public const int MaxLength = 4000;

    /// <summary>Deepest nesting of parentheses and <c>not</c> (deep recursion would be an uncatchable StackOverflow).</summary>
    public const int MaxNesting = 64;

    public static readonly IReadOnlyList<string> Functions = new[] { "count", "value", "step_ok", "has_line", "no_line" };

    private static readonly Regex NumberLiteral = new(@"^-?\d+(?:\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex Segment = new("^(?:\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_]*))(\\[(.*)\\])?$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Field = new("^(?:\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_]*))", RegexOptions.Compiled);

    private CheckExpression(Node root, string text)
    {
        Root = root;
        Text = text;
    }

    public Node Root { get; }

    public string Text { get; }

    public static CheckExpression Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CheckExpressionException("expect is empty");
        }

        if (text.Length > MaxLength)
        {
            throw new CheckExpressionException($"expect is too long ({text.Length} characters; the limit is {MaxLength})");
        }

        var parser = new Parser(text);
        var root = parser.ParseAll();
        return new CheckExpression(root, text);
    }

    /// <summary>Null when the expression is valid, else why it is not.</summary>
    public static string? SyntaxProblem(string? text)
    {
        try
        {
            _ = Parse(text ?? string.Empty);
            return null;
        }
        catch (CheckExpressionException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Step names used by <c>step_ok(...)</c> calls.</summary>
    public IEnumerable<string> ReferencedSteps() =>
        Calls(Root).Where(c => c.Name == "step_ok").Select(c => Unquote(c.Args[0]));

    /// <summary>Data file names used by sources and <c>has_line</c>/<c>no_line</c> calls.</summary>
    public IEnumerable<string> ReferencedFiles() =>
        Calls(Root).Select(c => c.Name switch
        {
            "count" or "value" => ParseSource(c.Args[0]).File,
            "has_line" or "no_line" => Unquote(c.Args[0]),
            _ => string.Empty,
        }).Where(f => f.Length > 0);

    private static IEnumerable<Call> Calls(Node n) => n switch
    {
        Call c => new[] { c },
        Compare cmp => Calls(cmp.Left).Concat(Calls(cmp.Right)).Concat(cmp.Tolerance is null ? Array.Empty<Call>() : Calls(cmp.Tolerance)),
        Logic l => Calls(l.Left).Concat(Calls(l.Right)),
        Not not => Calls(not.Inner),
        _ => Array.Empty<Call>(),
    };

    // ---- nodes -------------------------------------------------------------------------------

    public enum ValueKind
    {
        Number,
        Text,
        Bool,
        Pending,
    }

    public readonly record struct Value(ValueKind Kind, double Num, string Str, bool Bool)
    {
        public static Value Number(double d) => new(ValueKind.Number, d, string.Empty, false);

        public static Value Text(string s) => new(ValueKind.Text, 0, s, false);

        public static Value Boolean(bool b) => new(ValueKind.Bool, 0, string.Empty, b);

        public static Value Pending => new(ValueKind.Pending, 0, string.Empty, false);

        public double? AsNumber() => Kind switch
        {
            ValueKind.Number => Num,
            ValueKind.Text when NumberLiteral.IsMatch(Str.Trim()) => double.Parse(Str.Trim(), CultureInfo.InvariantCulture),
            _ => null,
        };

        public string AsText() => Kind switch
        {
            ValueKind.Number => Num.ToString("R", CultureInfo.InvariantCulture),
            ValueKind.Bool => Bool ? "true" : "false",
            ValueKind.Pending => "TBD",
            _ => Str,
        };
    }

    public abstract record Node
    {
        /// <summary>The source text of this node (used in reports).</summary>
        public string Text { get; init; } = string.Empty;
    }

    public sealed record Literal(Value Value) : Node;

    public sealed record Call(string Name, IReadOnlyList<string> Args) : Node;

    public sealed record Compare(Node Left, string Op, Node Right, Node? Tolerance) : Node;

    public sealed record Logic(bool IsAnd, Node Left, Node Right) : Node;

    public sealed record Not(Node Inner) : Node;

    public sealed record SourceSpec(string File, IReadOnlyList<PathSegment> Segments);

    public sealed record PathSegment(string Name, bool Brackets, IReadOnlyList<Condition> Conditions);

    public sealed record Condition(string Field, string Op, string Value);

    // ---- sources -----------------------------------------------------------------------------

    /// <summary>Parses <c>file -> a.b[Name="x"].c</c>.</summary>
    public static SourceSpec ParseSource(string arg)
    {
        var a = arg.Trim();
        var arrow = a.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            if (a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !a.Contains('['))
            {
                return new SourceSpec(Unquote(a), Array.Empty<PathSegment>());
            }

            throw new CheckExpressionException($"source '{a}' must be '<file> -> <path>' (for example levels.json -> elements[name=\"Roof\"])");
        }

        var file = Unquote(a[..arrow].Trim());
        var path = a[(arrow + 2)..].Trim();
        if (file.Length == 0)
        {
            throw new CheckExpressionException($"source '{a}' has no file before '->'");
        }

        if (path.Length == 0)
        {
            return new SourceSpec(file, Array.Empty<PathSegment>());
        }

        var segs = new List<PathSegment>();
        foreach (var raw in SplitTop(path, '.'))
        {
            var m = Segment.Match(raw);
            if (!m.Success)
            {
                throw new CheckExpressionException($"path segment '{raw}' is not a field name, \"quoted name\" or name[filter]");
            }

            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var conds = new List<Condition>();
            if (m.Groups[3].Success && m.Groups[4].Value.Trim().Length > 0)
            {
                foreach (var c in SplitTop(m.Groups[4].Value, ','))
                {
                    conds.Add(ParseCondition(c));
                }
            }

            segs.Add(new PathSegment(name, m.Groups[3].Success, conds));
        }

        return new SourceSpec(file, segs);
    }

    private static Condition ParseCondition(string c)
    {
        var f = Field.Match(c);
        if (!f.Success)
        {
            throw new CheckExpressionException($"filter '{c}' must start with a field name");
        }

        var field = f.Groups[1].Success ? f.Groups[1].Value : f.Groups[2].Value;
        var rest = c[f.Length..].Trim();
        foreach (var (word, op) in new[]
                 {
                     ("!=", "ne"), ("==", "eq"), ("=", "eq"),
                     ("starts_with", "starts"), ("not_contains", "notcontains"), ("contains", "contains"),
                 })
        {
            if (rest.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                var value = rest[word.Length..].Trim();
                if (op is not ("eq" or "ne") && value.Length == 0)
                {
                    break;
                }

                return new Condition(field, op, Unquote(value));
            }
        }

        throw new CheckExpressionException($"filter '{c}': use Field = \"x\", Field != \"x\", Field starts_with \"x\", Field contains \"x\" or Field not_contains \"x\"");
    }

    // ---- lexing helpers ----------------------------------------------------------------------

    /// <summary>Splits on <paramref name="sep"/> outside quotes and brackets.</summary>
    internal static List<string> SplitTop(string s, char sep)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var depth = 0;
        var inQuote = false;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inQuote && ch == '\\' && i + 1 < s.Length && s[i + 1] == '"')
            {
                sb.Append(ch).Append(s[++i]);
                continue;
            }

            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote)
            {
                if (ch is '(' or '[')
                {
                    depth++;
                }
                else if (ch is ')' or ']')
                {
                    depth--;
                }
                else if (ch == sep && depth == 0)
                {
                    parts.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
            }

            sb.Append(ch);
        }

        parts.Add(sb.ToString().Trim());
        return parts;
    }

    internal static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : s;
    }

    private enum Tok
    {
        Number,
        String,
        Op,
        Tol,
        LParen,
        RParen,
        Word,
        Call,
        End,
    }

    private sealed record Token(Tok Kind, string Text, int Start, int End, IReadOnlyList<string>? Args = null);

    private sealed class Parser
    {
        private static readonly Regex Ident = new(@"\G[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
        private static readonly Regex Num = new(@"\G-?\d+(?:\.\d+)?", RegexOptions.Compiled);

        private readonly string _src;
        private readonly List<Token> _tokens;
        private int _pos;
        private int _depth;

        public Parser(string src)
        {
            _src = src;
            _tokens = Lex(src);
        }

        public Node ParseAll()
        {
            var n = ParseOr();
            if (Peek.Kind != Tok.End)
            {
                throw Fail($"unexpected '{Peek.Text}'");
            }

            return n;
        }

        private Token Peek => _tokens[_pos];

        private Token Next() => _tokens[_pos++];

        private bool IsWord(string w) => Peek.Kind == Tok.Word && Peek.Text.Equals(w, StringComparison.OrdinalIgnoreCase);

        private CheckExpressionException Fail(string message) => new($"{message} (at position {Peek.Start + 1} of '{_src}')");

        private string Slice(int start, int end) => _src[start..end].Trim();

        private Node ParseOr()
        {
            var start = Peek.Start;
            var left = ParseAnd();
            while (IsWord("or"))
            {
                Next();
                var right = ParseAnd();
                left = new Logic(false, left, right) { Text = Slice(start, _tokens[_pos - 1].End) };
            }

            return left;
        }

        private Node ParseAnd()
        {
            var start = Peek.Start;
            var left = ParseFactor();
            while (IsWord("and"))
            {
                Next();
                var right = ParseFactor();
                left = new Logic(true, left, right) { Text = Slice(start, _tokens[_pos - 1].End) };
            }

            return left;
        }

        private Node ParseFactor()
        {
            if (++_depth > MaxNesting)
            {
                throw Fail($"expression is nested too deeply (limit {MaxNesting} levels of parentheses / not)");
            }

            try
            {
                return ParseFactorCore();
            }
            finally
            {
                _depth--;
            }
        }

        private Node ParseFactorCore()
        {
            var start = Peek.Start;
            if (IsWord("not"))
            {
                Next();
                var inner = ParseFactor();
                return new Not(inner) { Text = Slice(start, _tokens[_pos - 1].End) };
            }

            if (Peek.Kind == Tok.LParen)
            {
                Next();
                var inner = ParseOr();
                if (Peek.Kind != Tok.RParen)
                {
                    throw Fail("missing ')'");
                }

                Next();
                return inner;
            }

            var first = ParseOperand();
            if (Peek.Kind == Tok.Op)
            {
                var op = Next().Text;
                RequireValue(first);
                var second = ParseOperand();
                RequireValue(second);
                Node? tol = null;
                if (Peek.Kind == Tok.Tol)
                {
                    if (op != "==")
                    {
                        throw Fail("'+-' (tolerance) is only allowed after '=='");
                    }

                    Next();
                    tol = ParseOperand();
                    RequireValue(tol);
                }

                return new Compare(first, op, second, tol) { Text = Slice(start, _tokens[_pos - 1].End) };
            }

            // A lone operand must itself be a condition.
            return first switch
            {
                Call { Name: "step_ok" or "has_line" or "no_line" } => first,
                Literal { Value.Kind: ValueKind.Bool or ValueKind.Pending } => first,
                _ => throw Fail($"'{first.Text}' is a value, not a condition: compare it, e.g. {first.Text} == 1"),
            };
        }

        private void RequireValue(Node n)
        {
            var ok = n is Literal { Value.Kind: ValueKind.Number or ValueKind.Text or ValueKind.Pending } or Call { Name: "count" or "value" };
            if (!ok)
            {
                throw Fail($"'{n.Text}' cannot be compared (use numbers, \"text\", TBD, count(...) or value(...))");
            }
        }

        private Node ParseOperand()
        {
            var t = Peek;
            switch (t.Kind)
            {
                case Tok.Number:
                    Next();
                    return new Literal(Value.Number(double.Parse(t.Text, CultureInfo.InvariantCulture))) { Text = t.Text };
                case Tok.String:
                    Next();
                    return new Literal(Value.Text(Unquote(t.Text))) { Text = t.Text };
                case Tok.Call:
                    Next();
                    return BuildCall(t);
                case Tok.Word:
                    Next();
                    return t.Text.ToLowerInvariant() switch
                    {
                        "true" => new Literal(Value.Boolean(true)) { Text = t.Text },
                        "false" => new Literal(Value.Boolean(false)) { Text = t.Text },
                        "tbd" or "pending" => new Literal(Value.Pending) { Text = t.Text },
                        _ => throw new CheckExpressionException($"unexpected word '{t.Text}' (at position {t.Start + 1}); text values go in double quotes"),
                    };
                default:
                    throw Fail(t.Kind == Tok.End ? "the expression ends too early" : $"unexpected '{t.Text}'");
            }
        }

        private Node BuildCall(Token t)
        {
            var name = t.Text;
            var args = t.Args!;
            var text = Slice(t.Start, t.End);
            if (!Functions.Contains(name))
            {
                throw new CheckExpressionException($"unknown function '{name}' (functions: {string.Join(", ", Functions)})");
            }

            switch (name)
            {
                case "count":
                case "value":
                    if (args.Count != 1)
                    {
                        throw new CheckExpressionException($"{name}() takes 1 argument: <file> -> <path>");
                    }

                    _ = ParseSource(args[0]);
                    break;
                case "step_ok":
                    if (args.Count != 1 || Unquote(args[0]).Length == 0)
                    {
                        throw new CheckExpressionException("step_ok() takes 1 argument: the step name");
                    }

                    break;
                default:
                    if (args.Count != 2)
                    {
                        throw new CheckExpressionException($"{name}() takes 2 arguments: file, \"regex\"");
                    }

                    try
                    {
                        _ = new Regex(Unquote(args[1]));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new CheckExpressionException("invalid regex: " + ex.Message);
                    }

                    break;
            }

            return new Call(name, args) { Text = text };
        }

        private static List<Token> Lex(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var ch = s[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }

                if (ch == '"')
                {
                    var j = i + 1;
                    while (j < s.Length && !(s[j] == '"' && s[j - 1] != '\\'))
                    {
                        j++;
                    }

                    if (j >= s.Length)
                    {
                        throw new CheckExpressionException($"unterminated string starting at position {i + 1}");
                    }

                    tokens.Add(new Token(Tok.String, s[i..(j + 1)], i, j + 1));
                    i = j + 1;
                    continue;
                }

                if (ch == '(')
                {
                    tokens.Add(new Token(Tok.LParen, "(", i, i + 1));
                    i++;
                    continue;
                }

                if (ch == ')')
                {
                    tokens.Add(new Token(Tok.RParen, ")", i, i + 1));
                    i++;
                    continue;
                }

                if (ch == '+' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    tokens.Add(new Token(Tok.Tol, "+-", i, i + 2));
                    i += 2;
                    continue;
                }

                var two = i + 1 < s.Length ? s.Substring(i, 2) : string.Empty;
                if (two is "==" or "!=" or "<=" or ">=")
                {
                    tokens.Add(new Token(Tok.Op, two, i, i + 2));
                    i += 2;
                    continue;
                }

                if (ch is '<' or '>')
                {
                    tokens.Add(new Token(Tok.Op, ch.ToString(), i, i + 1));
                    i++;
                    continue;
                }

                var nm = Num.Match(s, i);
                if (nm.Success)
                {
                    tokens.Add(new Token(Tok.Number, nm.Value, i, i + nm.Length));
                    i += nm.Length;
                    continue;
                }

                var id = Ident.Match(s, i);
                if (id.Success)
                {
                    var k = i + id.Length;
                    var p = k;
                    while (p < s.Length && char.IsWhiteSpace(s[p]))
                    {
                        p++;
                    }

                    if (p < s.Length && s[p] == '(' && id.Value is not ("and" or "or" or "not"))
                    {
                        var close = FindClose(s, p);
                        var args = SplitTop(s[(p + 1)..close], ',');
                        if (args.Count == 1 && args[0].Length == 0)
                        {
                            args.Clear();
                        }

                        tokens.Add(new Token(Tok.Call, id.Value, i, close + 1, args));
                        i = close + 1;
                        continue;
                    }

                    tokens.Add(new Token(Tok.Word, id.Value, i, k));
                    i = k;
                    continue;
                }

                throw new CheckExpressionException($"unexpected character '{ch}' at position {i + 1}");
            }

            tokens.Add(new Token(Tok.End, string.Empty, s.Length, s.Length));
            return tokens;
        }

        private static int FindClose(string s, int open)
        {
            var depth = 0;
            var inQuote = false;
            for (var i = open; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '"' && (i == 0 || s[i - 1] != '\\'))
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (inQuote)
                {
                    continue;
                }

                if (ch is '(' or '[')
                {
                    depth++;
                }
                else if (ch is ')' or ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (ch != ')')
                        {
                            break;
                        }

                        return i;
                    }
                }
            }

            throw new CheckExpressionException($"missing ')' for the call at position {open + 1}");
        }
    }
}
