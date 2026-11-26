using System.Text;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Revit;

/// <summary>Result of the safety classification of one dialog.</summary>
/// <param name="ForbiddenTerm">Category of the first forbidden term found (null when none).</param>
/// <param name="IsSavePrompt">The dialog asks whether to save changes ("Save changes to X?").</param>
public sealed record DialogSafetyVerdict(string? ForbiddenTerm, bool IsSavePrompt)
{
    /// <summary>Never answered automatically (rules) and refused by revit_handle_dialog (except the save-prompt exception).</summary>
    public bool IsForbidden => ForbiddenTerm is not null || IsSavePrompt;

    /// <summary>
    /// The only exception to the forbidden list: a pure "save changes?" prompt (no other forbidden term) may be
    /// answered with the don't-save button when the agent explicitly passes <c>discard_changes: true</c>.
    /// </summary>
    public bool DiscardExceptionAllowed => IsSavePrompt && ForbiddenTerm is null;

    public string? Reason =>
        ForbiddenTerm is not null ? $"matches forbidden category '{ForbiddenTerm}'" :
        IsSavePrompt ? "save-changes prompt (only 'don't save' with discard_changes:true)" : null;
}

/// <summary>
/// Forbidden dialog categories:
/// security / unsigned / signature, license, sign-in, recovery, delete, overwrite, replace — English and
/// Vietnamese (with and without diacritics). Such dialogs are never answered by a rule, never by
/// revit_wait_ready / revit_launch, and revit_handle_dialog refuses them: a human must answer.
/// Matching is case-insensitive on title, texts and button labels, at word starts (so "dialog in" is not "log in").
/// A read dialog is classified with <see cref="ClassifyDialog"/>: only its enabled buttons count.
/// </summary>
public static class DialogSafety
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly (string Category, Regex Pattern)[] Forbidden =
    {
        ("security", Rx(@"\bsecurity|\bunsigned|\bsignature|\bdigitally\s+signed|\bpublisher|\bbảo\s+mật|\bbao\s+mat\b|\bchữ\s+k[ýí]|\bchu\s+ky\b")),
        ("license", Rx(@"\blicen[cs]|\bentitlement|\bgiấy\s+phép|\bgiay\s+phep\b|\bbản\s+quyền|\bban\s+quyen\b")),
        ("sign in", Rx(@"\bsign[\s-]?in\b|\blog[\s-]?in\b|\bđăng\s+nhập|\bdang\s+nhap\b")),
        ("recovery", Rx(@"\brecover|\bkhôi\s+phục|\bkhoi\s+phuc\b")),
        ("delete", Rx(@"\bdelet|\bxóa\b|\bxoá\b")),
        ("overwrite", Rx(@"\boverwrit|\bghi\s+đè|\bghi\s+de\b")),
        ("replace", Rx(@"\breplac|\bthay\s+thế|\bthay\s+the\b")),
    };

    private static readonly Regex SavePrompt = Rx(
        @"\bsave\s+(the\s+)?changes|\bwant\s+to\s+save|\blưu\s+(các\s+)?thay\s+đổi|\bluu\s+(cac\s+)?thay\s+doi\b|\bmuốn\s+lưu");

    private static readonly HashSet<string> DontSaveLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "don't save", "dont save", "do not save", "don’t save", "discard", "discard changes", "close without saving",
        "không", "khong", "không lưu", "khong luu",
    };

    /// <summary>The forbidden categories (for documentation and tool descriptions).</summary>
    public static IReadOnlyList<string> Categories { get; } = Forbidden.Select(f => f.Category).ToArray();

    /// <summary>
    /// Classifies a dialog that was read: its title, its texts and the labels of its ENABLED buttons. A disabled button cannot be
    /// pressed, so it does not make the dialog forbidden (Revit's Review Warnings shows a disabled "Delete Checked..." until a
    /// warning is checked; its "Close" must stay answerable). Enabled state comes from IsWindowEnabled (Win32) or UIA IsEnabled.
    /// </summary>
    public static DialogSafetyVerdict ClassifyDialog(string? title, DialogDetails? details) =>
        Classify(title, details?.Texts, details?.Buttons.Where(b => b.Enabled).Select(b => b.Text));

    /// <summary>Classifies a dialog from its title, texts and button labels.</summary>
    public static DialogSafetyVerdict Classify(string? title, IEnumerable<string>? texts, IEnumerable<string>? buttonLabels = null)
    {
        var parts = new List<string> { title ?? string.Empty };
        parts.AddRange(texts ?? Array.Empty<string>());
        parts.AddRange((buttonLabels ?? Array.Empty<string>()).Select(Win32Windows.StripMnemonic));
        var haystack = Normalize(string.Join("\n", parts));
        return new DialogSafetyVerdict(FindForbiddenCategory(haystack), SafeIsMatch(SavePrompt, haystack));
    }

    /// <summary>Category of the first forbidden term in <paramref name="text"/>, or null.</summary>
    public static string? FindForbiddenCategory(string? text)
    {
        var haystack = Normalize(text);
        foreach (var (category, pattern) in Forbidden)
        {
            if (SafeIsMatch(pattern, haystack))
            {
                return category;
            }
        }

        return null;
    }

    /// <summary>Every forbidden category found in <paramref name="text"/> (empty when none; a scan that times out reports "unscannable").</summary>
    public static IReadOnlyList<string> FindForbiddenCategories(string? text)
    {
        var haystack = Normalize(text);
        var found = new List<string>();
        foreach (var (category, pattern) in Forbidden)
        {
            try
            {
                if (pattern.IsMatch(haystack))
                {
                    found.Add(category);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                found.Add("unscannable");
            }
        }

        return found;
    }

    /// <summary>
    /// True for the "don't save" answer of a save prompt: label No / Don't save / Discard (EN, VI), or — when the
    /// label is empty — the standard IDNO id (7) of a message box / TaskDialog common button.
    /// </summary>
    public static bool IsDontSaveButton(DialogButton button)
    {
        var label = Win32Windows.StripMnemonic(button.Text).TrimEnd('.', '!', ':').Trim();
        if (label.Length > 0)
        {
            return DontSaveLabels.Contains(label.Normalize(NormalizationForm.FormC));
        }

        return button.Id == "7" || string.Equals(button.Id, "CommandButton_7", StringComparison.OrdinalIgnoreCase);
    }

    private static Regex Rx(string pattern) => new(pattern, Options, TimeSpan.FromMilliseconds(200));

    private static string Normalize(string? s) =>
        (s ?? string.Empty).Normalize(NormalizationForm.FormC).Replace(' ', ' ');

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: an input too complex to scan is treated as matching.
            return true;
        }
    }
}
