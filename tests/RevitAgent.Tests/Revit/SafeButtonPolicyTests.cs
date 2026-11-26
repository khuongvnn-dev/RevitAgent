using RevitAgent.Core.Revit;

namespace RevitAgent.Tests.Revit;

public class SafeButtonPolicyTests
{
    private static DialogButton[] Buttons(params string[] labels) => labels.Select(l => new DialogButton { Text = l }).ToArray();

    [Theory]
    [InlineData("Always Load|Load Once|Do Not Load", false, false, "Do Not Load")]
    [InlineData("OK|Cancel", false, false, "Cancel")]
    [InlineData("Delete|Cancel", false, false, "Cancel")]
    [InlineData("Yes|No", false, false, "No")]
    [InlineData("Yes|No|Close", false, false, "No")]
    [InlineData("Yes|Close", false, false, "Close")]
    [InlineData("OK|Hủy", false, false, "Hủy")]
    [InlineData("OK|Huỷ", false, false, "Huỷ")]
    [InlineData("Có|Không", false, false, "Không")]
    [InlineData("Đồng ý|Đóng", false, false, "Đóng")]
    [InlineData("Yes|No|Cancel", true, true, "Cancel")]             // save prompt: Cancel beats Don't save
    [InlineData("Save|Don't Save", true, true, "Don't Save")]
    [InlineData("Lưu|Không lưu", true, true, "Không lưu")]
    [InlineData("Yes|No", true, true, "No")]                         // pure save prompt of a launched Revit on a sandbox copy
    [InlineData("Save|Discard", true, true, "Discard")]
    [InlineData("Save|Don't Save|Close", true, true, "Don't Save")]
    [InlineData("Yes|No", true, false, null)]                        // save prompt, discard not allowed: "No" would lose work
    [InlineData("Save|Don't Save", true, false, null)]
    [InlineData("Save|Don't Save|Cancel", true, false, "Cancel")]
    [InlineData("Save|Don't Save|Close", true, false, "Close")]
    [InlineData("Don't Save|Cancel", false, false, "Cancel")]        // the don't-save family is for save prompts only
    [InlineData("Discard|Keep", false, false, null)]
    public void Pick_Table(string labels, bool savePrompt, bool discard, string? expected)
    {
        var picked = SafeButtonPolicy.Pick(Buttons(labels.Split('|')), savePrompt, discard);
        Assert.Equal(expected, picked?.Text);
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("Always Load|Load Once")]
    [InlineData("Recover|Restore")]
    [InlineData("Sign in|Register")]
    [InlineData("Yes|OK")]
    [InlineData("Replace|Keep both")]
    [InlineData("Overwrite|Rename")]
    [InlineData("Activate license|Buy")]
    [InlineData("Cancel Delete|Delete")]                              // not an exact label
    public void Pick_NoSafeButton_ReturnsNull(string labels) =>
        Assert.Null(SafeButtonPolicy.Pick(Buttons(labels.Split('|')), isSavePrompt: false, discardAllowed: false));

    [Fact]
    public void Pick_IgnoresDisabled_AndFailsClosedOnAmbiguity()
    {
        var disabledCancel = new[] { new DialogButton { Text = "Cancel", Enabled = false }, new DialogButton { Text = "Close" } };
        Assert.Equal("Close", SafeButtonPolicy.Pick(disabledCancel)?.Text);

        Assert.Null(SafeButtonPolicy.Pick(Buttons("Cancel", "Cancel", "OK")));
    }

    [Fact]
    public void Pick_EmptyLabelWithCancelId_IsCancel()
    {
        var picked = SafeButtonPolicy.Pick(new[] { new DialogButton { Text = "", Id = "1" }, new DialogButton { Text = "", Id = "2" } });
        Assert.Equal("2", picked?.Id);
    }
}
