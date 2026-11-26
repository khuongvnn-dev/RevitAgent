using System.Text;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Revit;

/// <summary>
/// Pure logic of the add-in allowlist: recognizes and parses the Revit "Security - Unsigned Add-In" prompt, matches
/// path globs and decides. No I/O, so it is unit-testable; fail closed everywhere (any doubt = NoDecision / no match).
/// </summary>
public static class AddinAllowlistPolicy
{
    public const string DialogTitle = "Security - Unsigned Add-In";
    public const string AlwaysLoadButtonId = "CommandButton_1001";
    public const string LoadOnceButtonId = "CommandButton_1002";
    public const string DoNotLoadButtonId = "CommandButton_1003";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex NameLine = new(@"^[ \t]*Name[ \t]*:[ \t]*(?<v>\S.*?)[ \t\r]*$", Options, RegexTimeout);
    private static readonly Regex LocationLine = new(@"^[ \t]*Location[ \t]*:[ \t]*(?<v>\S.*?)[ \t\r]*$", Options, RegexTimeout);
    private static readonly Regex RootedPattern = new(@"^(?:[A-Za-z]:\\|\\\\[^\\*?]+\\[^\\*?]+\\)", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>The window title of Revit's unsigned add-in prompt.</summary>
    public static bool IsUnsignedAddinTitle(string? title) =>
        string.Equals((title ?? string.Empty).Trim(), DialogTitle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts <c>Name:</c> and <c>Location:</c>. Null when a line is missing or ambiguous (two different values).</summary>
    public static (string Name, string Location)? ParseDialogText(IEnumerable<string>? texts)
    {
        if (texts is null)
        {
            return null;
        }

        try
        {
            var text = string.Join("\n", texts).Replace("\r\n", "\n").Replace('\r', '\n');
            var name = Single(NameLine, text);
            var location = Single(LocationLine, text);
            return name is null || location is null ? null : (name, location);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string? Single(Regex rx, string text)
    {
        var values = rx.Matches(text).Select(m => m.Groups["v"].Value.Trim()).Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>Normalizes a path for matching: forward slashes, <c>.</c> / <c>..</c> resolved, trailing separators trimmed. Null when invalid.</summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var p = path.Trim().Trim('"').Replace('/', '\\');
            if (p.IndexOfAny(new[] { '*', '?', '<', '>', '|', '\0' }) >= 0 || !Path.IsPathFullyQualified(p))
            {
                return null;
            }

            return Path.GetFullPath(p).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>Why <paramref name="pattern"/> is not acceptable (null = fine): it must start with a drive or UNC share and end in a file name.</summary>
    public static string? ValidatePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "pattern is empty";
        }

        var p = pattern.Trim().Replace('/', '\\');
        if (!RootedPattern.IsMatch(p))
        {
            return "pattern must start with a drive (D:\\...) or a UNC share (\\\\server\\share\\...) without wildcards in that part";
        }

        if (p.Split('\\').Any(s => s == ".."))
        {
            return "pattern must not contain '..' segments";
        }

        var fileName = p[(p.LastIndexOf('\\') + 1)..];
        if (fileName.Length == 0 || fileName.All(c => c is '*' or '?'))
        {
            return "pattern must end with a file name (e.g. MyAddin.dll), not only wildcards";
        }

        return null;
    }

    /// <summary>Glob match of a full path against a pattern: case-insensitive, <c>*</c> not across <c>\</c>, <c>**</c> across it, <c>?</c> one char.</summary>
    public static bool MatchesPattern(string pattern, string location)
    {
        if (ValidatePattern(pattern) is not null || NormalizePath(location) is not { } path)
        {
            return false;
        }

        var sb = new StringBuilder("^");
        var p = pattern.Trim().Replace('/', '\\');
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '*')
            {
                if (i + 1 < p.Length && p[i + 1] == '*')
                {
                    sb.Append(".*");
                    while (i + 1 < p.Length && p[i + 1] == '*')
                    {
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^\\\\]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^\\\\]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        try
        {
            return Regex.IsMatch(path, sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>First enabled entry whose name equals <paramref name="name"/> and whose pattern matches <paramref name="location"/>.</summary>
    public static AddinAllowlistEntry? FindMatch(IEnumerable<AddinAllowlistEntry> entries, string name, string location) =>
        entries.FirstOrDefault(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Name) &&
                                    string.Equals(e.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                    MatchesPattern(e.PathPattern, location));

    /// <summary>
    /// Allowlist decision for the dialog text. NoDecision when name/location cannot be parsed or the location is not a
    /// usable full path or the allowlist file is unreadable; LoadOnce on a match; DoNotLoad otherwise.
    /// </summary>
    public static AddinTrustDecision Decide(IEnumerable<string>? dialogTexts, AddinAllowlistSnapshot allowlist)
    {
        if (ParseDialogText(dialogTexts) is not { } parsed)
        {
            return new AddinTrustDecision(AddinTrustAction.NoDecision, null, null, null, "Name/Location could not be read from the dialog.");
        }

        if (NormalizePath(parsed.Location) is not { } location)
        {
            return new AddinTrustDecision(AddinTrustAction.NoDecision, parsed.Name, parsed.Location, null, "Location is not a usable full path.");
        }

        if (allowlist.Error is not null)
        {
            return new AddinTrustDecision(AddinTrustAction.NoDecision, parsed.Name, location, null, "Allowlist unreadable: " + allowlist.Error);
        }

        var match = FindMatch(allowlist.Entries, parsed.Name, location);
        return match is not null
            ? new AddinTrustDecision(AddinTrustAction.LoadOnce, parsed.Name, location, match.Id, $"Matches allowlist entry '{match.Name}' ({match.PathPattern}).")
            : new AddinTrustDecision(AddinTrustAction.DoNotLoad, parsed.Name, location, null, "Not on the allowlist.");
    }
}
