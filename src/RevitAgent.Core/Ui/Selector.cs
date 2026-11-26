using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui;

/// <summary>
/// Element selector.
/// <para>Grammar: <c>key=value</c> pairs joined by <c>;</c> (or by spaces when the next pair starts with a known key).
/// Keys: <c>id</c> (AutomationId, exact, case-sensitive), <c>name</c> (exact, case-insensitive, whitespace-normalized),
/// <c>type</c> (control type: Button, Edit, TabItem...), <c>class</c> (ClassName), <c>index</c> (0-based among matches),
/// <c>in</c> (search only inside a container: ref like e12 or a nested selector), <c>from=end</c> (reverse document order).
/// <c>~=</c> means "contains" (case-insensitive). A value written as <c>/regex/</c> is a regular expression.
/// Plain text without <c>key=</c> means "name contains". Aliases: automationid→id, controltype/role→type, text→name,
/// classname→class, nth→index.</para>
/// Examples: <c>id=btnOk</c>, <c>name="OK" type=Button</c>, <c>type=TabItem;name=Architecture</c>, <c>name~=Save;index=1</c>.
/// </summary>
public sealed class Selector
{
    private static readonly string[] KnownKeys =
    {
        "id", "name", "type", "class", "index", "in", "from", "automationid", "controltype", "role", "text", "classname", "nth",
    };

    private static readonly Regex StartsWithKey = new("^\\s*[a-zA-Z]+\\s*~?=", RegexOptions.Compiled);

    private Selector(string raw) => Raw = raw;

    public string Raw { get; }

    public string? Id { get; private set; }

    public string? IdContains { get; private set; }

    public Regex? IdRegex { get; private set; }

    public string? Name { get; private set; }

    public string? NameContains { get; private set; }

    public Regex? NameRegex { get; private set; }

    public string? Class { get; private set; }

    public string? ClassContains { get; private set; }

    public int? ControlType { get; private set; }

    public string? TypeName { get; private set; }

    public int Index { get; private set; }

    public bool HasIndex { get; private set; }

    /// <summary>Container: a ref (e12) or a nested selector string.</summary>
    public string? In { get; private set; }

    public bool FromEnd { get; private set; }

    /// <summary>True when the selector constrains anything (an empty selector would match every element).</summary>
    public bool HasCriteria =>
        Id is not null || IdContains is not null || IdRegex is not null || Name is not null || NameContains is not null ||
        NameRegex is not null || Class is not null || ClassContains is not null || ControlType is not null;

    public static bool LooksLikeRef(string? s) => s is not null && Regex.IsMatch(s.Trim(), "^e\\d+$");

    public static Selector Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw GatewayException.Validation("Empty selector. Examples: id=btnOk | name=\"OK\" type=Button | type=TabItem;name=Architecture | plain text = name contains.");
        }

        var raw = text.Trim();
        var sel = new Selector(raw);
        if (!StartsWithKey.IsMatch(raw))
        {
            sel.NameContains = UiText.Norm(Unquote(raw));
            return sel;
        }

        foreach (var (key, contains, value) in Tokenize(raw))
        {
            sel.Apply(key, contains, value);
        }

        if (!sel.HasCriteria && sel.In is null)
        {
            throw GatewayException.Validation($"Selector '{raw}' has no criteria (id, name, type or class).");
        }

        return sel;
    }

    /// <summary>Selector for a single exact name (used by ui_select item lookup).</summary>
    public static Selector ForName(string name, bool contains)
    {
        var s = new Selector(contains ? $"name~={name}" : $"name={name}");
        if (contains)
        {
            s.NameContains = UiText.Norm(name);
        }
        else
        {
            s.Name = UiText.Norm(name);
        }

        return s;
    }

    public bool Matches(string? automationId, string? name, string? className, int controlType)
    {
        var aid = automationId ?? string.Empty;
        if (Id is not null && !string.Equals(aid, Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (ControlType is { } ct && ct != controlType)
        {
            return false;
        }

        var cls = className ?? string.Empty;
        if (Class is not null && !string.Equals(cls, Class, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IdContains is not null && aid.IndexOf(IdContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (IdRegex is not null && !IdRegex.IsMatch(aid))
        {
            return false;
        }

        if (ClassContains is not null && cls.IndexOf(ClassContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (Name is not null || NameContains is not null || NameRegex is not null)
        {
            var nm = UiText.Norm(name);
            if (Name is not null && !string.Equals(nm, Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (NameContains is not null && nm.IndexOf(NameContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (NameRegex is not null && !NameRegex.IsMatch(nm))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a canonical selector string (used to report the resolved selector of an element).</summary>
    public static string Build(string? id, string? name, string? typeName, int index = 0)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(id))
        {
            parts.Add("id=" + QuoteIfNeeded(id));
        }
        else if (!string.IsNullOrEmpty(name))
        {
            parts.Add("name=" + QuoteIfNeeded(UiText.Norm(name)));
        }

        if (!string.IsNullOrEmpty(typeName) && (string.IsNullOrEmpty(id) || parts.Count == 0))
        {
            parts.Add("type=" + typeName);
        }

        if (index > 0)
        {
            parts.Add("index=" + index.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(";", parts);
    }

    public override string ToString() => Raw;

    private static string QuoteIfNeeded(string v) =>
        v.IndexOfAny(new[] { ';', '"', '\'' }) >= 0 || v != v.Trim() || v.Length == 0
            ? "\"" + v.Replace("\"", "\\\"") + "\""
            : v;

    private void Apply(string key, bool contains, string value)
    {
        key = key.ToLowerInvariant() switch
        {
            "automationid" => "id",
            "controltype" or "role" => "type",
            "text" => "name",
            "classname" => "class",
            "nth" => "index",
            var k => k,
        };

        bool isRegex = value.Length >= 2 && value.StartsWith('/') && value.EndsWith('/');
        switch (key)
        {
            case "id":
                if (isRegex)
                {
                    IdRegex = UiText.CompileRegex(value[1..^1], "id");
                }
                else if (contains)
                {
                    IdContains = value;
                }
                else
                {
                    Id = value;
                }

                break;
            case "name":
                if (isRegex)
                {
                    NameRegex = UiText.CompileRegex(value[1..^1], "name");
                }
                else if (contains)
                {
                    NameContains = UiText.Norm(value);
                }
                else
                {
                    Name = UiText.Norm(value);
                }

                break;
            case "class":
                if (contains)
                {
                    ClassContains = value;
                }
                else
                {
                    Class = value;
                }

                break;
            case "type":
                ControlType = ControlTypes.RequireId(value);
                TypeName = ControlTypes.Name(ControlType.Value);
                break;
            case "index":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx < 0)
                {
                    throw GatewayException.Validation($"Selector index must be a non-negative integer (got '{value}').");
                }

                Index = idx;
                HasIndex = true;
                break;
            case "in":
                In = value;
                break;
            case "from":
                FromEnd = string.Equals(value, "end", StringComparison.OrdinalIgnoreCase);
                break;
            default:
                throw GatewayException.Validation(
                    $"Unknown selector key '{key}' in '{Raw}'. Keys: id, name, type, class, index, in, from (use ~= for contains).");
        }
    }

    private static IEnumerable<(string Key, bool Contains, string Value)> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ';'))
            {
                i++;
            }

            if (i >= s.Length)
            {
                yield break;
            }

            int keyStart = i;
            while (i < s.Length && char.IsLetter(s[i]))
            {
                i++;
            }

            var key = s[keyStart..i];
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            bool contains = false;
            if (i < s.Length && s[i] == '~')
            {
                contains = true;
                i++;
            }

            if (key.Length == 0 || i >= s.Length || s[i] != '=')
            {
                throw GatewayException.Validation($"Cannot parse selector '{s}' near position {keyStart}. Use key=value pairs, e.g. id=btnOk;type=Button.");
            }

            i++;
            while (i < s.Length && char.IsWhiteSpace(s[i]) && s[i] != ';')
            {
                i++;
            }

            string value;
            if (i < s.Length && (s[i] == '"' || s[i] == '\''))
            {
                var q = s[i++];
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != q)
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == q)
                    {
                        i++;
                    }

                    sb.Append(s[i++]);
                }

                if (i < s.Length)
                {
                    i++;
                }

                value = sb.ToString();
            }
            else
            {
                int start = i;
                while (i < s.Length && s[i] != ';' && !NextPairStarts(s, i))
                {
                    i++;
                }

                value = s[start..i].Trim();
            }

            yield return (key, contains, value);
        }
    }

    /// <summary>True when position <paramref name="i"/> is whitespace followed by a known key and '=' / '~='.</summary>
    private static bool NextPairStarts(string s, int i)
    {
        if (!char.IsWhiteSpace(s[i]))
        {
            return false;
        }

        int j = i;
        while (j < s.Length && char.IsWhiteSpace(s[j]))
        {
            j++;
        }

        int k = j;
        while (k < s.Length && char.IsLetter(s[k]))
        {
            k++;
        }

        if (k == j)
        {
            return false;
        }

        var word = s[j..k];
        if (!KnownKeys.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        while (k < s.Length && char.IsWhiteSpace(s[k]))
        {
            k++;
        }

        if (k < s.Length && s[k] == '~')
        {
            k++;
        }

        return k < s.Length && s[k] == '=';
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')) ? s[1..^1] : s;
    }
}
