using RevitAgent.Core.Revit;

namespace RevitAgent.Tests.Revit;

/// <summary>A dialog read only partially must fail closed: forbidden words may hide in the unread part.</summary>
public sealed class IncompleteDialogReadTests
{
    private static DialogDetails Details(bool complete) => new()
    {
        Texts = new[] { "The model was updated." },
        Buttons = new[] { new DialogButton { Text = "OK", Enabled = true } },
        Source = "win32",
        Complete = complete,
    };

    [Fact]
    public void CompleteHarmlessRead_IsNotForbidden() =>
        Assert.False(RevitReadiness.VerdictOf("Autodesk Revit", Details(true)).IsForbidden);

    [Fact]
    public void IncompleteRead_IsForbidden_AndHasNoDiscardException()
    {
        var v = RevitReadiness.VerdictOf("Autodesk Revit", Details(false));
        Assert.True(v.IsForbidden);
        Assert.False(v.DiscardExceptionAllowed);
        Assert.Contains("incomplete read", v.Reason);
    }
}
