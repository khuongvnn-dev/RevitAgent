using System.Text;
using RevitAgent.Core.Revit;

namespace RevitAgent.Tests.Revit;

/// <summary>
/// The safe-dismiss decision (pure): dialog model + owned + blocking-operation flags → the button the gateway may press, or null.
/// Cases from the live run on Revit 2024 (2026-11-26): Review Warnings blocked revit_close, the MyAddin update prompt appeared on close.
/// </summary>
public class SafeDismissPolicyTests
{
    private const string AddinTitle = "MyAddin.App - MyProject";

    private static DialogButton B(string text, bool enabled = true, string? id = null) => new() { Text = text, Enabled = enabled, Id = id };

    private static DialogDetails D(string[] texts, params DialogButton[] buttons) => new() { Texts = texts, Buttons = buttons };

    /// <summary>The add-in's update TaskDialog: command links "Cập nhật" (update) and "Để sau" (later, CommandLink_1002).</summary>
    private static DialogDetails AddinDialog() => new()
    {
        IsTaskDialog = true,
        Source = "win32+uia",
        Texts = new[] { "Đã có phiên bản mới của MyProject", "Phiên bản hiện tại 1.2.0.0, phiên bản mới 1.2.0.1. Bạn có muốn cập nhật không?" },
        Buttons = new[]
        {
            B("Cập nhật", id: "CommandLink_1001") with { Kind = DialogButtonKind.TaskDialog, NumericId = 1001 },
            B("Để sau", id: "CommandLink_1002") with { Kind = DialogButtonKind.TaskDialog, NumericId = 1002 },
        },
    };

    /// <summary>Manage &gt; Review Warnings: "Delete Checked..." is disabled until a warning is checked.</summary>
    private static DialogDetails ReviewWarnings(bool deleteEnabled = false) => D(
        new[] { "Highlighted walls overlap. One of them may be ignored when Revit finds room boundaries." },
        B("Show"), B("More Info"), B("Delete Checked...", enabled: deleteEnabled), B("Expand"), B("Export..."), B("Close"));

    private static DialogButton? Pick(string title, DialogDetails details, bool owned = true, bool blocking = true, bool allowCancel = true) =>
        SafeDismissPolicy.Pick(title, details, owned, blocking, allowCancel);

    [Fact]
    public void MyAddinUpdatePrompt_PressesDeSau()
    {
        var picked = Pick(AddinTitle, AddinDialog());
        Assert.Equal("Để sau", picked?.Text);
        Assert.Equal("CommandLink_1002", picked?.Id);
    }

    [Fact]
    public void MyAddinUpdatePrompt_DuringClose_StillPressesDeSau() =>
        Assert.Equal("Để sau", Pick(AddinTitle, AddinDialog(), allowCancel: false)?.Text);

    [Fact]
    public void ReviewWarnings_WithDisabledDeleteChecked_PressesClose() =>
        Assert.Equal("Close", Pick("Warnings", ReviewWarnings())?.Text);

    [Fact]
    public void ReviewWarnings_WithDeleteCheckedEnabled_PressesNothing() =>
        Assert.Null(Pick("Warnings", ReviewWarnings(deleteEnabled: true)));

    [Fact]
    public void OkOnly_PressesNothing() => Assert.Null(Pick("Autodesk Revit 2024", D(new[] { "Something happened." }, B("OK"))));

    [Fact]
    public void NotLaunchedByTheGateway_PressesNothing()
    {
        Assert.Null(Pick(AddinTitle, AddinDialog(), owned: false));
        Assert.Null(Pick("Warnings", ReviewWarnings(), owned: false));
    }

    [Fact]
    public void NoGatewayOperationBlocked_PressesNothing() => Assert.Null(Pick(AddinTitle, AddinDialog(), blocking: false));

    [Fact]
    public void SecurityDialog_PressesNothing()
    {
        Assert.Null(Pick("Security - Unsigned Add-In", D(new[] { "The publisher of this add-in could not be verified." }, B("Always Load"), B("Load Once"), B("Do Not Load"), B("Cancel"))));
        Assert.Null(Pick("Revit", D(new[] { "This file is digitally signed by an unknown publisher." }, B("Close"))));
    }

    [Theory]
    [InlineData("Autodesk Licensing", "Your license expires in 3 days.")]
    [InlineData("Sign In", "Please sign in to Autodesk.")]
    [InlineData("File Recovery", "Revit found backup files.")]
    [InlineData("Revit", "Overwrite the existing file?")]
    [InlineData("Revit", "Replace the existing family?")]
    [InlineData("Autodesk Revit 2024", "Do you want to save changes to Project1.rvt?")] // save prompts as today
    public void ForbiddenCategories_PressNothing(string title, string text) =>
        Assert.Null(Pick(title, D(new[] { text }, B("OK"), B("Cancel"), B("Close"), B("Later"))));

    [Theory]
    [InlineData("This will delete 3 elements.")]
    [InlineData("Bạn có muốn xóa phần tử?")]
    [InlineData("Unsaved changes will be lost.")]
    [InlineData("Discard the local changes?")]
    [InlineData("The elements will be removed from the group.")]
    [InlineData("Erase the cache now?")]
    [InlineData("Purge unused families?")]
    [InlineData("Uninstall the old version first?")]
    [InlineData("Bạn sẽ mất dữ liệu chưa lưu.")]
    [InlineData("Gỡ bỏ phiên bản cũ?")]
    public void DestructiveMessageText_IsNeverAutoDismissed(string text) =>
        Assert.Null(Pick("MyAddin.App", D(new[] { text }, B("Close"), B("Cancel"), B("Later"))));

    [Theory]
    [InlineData("Discard")]
    [InlineData("Remove all")]
    [InlineData("Delete")]
    public void EnabledDestructiveButton_PressesNothing(string label) =>
        Assert.Null(Pick("MyAddin.App", D(new[] { "A new version is available." }, B(label), B("Later"))));

    [Fact]
    public void DisabledDestructiveButton_DoesNotBlockTheDismiss() =>
        Assert.Equal("Later", Pick("MyAddin.App", D(new[] { "A new version is available." }, B("Remove old version", enabled: false), B("Later")))?.Text);

    [Theory]
    [InlineData("OK")]
    [InlineData("Yes")]
    [InlineData("No")]
    [InlineData("Apply")]
    [InlineData("Update")]
    [InlineData("Cập nhật")]
    [InlineData("Continue")]
    [InlineData("Ignore")]
    [InlineData("Retry")]
    public void OtherLabels_AreNeverPressed(string label) =>
        Assert.Null(Pick("MyAddin.App", D(new[] { "A new version is available." }, B(label))));

    [Theory]
    [InlineData("Later")]
    [InlineData("Remind me later")]
    [InlineData("Remind Me Later...")]
    [InlineData("Not now")]
    [InlineData("Không phải bây giờ")]
    [InlineData("Để sau")]
    [InlineData("Close")]
    [InlineData("&Close")]
    [InlineData("Đóng")]
    [InlineData("Cancel")]
    [InlineData("Hủy")]
    [InlineData("Huỷ")]
    public void SafeDismissLabels_ArePressed(string label) =>
        Assert.Equal(label, Pick("MyAddin.App", D(new[] { "A new version is available." }, B("Update"), B(label)))?.Text);

    [Fact]
    public void DecomposedVietnamese_IsNormalized()
    {
        var label = "Để sau".Normalize(NormalizationForm.FormD);
        Assert.Equal(label, Pick(AddinTitle, D(new[] { "Có bản mới." }, B("Cập nhật"), B(label)))?.Text);
    }

    [Fact]
    public void CommandLinkWithNoteLine_MatchesOnItsFirstLine() =>
        Assert.Equal("CommandLink_1002", Pick(AddinTitle, D(new[] { "Có bản mới." }, B("Cập nhật", id: "CommandLink_1001"), B("Để sau\nNhắc lại vào lần mở Revit sau", id: "CommandLink_1002")))?.Id);

    [Theory]
    [InlineData("Later|Close|Cancel", "Later")]       // postpone first
    [InlineData("Close|Cancel", "Close")]
    [InlineData("OK|Cancel", "Cancel")]
    [InlineData("Close|Close", null)]                 // ambiguous: fail closed
    [InlineData("Later|Not now", null)]
    public void Tiers_PostponeThenCloseThenCancel(string labels, string? expected) =>
        Assert.Equal(expected, Pick("Add-in", D(new[] { "Information." }, labels.Split('|').Select(l => B(l)).ToArray()))?.Text);

    [Fact]
    public void WithoutCancel_RevitCloseNeverPressesCancel()
    {
        Assert.Null(Pick("Add-in", D(new[] { "Synchronize before closing?" }, B("Yes"), B("No"), B("Cancel")), allowCancel: false));
        Assert.Equal("Close", Pick("Add-in", D(new[] { "Information." }, B("Close"), B("Cancel")), allowCancel: false)?.Text);
    }

    [Fact]
    public void DisabledSafeButton_IsNotPressed() =>
        Assert.Null(Pick("Add-in", D(new[] { "Information." }, B("OK"), B("Close", enabled: false))));

    [Fact]
    public void IncompleteOrUnreadDialog_PressesNothing()
    {
        Assert.Null(SafeDismissPolicy.Pick("Warnings", null, owned: true, blocksGatewayOperation: true));
        Assert.Null(Pick("Warnings", ReviewWarnings() with { Complete = false }));
        Assert.Null(Pick("Warnings", new DialogDetails { Source = "title_only", Complete = false }));
    }

    [Fact]
    public void ProgressDialog_PressesNothing() =>
        Assert.Null(Pick("Loading links", D(new[] { "Loading linked models..." }, B("Cancel")) with { HasProgressBar = true }));

    [Fact]
    public void Labels_AreTheApprovedSet_Only()
    {
        Assert.Equal(
            new[] { "later", "để sau", "de sau", "remind me later", "not now", "không phải bây giờ", "khong phai bay gio", "close", "đóng", "dong", "cancel", "hủy", "huỷ", "huy" },
            SafeDismissPolicy.Labels);
    }
}
