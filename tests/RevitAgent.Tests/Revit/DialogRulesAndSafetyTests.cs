using System.IO;
using System.IO.Pipes;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Core;
using RevitAgent.Core.Revit;

namespace RevitAgent.Tests.Revit;

public class DialogRulesAndSafetyTests
{
    [Theory]
    [InlineData("Security - Unsigned Add-In", null, "security")]
    [InlineData("Revit", "The publisher could not be verified. Digitally signed?", "security")]
    [InlineData("Autodesk Licensing", null, "license")]
    [InlineData("Revit", "Your licence expired", "license")]
    [InlineData("Sign In", null, "sign in")]
    [InlineData("Revit", "Please log in to continue", "sign in")]
    [InlineData("File Recovery", null, "recovery")]
    [InlineData("Revit", "Elements will be deleted", "delete")]
    [InlineData("Revit", "Overwrite the existing file?", "overwrite")]
    [InlineData("Revit", "Replace the existing family?", "replace")]
    [InlineData("Cảnh báo bảo mật", null, "security")]
    [InlineData("Revit", "Giấy phép đã hết hạn", "license")]
    [InlineData("Revit", "Đăng nhập", "sign in")]
    [InlineData("Khôi phục", null, "recovery")]
    [InlineData("Revit", "Bạn có muốn xóa phần tử?", "delete")]
    [InlineData("Revit", "Bạn có muốn xoá phần tử?", "delete")]
    [InlineData("Revit", "Ghi đè tệp?", "overwrite")]
    [InlineData("Revit", "ghi de tep", "overwrite")]
    [InlineData("Revit", "Thay thế họ?", "replace")]
    public void Classify_FindsForbiddenCategories(string title, string? text, string category)
    {
        var verdict = DialogSafety.Classify(title, text is null ? null : new[] { text });
        Assert.Equal(category, verdict.ForbiddenTerm);
        Assert.True(verdict.IsForbidden);
    }

    [Theory]
    [InlineData("Copied Central Model", "The central model has been copied or moved.")]
    [InlineData("Unresolved References", "Revit could not find or read 1 references.")]
    [InlineData("Warning", "This dialog in Revit shows information.")] // "dialog in" is not "log in"
    [InlineData("Desktop Connector isn't installed", "Install it from the Autodesk Account.")]
    public void Classify_InformationalDialogs_AreNotForbidden(string title, string text)
    {
        var verdict = DialogSafety.Classify(title, new[] { text }, new[] { "&OK", "Close" });
        Assert.False(verdict.IsForbidden, verdict.Reason);
        Assert.Null(verdict.ForbiddenTerm);
    }

    [Fact]
    public void Classify_ButtonLabels_CountToo()
    {
        Assert.Equal("delete", DialogSafety.Classify("Warning", new[] { "Elements are joined." }, new[] { "Delete Element(s)", "OK" }).ForbiddenTerm);
    }

    /// <summary>Revit 2024 Manage &gt; Review Warnings (live 2026-11-26): "Delete Checked..." is disabled until a warning is checked.</summary>
    private static DialogDetails ReviewWarnings(bool deleteEnabled) => new()
    {
        Texts = new[] { "Highlighted walls overlap. One of them may be ignored when Revit finds room boundaries." },
        Buttons = new[]
        {
            new DialogButton { Text = "Show" }, new DialogButton { Text = "More Info" },
            new DialogButton { Text = "Delete Checked...", Enabled = deleteEnabled },
            new DialogButton { Text = "Expand" }, new DialogButton { Text = "Export..." }, new DialogButton { Text = "Close" },
        },
    };

    [Fact]
    public void ClassifyDialog_DisabledDestructiveButton_IsNotForbidden()
    {
        var verdict = DialogSafety.ClassifyDialog("Warnings", ReviewWarnings(deleteEnabled: false));
        Assert.False(verdict.IsForbidden, verdict.Reason);
        Assert.Null(verdict.ForbiddenTerm);
    }

    [Fact]
    public void ClassifyDialog_EnabledDestructiveButton_IsStillForbidden()
    {
        var verdict = DialogSafety.ClassifyDialog("Warnings", ReviewWarnings(deleteEnabled: true));
        Assert.True(verdict.IsForbidden);
        Assert.Equal("delete", verdict.ForbiddenTerm);
    }

    [Theory]
    [InlineData("Warnings", "Elements will be deleted.", "delete")]  // the message text counts whatever the buttons
    [InlineData("Delete Elements", "Are you sure?", "delete")]        // so does the title
    [InlineData("Revit", "Overwrite the existing file?", "overwrite")]
    [InlineData("Security - Unsigned Add-In", "The publisher could not be verified.", "security")]
    public void ClassifyDialog_TitleAndText_StayForbidden_WhateverTheButtons(string title, string text, string category)
    {
        var details = new DialogDetails
        {
            Texts = new[] { text },
            Buttons = new[] { new DialogButton { Text = "Delete", Enabled = false }, new DialogButton { Text = "Close" } },
        };
        Assert.Equal(category, DialogSafety.ClassifyDialog(title, details).ForbiddenTerm);
    }

    [Fact]
    public void ClassifyDialog_SavePrompt_StaysASavePrompt()
    {
        var details = new DialogDetails
        {
            Texts = new[] { "Do you want to save changes to Project1.rvt?" },
            Buttons = new[] { new DialogButton { Text = "Yes" }, new DialogButton { Text = "No" }, new DialogButton { Text = "Cancel" } },
        };
        var verdict = DialogSafety.ClassifyDialog("Autodesk Revit 2024", details);
        Assert.True(verdict.IsSavePrompt);
        Assert.True(verdict.DiscardExceptionAllowed);
    }

    [Theory]
    [InlineData("Autodesk Revit 2024", "Do you want to save changes to Project1.rvt?")]
    [InlineData("Save File", "Save changes to Project1?")]
    [InlineData("Revit", "Bạn có muốn lưu thay đổi?")]
    public void Classify_SavePrompts_AreForbiddenWithDiscardException(string title, string text)
    {
        var verdict = DialogSafety.Classify(title, new[] { text });
        Assert.True(verdict.IsSavePrompt);
        Assert.True(verdict.IsForbidden);
        Assert.True(verdict.DiscardExceptionAllowed);
    }

    [Fact]
    public void SavePrompt_WithAnotherForbiddenTerm_HasNoException()
    {
        var verdict = DialogSafety.Classify("Recovered file", new[] { "Do you want to save changes?" });
        Assert.True(verdict.IsSavePrompt);
        Assert.False(verdict.DiscardExceptionAllowed);
    }

    [Theory]
    [InlineData("&No", null, true)]
    [InlineData("Don't save", null, true)]
    [InlineData("Không lưu", null, true)]
    [InlineData("", "CommandButton_7", true)]
    [InlineData("Yes", "6", false)]
    [InlineData("Save", null, false)]
    [InlineData("Cancel", "2", false)]
    public void DontSaveButton_Detection(string text, string? id, bool expected) =>
        Assert.Equal(expected, DialogSafety.IsDontSaveButton(new DialogButton { Text = text, Id = id }));

    [Fact]
    public void BuiltinRules_LoadWithoutErrors()
    {
        var provider = new DialogRuleProvider(new AgentPaths(TestGateway.NewTempDir("revitagent_home_")));
        var set = provider.Load();
        Assert.Empty(set.Errors);
        Assert.Equal(4, set.Rules.Count);
        var r = set.Match("Revit", "Copied Central Model", Array.Empty<string>());
        Assert.NotNull(r);
        Assert.Equal("id=CommandButton_8", r!.Press);
        Assert.Null(set.Match("powershell", "Copied Central Model", Array.Empty<string>())); // proc: Revit only
        Assert.NotNull(set.Match("Revit.exe", "Unresolved References - Project1", Array.Empty<string>()));
        Assert.Null(set.Match("Revit", "Something else", Array.Empty<string>()));
    }

    [Fact]
    public void UserRules_AreMerged_AndReloadedWhenTheFileChanges()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var provider = new DialogRuleProvider(paths);
        Assert.Equal(4, provider.Load().Rules.Count);

        File.WriteAllText(provider.UserRulesPath, """{ "rules": [ { "id": "mine", "titleContains": "Info", "bodyPattern": "^Done\\b", "press": "name=OK" } ] }""");
        var set = provider.Load();
        Assert.Equal(5, set.Rules.Count);
        Assert.NotNull(set.Match("anything", "Info box", new[] { "Done loading" }));
        Assert.Null(set.Match("anything", "Info box", new[] { "Not done" }));

        File.WriteAllText(provider.UserRulesPath, "[]");
        File.SetLastWriteTimeUtc(provider.UserRulesPath, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(4, provider.Load().Rules.Count);
    }

    [Fact]
    public void Rules_ForbiddenTitles_Keys_AndBadInput_AreRejectedWithErrors()
    {
        var set = DialogRuleSet.Parse("""
            [
              { "id": "unsigned", "titleContains": "Security - Unsigned Add-In", "press": "id=CommandLink_1001" },
              { "id": "license", "titleContains": "License Manager", "press": "name=OK" },
              { "id": "recovery", "titleContains": "File Recovery", "press": "name=Close" },
              { "id": "save", "titleContains": "Save changes?", "press": "name=No" },
              { "id": "vi", "titleContains": "Xóa phần tử", "press": "name=OK" },
              { "id": "keys", "titleContains": "Info" },
              { "id": "no title", "press": "name=OK" },
              { "id": "bad regex", "titleContains": "Info", "bodyPattern": "([", "press": "name=OK" },
              { "id": "good", "titleContains": "Info", "press": "name=OK" }
            ]
            """, "test.json");
        Assert.Equal(new[] { "good" }, set.Rules.Select(r => r.Id));
        Assert.Equal(8, set.Errors.Count);
        Assert.Contains(set.Errors, e => e.Contains("'unsigned'") && e.Contains("forbidden"));
        Assert.Contains(set.Errors, e => e.Contains("'keys'") && e.Contains("press"));

        Assert.Single(DialogRuleSet.Parse("not json", "x.json").Errors);
        Assert.Single(DialogRuleSet.Parse("{\"a\": 1}", "x.json").Errors);
    }

    [Fact]
    public void ButtonMatcher_ResolvesIdsLabelsAndSelectors()
    {
        var d = new DialogDetails
        {
            Buttons = new[]
            {
                new DialogButton { Text = "Yes", Id = "6" },
                new DialogButton { Text = "Close", Id = "CommandButton_8" },
                new DialogButton { Text = "Ignore and continue opening the project\nnote", Id = "CommandLink_1002" },
            },
        };
        Assert.Equal("6", Assert.Single(ButtonMatcher.Find(d, "6")).Id);
        Assert.Equal("6", Assert.Single(ButtonMatcher.Find(d, "&yes")).Id);
        Assert.Equal("CommandButton_8", Assert.Single(ButtonMatcher.Find(d, "id=commandbutton_8")).Id);
        Assert.Equal("CommandButton_8", Assert.Single(ButtonMatcher.Find(d, "name=Close")).Id);
        Assert.Equal("CommandLink_1002", Assert.Single(ButtonMatcher.Find(d, "Ignore and continue opening the project")).Id);
        Assert.Empty(ButtonMatcher.Find(d, "id=Close"));
        Assert.Empty(ButtonMatcher.Find(d, "name=6"));
        Assert.Empty(ButtonMatcher.Find(d, ""));
    }

    [Theory]
    [InlineData("Autodesk Revit 2024.2 - [Project1 - Floor Plan: Level 1]", "Project1", "Floor Plan: Level 1")]
    [InlineData("Autodesk Revit 2025 - rac_sample.rvt - 3D View: {3D}", "rac_sample.rvt", "3D View: {3D}")]
    [InlineData("Autodesk Revit 2024", null, null)]
    [InlineData(null, null, null)]
    public void GuessDocument_ParsesTheMainWindowTitle(string? title, string? doc, string? view)
    {
        var (d, v) = RevitProcessService.GuessDocument(title);
        Assert.Equal(doc, d);
        Assert.Equal(view, v);
    }

    [Theory]
    [InlineData("0x1A2B", 0x1A2B)]
    [InlineData("6699", 6699)]
    [InlineData("1A2B", 0x1A2B)]
    public void TryParseHwnd_AcceptsHexAndDecimal(string text, long expected)
    {
        Assert.True(Win32Windows.TryParseHwnd(text, out var h));
        Assert.Equal(expected, h.ToInt64());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("-5")]
    [InlineData("zz")]
    public void TryParseHwnd_RejectsGarbage(string text) => Assert.False(Win32Windows.TryParseHwnd(text, out _));

    [Fact]
    public void StripMnemonic_HandlesAmpersands()
    {
        Assert.Equal("Yes", Win32Windows.StripMnemonic("&Yes"));
        Assert.Equal("Save & Close", Win32Windows.StripMnemonic("Save && &Close"));
    }

    [Fact]
    public async Task BridgePipes_SeesAPipeWithoutConnectingToIt()
    {
        var fakePid = 900000 + Random.Shared.Next(90000);
        await using var server = new NamedPipeServerStream(BridgePipeNames.ForProcess(fakePid), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connect = server.WaitForConnectionAsync();

        var pids = BridgePipes.ListPids();
        Assert.NotNull(pids);
        Assert.Contains(fakePid, pids!);
        Assert.True(BridgePipes.IsAvailable(fakePid));
        Assert.False(BridgePipes.IsAvailable(fakePid + 1));

        await Task.Delay(200);
        Assert.False(connect.IsCompleted, "listing the pipe namespace must not connect to the Bridge pipe");
    }
}
