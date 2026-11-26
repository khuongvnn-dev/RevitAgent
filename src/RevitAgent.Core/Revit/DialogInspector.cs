using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Revit;

/// <summary>
/// Reads the texts and buttons of a blocking window.
/// Classic dialogs (#32770 MessageBox etc.): Win32 child controls — Static/Edit text via WM_GETTEXT with
/// SendMessageTimeout, "Button" children with their real control ids.
/// TaskDialogs (#32770 + DirectUIHWND, Revit's TaskDialog): texts are windowless, button windows have id 0 —
/// texts and TaskDialog ids (AutomationId CommandButton_&lt;n&gt; / CommandLink_&lt;n&gt;) come from UI Automation
/// with a strict timeout; without UIA, the exact button windows can still be clicked (BM_CLICK).
/// Other modal windows (WPF): UI Automation buttons/texts with the same timeout; title only as last resort.
/// </summary>
public static class DialogInspector
{
    private const int MaxTextChars = 2000;
    private static readonly TimeSpan Win32TextBudget = TimeSpan.FromSeconds(2.5);
    private static readonly Regex TaskDialogId = new(@"^(CommandButton|CommandLink)_(\d+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DialogDetails Inspect(WindowInfo window, TimeSpan uiaTimeout)
    {
        var children = Win32Windows.Children(window.Hwnd);
        var isTaskDialog = window.IsDialogClass && children.Any(c => c.ClassName == "DirectUIHWND");
        var win32Progress = children.Any(c => c.Visible && IsProgressClass(c.ClassName));
        var notes = new List<string>();

        var texts = new List<string>();
        var win32Buttons = new List<(ChildControl Control, string Text)>();
        var complete = true;
        var sw = Stopwatch.StartNew();
        foreach (var c in children.Where(c => c.Visible))
        {
            if (sw.Elapsed > Win32TextBudget)
            {
                notes.Add("text reading stopped after 2.5 s (target slow)");
                complete = false;
                break;
            }

            if (IsButtonClass(c.ClassName) && IsPushButtonStyle(c.Style))
            {
                var label = Win32Windows.ReadText(c.Hwnd, 300);
                if (label is null)
                {
                    complete = false;
                }
                else if (!string.IsNullOrWhiteSpace(label))
                {
                    win32Buttons.Add((c, Win32Windows.StripMnemonic(label)));
                }
            }
            else if (IsTextClass(c.ClassName) && !IsPasswordEdit(c))
            {
                var text = Win32Windows.ReadText(c.Hwnd, MaxTextChars);
                if (text is null)
                {
                    complete = false;
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text.Trim());
                }
            }
        }

        var needsUia = isTaskDialog || (!window.IsDialogClass && win32Buttons.Count == 0);
        if (!needsUia)
        {
            var nothing = texts.Count == 0 && win32Buttons.Count == 0;
            return new DialogDetails
            {
                Texts = texts,
                Buttons = win32Buttons.Select(b => ClassicButton(b.Control, b.Text, win32Buttons)).ToArray(),
                IsTaskDialog = false,
                Source = nothing ? "title_only" : "win32",
                Complete = complete && !nothing,
                Note = Join(notes),
                HasProgressBar = win32Progress,
            };
        }

        var items = UiaDialogReader.Read(window.Hwnd, allDescendants: isTaskDialog, uiaTimeout, out var uiaNote);
        if (uiaNote is not null)
        {
            notes.Add(uiaNote);
        }

        if (items is null)
        {
            // UIA unavailable: keep what Win32 gave us. TaskDialog button windows are still clickable precisely (BM_CLICK).
            var fallback = win32Buttons.Select(b => isTaskDialog
                ? new DialogButton { Text = b.Text, Id = null, Enabled = b.Control.Enabled, Kind = DialogButtonKind.Win32Window, Hwnd = b.Control.Hwnd }
                : ClassicButton(b.Control, b.Text, win32Buttons)).ToArray();
            return new DialogDetails
            {
                Texts = texts,
                Buttons = fallback,
                IsTaskDialog = isTaskDialog,
                Source = texts.Count == 0 && fallback.Length == 0 ? "title_only" : "win32",
                Complete = false, // the content lives in UIA, which did not answer
                Note = Join(notes),
                HasProgressBar = win32Progress,
            };
        }

        foreach (var t in items.Where(i => i.ControlType == "Text" && !string.IsNullOrWhiteSpace(i.Name)))
        {
            var s = Truncate(t.Name.Trim());
            if (!texts.Contains(s) && !string.Equals(s, window.Title, StringComparison.Ordinal))
            {
                texts.Add(s);
            }
        }

        var buttons = new List<DialogButton>();
        if (isTaskDialog)
        {
            foreach (var item in items)
            {
                var m = TaskDialogId.Match(item.AutomationId);
                if (!m.Success)
                {
                    continue;
                }

                var id = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var name = Win32Windows.StripMnemonic(item.Name);
                var match = win32Buttons.FirstOrDefault(b => b.Control.Hwnd == item.Hwnd || b.Control.Parent == item.Hwnd);
                if (match.Control is null)
                {
                    match = win32Buttons.FirstOrDefault(b => string.Equals(FirstLine(b.Text), FirstLine(name), StringComparison.OrdinalIgnoreCase));
                }

                buttons.Add(new DialogButton
                {
                    Text = name,
                    Id = item.AutomationId,
                    Enabled = item.Enabled,
                    Kind = DialogButtonKind.TaskDialog,
                    NumericId = id,
                    Hwnd = match.Control?.Hwnd ?? IntPtr.Zero,
                });
            }

            // Buttons UIA did not expose with a TaskDialog id stay clickable through their own window.
            foreach (var b in win32Buttons.Where(b => buttons.All(x => x.Hwnd != b.Control.Hwnd)))
            {
                buttons.Add(new DialogButton { Text = b.Text, Enabled = b.Control.Enabled, Kind = DialogButtonKind.Win32Window, Hwnd = b.Control.Hwnd });
            }
        }
        else
        {
            buttons.AddRange(win32Buttons.Select(b => ClassicButton(b.Control, b.Text, win32Buttons)));
            foreach (var item in items.Where(i => i.ControlType == "Button" && (i.Name.Length > 0 || i.AutomationId.Length > 0)))
            {
                buttons.Add(new DialogButton
                {
                    Text = Win32Windows.StripMnemonic(item.Name),
                    Id = item.AutomationId.Length > 0 ? item.AutomationId : null,
                    Enabled = item.Enabled,
                    Kind = DialogButtonKind.Uia,
                });
            }
        }

        return new DialogDetails
        {
            Texts = texts,
            Buttons = buttons,
            IsTaskDialog = isTaskDialog,
            Source = win32Buttons.Count > 0 ? "win32+uia" : "uia",
            Note = Join(notes),
            HasProgressBar = win32Progress || items.Any(i => i.ControlType == "ProgressBar"),
        };
    }

    private static DialogButton ClassicButton(ChildControl c, string text, List<(ChildControl Control, string Text)> all)
    {
        // A real, unique control id → WM_COMMAND(BN_CLICKED); otherwise click that exact window (BM_CLICK).
        var unique = c.ControlId > 0 && c.ControlId < 0xFFFF && all.Count(x => x.Control.ControlId == c.ControlId && x.Control.Parent == c.Parent) == 1;
        return new DialogButton
        {
            Text = text,
            Id = unique ? c.ControlId.ToString(CultureInfo.InvariantCulture) : null,
            Enabled = c.Enabled,
            Kind = unique ? DialogButtonKind.Win32 : DialogButtonKind.Win32Window,
            NumericId = unique ? c.ControlId : null,
            Hwnd = c.Hwnd,
        };
    }

    /// <summary>msctls_progress32, also inside WinForms class names ("WindowsForms10.msctls_progress32.app...").</summary>
    private static bool IsProgressClass(string cls) => cls.Contains("msctls_progress32", StringComparison.OrdinalIgnoreCase);

    private static bool IsButtonClass(string cls) =>
        string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase) || cls.Contains(".BUTTON.", StringComparison.OrdinalIgnoreCase);

    /// <summary>ES_PASSWORD edits are never read: WM_GETTEXT ignores the mask and would return the typed secret.</summary>
    private static bool IsPasswordEdit(ChildControl c) =>
        (c.ClassName.Equals("Edit", StringComparison.OrdinalIgnoreCase) || c.ClassName.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase) ||
         c.ClassName.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) && (c.Style & 0x20) != 0;

    private static bool IsTextClass(string cls) =>
        string.Equals(cls, "Static", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(cls, "Edit", StringComparison.OrdinalIgnoreCase) ||
        cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
        cls.Contains(".STATIC.", StringComparison.OrdinalIgnoreCase) ||
        cls.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Push-like button styles (push, default push, split, command link, owner-draw); not check/radio/group boxes.</summary>
    private static bool IsPushButtonStyle(long style) => (style & 0xF) is 0x0 or 0x1 or 0xB or 0xC or 0xD or 0xE or 0xF;

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private static string Truncate(string s) => s.Length <= MaxTextChars ? s : s[..MaxTextChars] + "…";

    private static string? Join(List<string> notes) => notes.Count == 0 ? null : string.Join("; ", notes);
}
