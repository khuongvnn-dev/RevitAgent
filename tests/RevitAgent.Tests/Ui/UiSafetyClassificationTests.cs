using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Tests.Ui;

/// <summary>
/// Which words of a dialog decide whether ui_* actions are refused (live finding 2026-11-25: Revit's Schedule
/// Properties form was refused because of its "Delete" / "Delete Field" function buttons).
/// </summary>
public class UiSafetyClassificationTests
{
    private static void Check(string title, string[] texts, string[] buttons, bool isForm) =>
        UiSafety.EnsureAllowed(title, UiSafety.ClassifiedTexts(texts, buttons, isForm));

    [Fact]
    public void Form_WithDeleteFunctionButtons_IsAllowed()
    {
        Check(
            "Schedule Properties",
            new[] { "Select available fields from:", "Available fields:", "Scheduled fields (in order):" },
            new[] { "Edit", "Delete", "Add", "Remove", "Delete Field", "OK", "Cancel" },
            isForm: true);
    }

    [Fact]
    public void Prompt_WhoseOnlyForbiddenWordIsTheAnswerButton_IsRefused()
    {
        var ex = Assert.Throws<GatewayException>(() =>
            Check("Revit", new[] { "Are you sure?" }, new[] { "Delete", "Cancel" }, isForm: false));
        Assert.Equal("PERMISSION_DENIED", ex.Code);
    }

    [Fact]
    public void Form_WithForbiddenStaticText_IsStillRefused()
    {
        var ex = Assert.Throws<GatewayException>(() =>
            Check("Add-in options", new[] { "The publisher of this add-in could not be verified." }, new[] { "OK" }, isForm: true));
        Assert.Equal("PERMISSION_DENIED", ex.Code);
    }

    [Fact]
    public void Form_WithForbiddenTitle_IsStillRefused()
    {
        Assert.Throws<GatewayException>(() =>
            Check("Security - Unsigned Add-In", Array.Empty<string>(), new[] { "Load Once" }, isForm: true));
    }
}
