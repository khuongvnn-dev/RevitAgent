using System.Text;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Revit;

/// <summary>Result of the safety classification of one dialog.</summary>
/// <param name="ForbiddenTerm">Category of the first forbidden term found (null when none).</param>
/// <param name="IsSavePrompt">The dialog asks whether to save changes ("Save changes to X?").</param>
public sealed record DialogSafetyVerdict(string? ForbiddenTerm, bool IsSavePrompt)
{
    /// <summary>Category of a dialog whose UI language is neither English nor Vietnamese: its forbidden terms cannot be checked.</summary>
    public const string UnrecognizedLanguageCategory = "unrecognized_language";

    /// <summary>The verdict for a dialog in an unrecognized UI language (forbidden: a human answers it).</summary>
    public static DialogSafetyVerdict UnrecognizedLanguage { get; } = new(UnrecognizedLanguageCategory, false);

    /// <summary>Never answered automatically (rules) and refused by revit_handle_dialog (except the save-prompt exception).</summary>
    public bool IsForbidden => ForbiddenTerm is not null || IsSavePrompt;

    /// <summary>Forbidden only because its language is not recognised (see <see cref="DialogSafety.IsRecognizedLanguage"/>).</summary>
    public bool IsUnrecognizedLanguage => ForbiddenTerm == UnrecognizedLanguageCategory;

    /// <summary>
    /// The only exception to the forbidden list: a pure "save changes?" prompt (no other forbidden term) may be
    /// answered with the don't-save button when the agent explicitly passes <c>discard_changes: true</c>.
    /// </summary>
    public bool DiscardExceptionAllowed => IsSavePrompt && ForbiddenTerm is null;

    public string? Reason =>
        IsUnrecognizedLanguage ? "is in an unrecognized UI language (only English and Vietnamese dialogs can be checked)" :
        ForbiddenTerm is not null ? $"matches forbidden category '{ForbiddenTerm}'" :
        IsSavePrompt ? "save-changes prompt (only 'don't save' with discard_changes:true)" : null;
}

/// <summary>
/// Forbidden dialog categories:
/// security / unsigned / signature, license, sign-in, recovery, delete, overwrite, replace — English and
/// Vietnamese (with and without diacritics). Such dialogs are never answered by a rule, never by
/// revit_wait_ready / revit_launch, and revit_handle_dialog refuses them: a human must answer.
/// Matching is case-insensitive on title, texts and button labels, at word starts (so "dialog in" is not "log in").
/// A read dialog is classified with <see cref="ClassifyDialog"/>: only its enabled buttons count, and a dialog that is not
/// recognisably English or Vietnamese is forbidden too (<see cref="DialogSafetyVerdict.UnrecognizedLanguageCategory"/>):
/// dialogs in other Revit UI languages are left for a human.
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

    /// <summary>
    /// Button labels that prove an English or Vietnamese UI (exact, normalized first line). Reuses the safe-button,
    /// safe-dismiss, don't-save and add-in prompt labels, plus common dialog verbs.
    /// </summary>
    private static readonly HashSet<string> LanguageLabels = new(StringComparer.Ordinal)
    {
        "cancel", "close", "yes", "yes to all", "no to all", "later", "remind me later", "not now",
        "always load", "load once", "do not load", "don't load", "dont load",
        "save", "don't save", "dont save", "do not save", "discard", "discard changes", "close without saving",
        "retry", "try again", "ignore", "abort", "continue", "help", "apply", "next", "back", "finish", "done",
        "accept", "decline", "agree", "i agree", "skip", "open", "browse", "more info", "show", "expand", "collapse",
        "show details", "hide details", "print", "delete", "delete checked", "remove", "overwrite", "replace", "keep",
        "run", "stop", "resume", "install", "sign in", "log in", "submit", "select all", "clear", "unload", "reload",
        "reload latest", "save as", "learn more", "settings", "refresh",
        "đóng", "dong", "hủy", "huỷ", "huy", "hủy bỏ", "huỷ bỏ", "huy bo", "để sau", "de sau", "có", "không", "khong",
        "không phải bây giờ", "khong phai bay gio", "đồng ý", "bỏ qua", "thử lại", "tiếp tục", "tiếp", "quay lại", "hoàn tất",
        "áp dụng", "trợ giúp", "lưu", "không lưu", "khong luu", "luôn tải", "tải một lần", "không tải", "khong tai",
        "cập nhật", "xóa", "xoá", "mở", "chọn", "chấp nhận", "từ chối",
    };

    /// <summary>Labels used unchanged by other UI languages too (OK, No, Details, Export, ...): no proof on their own.</summary>
    private static readonly HashSet<string> NeutralLabels = new(StringComparer.Ordinal)
    {
        "ok", "no", "details", "export", "update", "start", "reset", "info", "options",
    };

    /// <summary>English / Vietnamese dialog-title words (a German "Warnung" or "Fehler" does not match).</summary>
    private static readonly Regex TitleWords = Rx(
        @"\bwarning\b|\berror\b|\bquestion\b|\bconfirm|\bnotice\b|\bsecurity\b|\bunsigned\b|\bcannot\b|" +
        @"\bcảnh\s+báo|\blỗi\b|\bthông\s+báo|\bxác\s+nhận|\bbảo\s+mật|\bcâu\s+hỏi");

    /// <summary>The forbidden categories (for documentation and tool descriptions).</summary>
    public static IReadOnlyList<string> Categories { get; } = Forbidden.Select(f => f.Category).ToArray();

    /// <summary>
    /// Classifies a dialog that was read: its title, its texts and the labels of its ENABLED buttons. A disabled button cannot be
    /// pressed, so it does not make the dialog forbidden (Revit's Review Warnings shows a disabled "Delete Checked..." until a
    /// warning is checked; its "Close" must stay answerable). Enabled state comes from IsWindowEnabled (Win32) or UIA IsEnabled.
    /// Fails closed on language: a dialog with buttons that is not recognisably English or Vietnamese
    /// (<see cref="IsRecognizedLanguage"/>) is <see cref="DialogSafetyVerdict.UnrecognizedLanguageCategory"/>, i.e. forbidden.
    /// </summary>
    public static DialogSafetyVerdict ClassifyDialog(string? title, DialogDetails? details)
    {
        var verdict = Classify(title, details?.Texts, details?.Buttons.Where(b => b.Enabled).Select(b => b.Text));
        if (verdict.ForbiddenTerm is null && details is { Buttons.Count: > 0 } && !IsRecognizedLanguage(title, details.Buttons.Select(b => b.Text)))
        {
            return verdict with { ForbiddenTerm = DialogSafetyVerdict.UnrecognizedLanguageCategory };
        }

        return verdict;
    }

    /// <summary>
    /// True when a dialog is recognisably English or Vietnamese, so the forbidden terms above can be trusted: a button label
    /// proves it (Cancel, Close, Yes, Later, Always Load, Đóng, Hủy, Để sau, ...); a language-neutral label (OK, No, Details,
    /// ...) counts only when every labelled button is a known label; otherwise the title must carry an English / Vietnamese
    /// dialog word (Warning, Error, Cảnh báo, ...). A German "Immer laden / Einmal laden / Nicht laden" prompt is not recognised.
    /// </summary>
    public static bool IsRecognizedLanguage(string? title, IEnumerable<string?> buttonLabels)
    {
        var labels = buttonLabels.Select(Label).Where(l => l.Length > 0).ToList();
        if (labels.Any(LanguageLabels.Contains))
        {
            return true;
        }

        if (labels.Count > 0 && labels.All(NeutralLabels.Contains))
        {
            return true;
        }

        try
        {
            return TitleWords.IsMatch(Normalize(title));
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // fail closed: not recognised
        }
    }

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

    /// <summary>Normalized first line of a button label (a command link's note line is not its label).</summary>
    private static string Label(string? text) => SafeButtonPolicy.Normalize((text ?? string.Empty).Split('\n')[0]);

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
