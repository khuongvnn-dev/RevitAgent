using System.Text;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Revit;

/// <summary>
/// Decides whether the gateway itself may press a "safe dismiss" button on a dialog that blocks one of its own operations.
/// Pure (no window is touched here); <see cref="RevitReadiness.TrySafeDismiss"/> acts on the decision. ALL of these must hold,
/// otherwise nothing is pressed and the dialog keeps today's handling (MODAL_BLOCKED; forbidden ones: refusal / Telegram escalation):
/// <list type="number">
/// <item>The Revit was launched by this gateway (<see cref="LaunchedProcessRegistry.IsOwned"/>).</item>
/// <item>The dialog blocks a gateway operation in progress: revit_close, the doc_open dialog watch, or the readiness wait of
/// revit_launch / revit_wait_ready.</item>
/// <item>The dialog was read completely, shows no progress bar, and its title, texts and ENABLED buttons carry no forbidden meaning
/// (<see cref="DialogSafety.ClassifyDialog"/>, save prompts included) and no destructive one (discard, remove, erase, lose, ...).</item>
/// <item>An enabled button has a safe-dismiss label (exact, first line): Later / Để sau / Remind me later / Not now /
/// Không phải bây giờ, then Close / Đóng, then Cancel / Hủy / Huỷ. The first tier with a hit wins; two hits in one tier
/// are ambiguous and press nothing.</item>
/// </list>
/// OK, Yes, No, Apply, Update / Cập nhật and every other label are never pressed.
/// </summary>
public static class SafeDismissPolicy
{
    private static readonly string[][] Tiers =
    {
        new[] { "later", "để sau", "de sau", "remind me later", "not now", "không phải bây giờ", "khong phai bay gio" },
        new[] { "close", "đóng", "dong" },
        new[] { "cancel", "hủy", "huỷ", "huy" },
    };

    private const int CancelTier = 2;

    /// <summary>Destructive meaning beyond the forbidden categories of <see cref="DialogSafety"/> (which already has delete / overwrite / replace).</summary>
    private static readonly Regex Destructive = new(
        @"\bdiscard|\bremov|\berase|\bpurge|\buninstall|\blose\b|\blost\b|\bloss\b|\bgỡ\s+bỏ|\bgo\s+bo\b|\bloại\s+bỏ|\bloai\s+bo\b|" +
        @"\bmất\s+(dữ\s+liệu|các\s+thay\s+đổi|thay\s+đổi)|\bmat\s+(du\s+lieu|cac\s+thay\s+doi|thay\s+doi)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>Every safe-dismiss label, in tier order (documentation and tests).</summary>
    public static IReadOnlyList<string> Labels { get; } = Tiers.SelectMany(t => t).ToArray();

    /// <summary>The button the gateway may press, or null when any condition fails.</summary>
    /// <param name="title">Window title of the dialog.</param>
    /// <param name="details">What was read from the dialog (texts, buttons with their enabled state).</param>
    /// <param name="owned">The Revit was launched by this gateway.</param>
    /// <param name="blocksGatewayOperation">The dialog blocks a gateway operation in progress (revit_close, doc_open, revit_launch, revit_wait_ready).</param>
    /// <param name="allowCancel">False skips the Cancel tier (revit_close: Cancel on a closing question cancels the close).</param>
    public static DialogButton? Pick(string? title, DialogDetails? details, bool owned, bool blocksGatewayOperation, bool allowCancel = true)
    {
        if (!owned || !blocksGatewayOperation || details is not { Complete: true } || details.HasProgressBar || details.Buttons.Count == 0)
        {
            return null;
        }

        if (DialogSafety.ClassifyDialog(title, details).IsForbidden)
        {
            return null;
        }

        var enabled = details.Buttons.Where(b => b.Enabled).ToList();
        var everything = string.Join("\n", new[] { title ?? string.Empty }.Concat(details.Texts).Concat(enabled.Select(b => Win32Windows.StripMnemonic(b.Text))));
        if (IsDestructive(everything))
        {
            return null;
        }

        for (var tier = 0; tier < Tiers.Length; tier++)
        {
            if (tier == CancelTier && !allowCancel)
            {
                continue;
            }

            var hits = enabled.Where(b => Tiers[tier].Contains(Label(b.Text))).ToList();
            if (hits.Count == 1)
            {
                return hits[0];
            }

            if (hits.Count > 1)
            {
                return null; // ambiguous: fail closed
            }
        }

        return null;
    }

    /// <summary>Normalized first line of a label (a command link's note line is ignored for matching; it is still scanned above).</summary>
    private static string Label(string? text) => SafeButtonPolicy.Normalize((text ?? string.Empty).Split('\n')[0]);

    private static bool IsDestructive(string text)
    {
        try
        {
            return Destructive.IsMatch(text.Normalize(NormalizationForm.FormC).Replace(' ', ' '));
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // fail closed
        }
    }
}
