using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;
using RevitAgent.Tests.Ui;

namespace RevitAgent.Tests.Revit;

/// <summary>A real German-labelled modal dialog (helper process the test starts): neither revit_handle_dialog nor ui_click may answer it.</summary>
[Collection(DialogProcessCollection.Name)]
public class DialogLanguageDesktopTests
{
    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task GermanUnsignedAddinDialog_IsNeverAnswered_ByTheAgent()
    {
        const string title = "Sicherheit - Nicht signiertes Add-In";
        using var host = DialogHostProcess.ModalForm(title, "Der Herausgeber konnte nicht überprüft werden.", "Immer laden|Einmal laden|Nicht laden");
        var dialog = host.WaitForWindow(title);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process); // even in a gateway-launched process
        var registry = sp.GetRequiredService<ToolRegistry>();

        var b = Assert.Single(sp.GetRequiredService<IRevitReadiness>().GetState(host.Pid).BlockingDialogs);
        Assert.Equal(DialogCategories.Forbidden, b.Category);

        var handle = await registry.InvokeAsync("revit_handle_dialog", JsonSerializer.SerializeToElement(new { pid = host.Pid, hwnd = dialog.HwndHex, button = "Immer laden" }), "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, handle.ErrorCode);

        var click = await registry.InvokeAsync("ui_click", JsonSerializer.SerializeToElement(new { pid = host.Pid, window = title, selector = "name=\"Immer laden\"" }), "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, click.ErrorCode);
        Assert.Contains("unrecognized UI language", ((TextContent)click.Content[0]).Text);
        Assert.Null(host.WaitForResult(1));
    }
}

/// <summary>
/// Dialog safety fails closed on language: the forbidden terms are English and Vietnamese, so a dialog in another Revit UI
/// language (German here) is classified <c>unrecognized_language</c> = forbidden: never answered by an agent, a rule or a
/// safe dismiss.
/// </summary>
public class DialogLanguageTests
{
    private static DialogDetails Dialog(string text, params string[] buttons) => new()
    {
        Texts = new[] { text },
        Buttons = buttons.Select((b, i) => new DialogButton { Text = b, Id = "CommandLink_" + (1001 + i) }).ToArray(),
        Complete = true,
    };

    /// <summary>German Revit's unsigned add-in prompt ("Security - Unsigned Add-In").</summary>
    private static DialogDetails GermanUnsignedAddin() =>
        Dialog("Der Herausgeber dieses Add-Ins konnte nicht überprüft werden.", "Immer laden", "Einmal laden", "Nicht laden");

    [Fact]
    public void GermanUnsignedAddinPrompt_IsUnrecognizedLanguage_AndForbidden()
    {
        var verdict = DialogSafety.ClassifyDialog("Sicherheit - Nicht signiertes Add-In", GermanUnsignedAddin());

        Assert.True(verdict.IsForbidden);
        Assert.True(verdict.IsUnrecognizedLanguage);
        Assert.Equal(DialogSafetyVerdict.UnrecognizedLanguageCategory, verdict.ForbiddenTerm);
        Assert.False(verdict.DiscardExceptionAllowed);
        Assert.Contains("unrecognized UI language", verdict.Reason);
    }

    [Theory]
    [InlineData("Revit", "Elemente werden gelöscht.", "OK|Abbrechen")]                         // OK alone proves nothing
    [InlineData("Autodesk Revit 2024", "Lizenz abgelaufen.", "Schließen")]
    [InlineData("Warnung - kann ignoriert werden", "Wände überlappen.", "Anzeigen|Weitere Informationen|Erweitern|Schließen")]
    [InlineData("Fehler", "Datei überschreiben?", "Ja|Nein|Abbrechen")]
    public void GermanDialogs_AreUnrecognized(string title, string text, string buttons)
    {
        var verdict = DialogSafety.ClassifyDialog(title, Dialog(text, buttons.Split('|')));
        Assert.True(verdict.IsUnrecognizedLanguage, $"{title}: {verdict.Reason}");
    }

    [Theory]
    [InlineData("Revit", "Just so you know.", "OK")]                           // OK alone, every label known
    [InlineData("Revit", "Pick one", "Yes|No|Cancel")]
    [InlineData("Copied Central Model", "The central model has been copied.", "Close")]
    [InlineData("MyAddin.App - MyProject", "Phiên bản mới 1.2.0.1.", "Cập nhật|Để sau")]
    [InlineData("Revit", "Bạn có chắc không?", "Có|Không|Hủy")]
    [InlineData("Warning", "Custom add-in step.", "Run step")]                 // unknown label, but an English title word
    [InlineData("Settings", "Apply the settings?", "Apply|Cancel")]
    public void EnglishAndVietnameseDialogs_AreRecognized(string title, string text, string buttons)
    {
        var verdict = DialogSafety.ClassifyDialog(title, Dialog(text, buttons.Split('|')));
        Assert.False(verdict.IsUnrecognizedLanguage, $"{title}: {verdict.Reason}");
        Assert.False(verdict.IsForbidden, $"{title}: {verdict.Reason}");
    }

    [Fact]
    public void ForbiddenEnglishTerms_StillWin_OverTheLanguageCheck()
    {
        var verdict = DialogSafety.ClassifyDialog("Security - Unsigned Add-In", Dialog("The publisher could not be verified.", "Always Load", "Load Once", "Do Not Load"));
        Assert.Equal("security", verdict.ForbiddenTerm);
    }

    [Fact]
    public void DialogWithoutButtons_IsNotJudgedOnLanguage()
    {
        // Nothing can be pressed by label anyway; the title-only path keeps its old behaviour.
        var verdict = DialogSafety.ClassifyDialog("Fortschritt", new DialogDetails { Texts = new[] { "Bitte warten" }, Complete = true });
        Assert.False(verdict.IsForbidden);
    }

    [Fact]
    public void GermanDialog_IsNeverSafeDismissed_EvenInAGatewayLaunchedRevit()
    {
        var german = Dialog("Eine neue Version ist verfügbar.", "Später", "Schließen");
        Assert.Null(SafeDismissPolicy.Pick("MyAddin", german, owned: true, blocksGatewayOperation: true));

        // Mixed: an English "Close" next to German text is recognised, but the gateway still only presses its own labels.
        var english = Dialog("A new version is available.", "Later", "Close");
        Assert.Equal("Later", SafeDismissPolicy.Pick("MyAddin", english, owned: true, blocksGatewayOperation: true)?.Text);
    }

    [Fact]
    public void IsRecognizedLanguage_UsesButtonsOrTitle()
    {
        Assert.True(DialogSafety.IsRecognizedLanguage("Revit", new[] { "&Cancel" }));
        Assert.True(DialogSafety.IsRecognizedLanguage("Revit", new[] { "Close\nCloses the dialog" })); // command link note line ignored
        Assert.True(DialogSafety.IsRecognizedLanguage("Revit - Error", new[] { "Weiter" }));
        Assert.True(DialogSafety.IsRecognizedLanguage("Cảnh báo", new[] { "Tiếp" }));
        Assert.False(DialogSafety.IsRecognizedLanguage("Revit", new[] { "OK", "Abbrechen" }));
        Assert.False(DialogSafety.IsRecognizedLanguage("Revit", new[] { "Aceptar", "Cancelar" }));
        Assert.False(DialogSafety.IsRecognizedLanguage(null, Array.Empty<string>()));
    }
}
