using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui;

/// <summary>
/// Dialog safety for the UI engine: a security / license / sign-in / recovery / delete / overwrite / replace dialog is
/// never acted on by ui_* actions or UI scripts — a human must answer it (same rule as revit_handle_dialog).
/// Read-only tools are not guarded.
/// </summary>
public static class UiSafety
{
    /// <summary>Time budget for reading a dialog's texts before an action.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Throws PERMISSION_DENIED unless input may go to <paramref name="w"/> (the window that will receive it, after refs and
    /// selectors were resolved): first the process check (<see cref="UiAutomationService.EnsureInputTarget"/>: never the
    /// gateway, Revit or an owner-allowed application only, a grant for a Revit the gateway did not launch), then the dialog
    /// check (forbidden dialogs, and prompts in a UI language the gateway cannot classify).
    /// </summary>
    public static void EnsureActionAllowed(UiOp op, TopLevelWindow w)
    {
        op.Service.EnsureInputTarget(w.ProcessId, Sandbox.EvidenceService.CurrentCaller?.Tool ?? "UI input");
        var texts = new List<string>();
        // The application's main window carries ribbon buttons ("Delete", ...) that are not dialog content: title only.
        bool main = false;
        try
        {
            main = op.Service.IsMainWindow(w);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        var unrecognized = false;
        if (!main)
        {
            try
            {
                var own = DialogOwnText(op, op.Core.FromHandle(w.Hwnd), ReadBudget);
                texts.AddRange(ClassifiedTexts(own.Texts, own.Buttons, own.IsForm));

                // A modal prompt in another UI language (a German "Immer laden"): its forbidden terms cannot be checked.
                unrecognized = w.IsModalDialog && !own.IsForm && own.Buttons.Count > 0 && !DialogSafety.IsRecognizedLanguage(w.Title, own.Buttons);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Could not read the window content: the title alone is classified.
            }
        }

        if (unrecognized)
        {
            throw Refusal(w.Title, DialogSafetyVerdict.UnrecognizedLanguage); // fail closed: a human answers it
        }

        EnsureAllowed(w.Title, texts);
    }

    /// <summary>
    /// What is classified for a dialog: its static texts always; its button labels only for a PROMPT (message +
    /// answer buttons, e.g. a TaskDialog or MessageBox, where a "Delete" button IS the answer). A FORM dialog (it has
    /// edit boxes, lists, trees, combo boxes, tabs or grids) carries function buttons such as "Delete" / "Delete Field"
    /// in Revit's Schedule Properties — they are commands of the form, not the answer to a delete confirmation, and
    /// must not make the form unusable (live finding 2026-11-25). Prompts stay fail-closed on their button labels.
    /// </summary>
    public static IEnumerable<string> ClassifiedTexts(IReadOnlyList<string> texts, IReadOnlyList<string> buttons, bool isForm) =>
        isForm ? texts : texts.Concat(buttons);

    /// <summary>
    /// The dialog's own words: names of Text and Button elements, NOT the contents of lists, trees, combo boxes or
    /// grids. Data shown in a dialog (e.g. the category "Security Devices" in Revit's New Schedule list, a view named
    /// "Security Plan") is not a security prompt and must not make the dialog unusable (live finding 2026-11-20).
    /// <c>IsForm</c> is true when the dialog contains input controls (edit, list, tree, combo box, tab, grid, table).
    /// </summary>
    private static (List<string> Texts, List<string> Buttons, bool IsForm) DialogOwnText(UiOp op, Interop.UIAutomationClient.IUIAutomationElement root, TimeSpan budget)
    {
        var texts = new List<string>();
        var buttons = new List<string>();
        bool isForm = false;
        var walker = op.Core.Walker(false);
        var cr = op.Core.NewCacheRequest(new[]
        {
            Interop.UIAutomationClient.UIA_PropertyIds.UIA_NamePropertyId,
            Interop.UIAutomationClient.UIA_PropertyIds.UIA_ControlTypePropertyId,
            Interop.UIAutomationClient.UIA_PropertyIds.UIA_IsOffscreenPropertyId,
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stack = new Stack<Interop.UIAutomationClient.IUIAutomationElement>();
        stack.Push(root.BuildUpdatedCache(cr));
        while (stack.Count > 0 && sw.Elapsed < budget)
        {
            op.Token.ThrowIfCancellationRequested();
            var e = stack.Pop();
            try
            {
                var ct = e.GetCachedPropertyValue(Interop.UIAutomationClient.UIA_PropertyIds.UIA_ControlTypePropertyId) is int i ? i : 0;
                if (ct is ControlTypes.List or ControlTypes.Tree or ControlTypes.ComboBox or ControlTypes.DataGrid or ControlTypes.Table)
                {
                    isForm = true;
                    continue; // data, not the dialog's message
                }

                if (ct is ControlTypes.Edit or ControlTypes.Tab)
                {
                    isForm = true;
                }

                if (ct is ControlTypes.Text or ControlTypes.Button &&
                    e.GetCachedPropertyValue(Interop.UIAutomationClient.UIA_PropertyIds.UIA_NamePropertyId) is string name &&
                    name.Trim().Length > 0)
                {
                    (ct == ControlTypes.Text ? texts : buttons).Add(name.Trim());
                }

                if (ct == ControlTypes.Button)
                {
                    continue; // a button's subtree is its own label (WPF exposes it as a Text child): already counted
                }

                var kids = ElementSearch.Children(walker, e, cr);
                for (int j = kids.Count - 1; j >= 0; j--)
                {
                    stack.Push(kids[j]);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }

        return (texts, buttons, isForm);
    }

    /// <summary>Classification only (no UIA): throws PERMISSION_DENIED when the dialog is forbidden.</summary>
    public static void EnsureAllowed(string? title, IEnumerable<string>? texts)
    {
        var verdict = DialogSafety.Classify(title, texts);
        if (verdict.IsForbidden)
        {
            throw Refusal(title, verdict);
        }
    }

    public static GatewayException Refusal(string? title, DialogSafetyVerdict verdict) =>
        GatewayException.Denied(
            $"Refused: the window \"{title}\" {verdict.Reason}. Security / license / sign-in / recovery / delete / overwrite / replace dialogs " +
            "(and save prompts) are never answered by the gateway - a human must answer it: ask the user to do it themselves, then continue. " +
            "Read-only tools (ui_snapshot, ui_screenshot) still work on it.");
}
