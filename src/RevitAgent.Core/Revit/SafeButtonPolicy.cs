using System.Text;

namespace RevitAgent.Core.Revit;

/// <summary>
/// Picks the SAFE button of a forbidden dialog: the one that changes nothing when the owner never answers
/// (Do Not Load &gt; Cancel &gt; No &gt; Don't Save (save prompts only) &gt; Close; English and Vietnamese labels, exact match).
/// Never picks Always Load, OK / Yes, Recover / Restore, Sign in or anything license related. No match, or an ambiguous
/// match (the same label twice), means no safe button: the gateway then presses nothing (the dialog stays human-only).
/// </summary>
public static class SafeButtonPolicy
{
    private static readonly string[][] Tiers =
    {
        new[] { "do not load", "don't load", "dont load", "không tải", "khong tai" },
        new[] { "cancel", "huỷ", "hủy", "huy", "hủy bỏ", "huỷ bỏ", "huy bo" },
        new[] { "no", "không", "khong" },
        new[] { "don't save", "dont save", "do not save", "discard", "discard changes", "close without saving", "không lưu", "khong luu" },
        new[] { "close", "đóng", "dong" },
    };

    private const int NoTier = 2;
    private const int DontSaveTier = 3;

    /// <summary>The safe button, or null when there is none.</summary>
    /// <param name="buttons">The buttons read from the dialog.</param>
    /// <param name="isSavePrompt">The dialog asks "save changes?".</param>
    /// <param name="discardAllowed">
    /// Only meaningful for a save prompt: the Revit was launched by the gateway and the prompt is a pure save prompt (a sandbox copy),
    /// so "No" / "Don't save" / "Discard" may be pressed. A save prompt without this gets Cancel / Close or nothing.
    /// </param>
    public static DialogButton? Pick(IEnumerable<DialogButton> buttons, bool isSavePrompt = false, bool discardAllowed = false)
    {
        var candidates = buttons.Where(b => b.Enabled).Select(b => (Button: b, Label: Normalize(b.Text), b.Id)).ToList();
        for (var tier = 0; tier < Tiers.Length; tier++)
        {
            if (tier is NoTier or DontSaveTier && isSavePrompt && !discardAllowed)
            {
                continue; // "No" on a save prompt discards changes
            }

            if (tier == DontSaveTier && !isSavePrompt)
            {
                continue;
            }

            var hits = candidates.Where(c => Tiers[tier].Contains(c.Label) || (c.Label.Length == 0 && tier == 1 && IsCancelId(c.Id))).ToList();
            if (hits.Count == 1)
            {
                return hits[0].Button;
            }

            if (hits.Count > 1)
            {
                return null; // ambiguous: fail closed
            }
        }

        return null;
    }

    /// <summary>Normalized label (mnemonic markers removed, trailing punctuation trimmed, lower case).</summary>
    internal static string Normalize(string? text) =>
        Win32Windows.StripMnemonic(text ?? string.Empty).Trim().TrimEnd('.', '!', ':', '…').Trim()
            .Normalize(NormalizationForm.FormC).Replace('’', '\'').ToLowerInvariant();

    private static bool IsCancelId(string? id) =>
        id is "2" || string.Equals(id, "CommandButton_2", StringComparison.OrdinalIgnoreCase);
}
