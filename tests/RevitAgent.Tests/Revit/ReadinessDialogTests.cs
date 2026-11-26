using RevitAgent.Tests.Ui;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Revit;

/// <summary>Serializes the tests that start helper processes with real windows (one dialog at a time on the desktop).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DialogProcessCollection
{
    public const string Name = "revit-dialog-processes";
}

/// <summary>
/// Readiness gate + dialog handling against REAL dialogs owned by helper processes this test starts
/// (never against any other process). Each test kills its helper in Dispose.
/// </summary>
[Collection(DialogProcessCollection.Name)]
public class ReadinessDialogTests
{
    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task MessageBoxOwnedByForm_IsDetected_WithTextsButtonsAndDisabledMainWindow()
    {
        using var host = DialogHostProcess.MessageBox("RevitAgent Detect Test", "Body line of the test dialog");
        var dialog = host.WaitForDialog("RevitAgent Detect Test");
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<RevitReadiness>();

        var snap = readiness.Detect(host.Pid);
        Assert.True(snap.Alive);
        Assert.NotNull(snap.Main);
        Assert.Equal(DialogHostProcess.MainTitle, snap.Main!.Title);
        Assert.False(snap.Main.Enabled);
        var raw = Assert.Single(snap.Blockers);
        Assert.Equal(BlockerKinds.Dialog, raw.Kind);
        Assert.Equal(dialog.Hwnd, raw.Window.Hwnd);

        var state = readiness.GetState(host.Pid);
        Assert.False(state.Ready);
        Assert.Equal("modal", state.State);
        Assert.True(state.Blocked);
        Assert.Single(state.BlockingDialogs);
        Assert.False(state.MainWindowEnabled);
        Assert.True(state.Responding);
        var b = Assert.Single(state.Blockers);
        Assert.Equal("RevitAgent Detect Test", b.Title);
        Assert.Equal("#32770", b.Class);
        Assert.Contains("Body line of the test dialog", b.Text);
        Assert.Equal(DialogCategories.Unknown, b.Category);
        Assert.False(b.IsTaskDialog);
        Assert.NotNull(b.Buttons);
        Assert.Equal(new[] { "Yes", "No", "Cancel" }, b.Buttons!.Select(x => x.Text));
        Assert.Equal(new[] { "6", "7", "2" }, b.Buttons!.Select(x => x.Id));

        // Nothing was clicked by observing.
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopTheory] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    [InlineData("No", "No")]
    [InlineData("&yes", "Yes")]
    [InlineData("2", "Cancel")]
    public async Task HandleDialog_ClicksExactlyTheRequestedButton(string button, string expected)
    {
        using var host = DialogHostProcess.MessageBox("RevitAgent Click Test", "Pick one");
        var dialog = host.WaitForDialog("RevitAgent Click Test");
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var result = readiness.HandleDialog(host.Pid, dialog.Hwnd, button, discardChanges: false, TimeSpan.FromSeconds(5));

        Assert.Equal(expected, host.WaitForResult());
        Assert.True(result.Handled.DialogClosed);
        Assert.Equal("wm_command", result.Handled.Method);
        Assert.Null(result.Handled.Error);
        Assert.Empty(result.RemainingBlockers);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task HandleDialog_UnknownButton_IsNotFound_AndNothingIsClicked()
    {
        using var host = DialogHostProcess.MessageBox("RevitAgent Missing Button", "Pick one");
        var dialog = host.WaitForDialog("RevitAgent Missing Button");
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var ex = Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "Maybe", false, TimeSpan.FromSeconds(1)));
        Assert.Equal(GatewayErrorCodes.NotFound, ex.Code);
        Assert.True(Win32Windows.IsVisible(dialog.Hwnd));
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopTheory] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    [InlineData("Security - Unsigned Add-In", "The add-in is not signed. Load it?")]
    [InlineData("RevitAgent Test", "Your license has expired.")]
    [InlineData("Khôi phục tệp", "Bạn có muốn khôi phục bản vẽ?")]
    public async Task HandleDialog_RefusesForbiddenDialogs(string title, string text)
    {
        using var host = DialogHostProcess.MessageBox(title, text);
        var dialog = host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var state = readiness.GetState(host.Pid);
        Assert.Equal(DialogCategories.Forbidden, Assert.Single(state.Blockers).Category);

        var ex = Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "Yes", discardChanges: true, TimeSpan.FromSeconds(1)));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        Assert.Contains("human", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Win32Windows.IsVisible(dialog.Hwnd));
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task HandleDialog_SavePrompt_OnlyDontSaveWithDiscardChanges()
    {
        const string title = "Autodesk Revit Save Test";
        using var host = DialogHostProcess.MessageBox(title, "Do you want to save changes to Project1.rvt?");
        var dialog = host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var b = Assert.Single(readiness.GetState(host.Pid).Blockers);
        Assert.Equal(DialogCategories.Forbidden, b.Category);
        Assert.True(b.SavePrompt);

        Assert.Equal(GatewayErrorCodes.PermissionDenied, Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "No", false, TimeSpan.FromSeconds(1))).Code);

        // Not launched by the gateway: discard_changes is not honored (it may be the user's own work).
        var foreign = Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "No", true, TimeSpan.FromSeconds(1)));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, foreign.Code);
        Assert.Contains("launched", foreign.Message);

        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "Yes", true, TimeSpan.FromSeconds(1))).Code);
        Assert.Null(host.WaitForResult(1));

        var ok = readiness.HandleDialog(host.Pid, dialog.Hwnd, "No", discardChanges: true, TimeSpan.FromSeconds(5));
        Assert.True(ok.Handled.DialogClosed);
        Assert.Equal("No", host.WaitForResult());
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task TaskDialog_TextsAndIdsComeFromUia_AndTheClickUsesTdmClickButton()
    {
        const string title = "RevitAgent TaskDialog Test";
        using var host = DialogHostProcess.TaskDialog(title, "Content text of the task dialog");
        var dialog = host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var b = Assert.Single(readiness.GetState(host.Pid).Blockers);
        Assert.True(b.IsTaskDialog);
        Assert.Contains("Main instruction of the test", b.Text);
        Assert.Contains("Content text of the task dialog", b.Text);
        Assert.Contains(b.Buttons!, x => x.Id == "CommandButton_6" && x.Text == "Yes");
        Assert.Contains(b.Buttons!, x => x.Id == "CommandButton_7" && x.Text == "No");
        Assert.Contains(b.Buttons!, x => x.Id == "CommandButton_8" && x.Text == "Close");
        Assert.Equal(DialogCategories.Unknown, b.Category);

        var result = readiness.HandleDialog(host.Pid, dialog.Hwnd, "id=CommandButton_8", false, TimeSpan.FromSeconds(5));
        Assert.Equal("tdm_click_button", result.Handled.Method);
        Assert.True(result.Handled.DialogClosed);
        Assert.Equal("8", host.WaitForResult());
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task TaskDialog_ForbiddenContentReadThroughUia_IsRefused()
    {
        const string title = "RevitAgent TaskDialog Content";
        using var host = DialogHostProcess.TaskDialog(title, "The existing file will be overwritten.");
        var dialog = host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var b = Assert.Single(readiness.GetState(host.Pid).Blockers);
        Assert.Equal(DialogCategories.Forbidden, b.Category);
        Assert.Contains("overwrite", b.ForbiddenReason);

        var ex = Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "CommandButton_6", false, TimeSpan.FromSeconds(1)));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task WpfModalWindow_IsAModalWindowBlocker_ReadAndClickedThroughUia()
    {
        const string title = "RevitAgent WPF Modal Test";
        using var host = DialogHostProcess.WpfModal(title, "Apply the settings?");
        var modal = host.WaitForWindow(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var snap = readiness.Detect(host.Pid);
        Assert.Equal(DialogHostProcess.MainTitle, snap.Main!.Title);
        Assert.False(snap.Main.Enabled);
        var raw = Assert.Single(snap.Blockers);
        Assert.Equal(BlockerKinds.ModalWindow, raw.Kind);
        Assert.Equal(modal.Hwnd, raw.Window.Hwnd);

        var b = Assert.Single(readiness.GetState(host.Pid).Blockers);
        Assert.Equal("uia", b.DetailsSource);
        Assert.Contains("Apply the settings?", b.Text);
        Assert.Contains(b.Buttons!, x => x.Id == "btnApply" && x.Text == "Apply");
        Assert.Contains(b.Buttons!, x => x.Id == "btnCancel" && x.Text == "Cancel");
        Assert.Equal(DialogCategories.Unknown, b.Category);

        var result = readiness.HandleDialog(host.Pid, modal.Hwnd, "Apply", false, TimeSpan.FromSeconds(5));
        Assert.Equal("uia_invoke", result.Handled.Method);
        Assert.True(result.Handled.DialogClosed, result.Handled.Error);
        Assert.Equal("Apply", host.WaitForResult());
        Assert.Empty(result.RemainingBlockers);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task WaitReady_AnswersInformationalRule_ThenReportsReady()
    {
        const string title = "RevitAgent Info Rule Test";
        using var host = DialogHostProcess.MessageBox(title, "Just so you know.", "OK", mode: "stay");
        host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out var paths);
        File.WriteAllText(Path.Combine(paths.ConfigDir, DialogRuleProvider.UserFileName), $$"""
            [ { "id": "test info -> OK", "titleContains": "{{title}}", "press": "name=OK" } ]
            """);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var result = await readiness.WaitReadyAsync(host.Pid, new ReadinessOptions { Timeout = TimeSpan.FromSeconds(40), Settle = TimeSpan.FromSeconds(1) }, CancellationToken.None);

        Assert.True(result.Ready, result.Reason);
        Assert.Equal("ready", result.State);
        var h = Assert.Single(result.Handled);
        Assert.Equal("rule: test info -> OK", h.By);
        Assert.Equal("OK", h.Button);
        Assert.True(h.DialogClosed);
        Assert.Equal("OK", host.WaitForResult());
        Assert.Null(result.RuleErrors);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task WaitReady_ForbiddenDialog_StopsAtOnce_WithScreenshot_AndNeverClicks()
    {
        const string title = "Licensing Error Test";
        using var host = DialogHostProcess.MessageBox(title, "Your license could not be validated.", "OKCancel");
        host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out var paths);
        // Even a (bad) user rule for it must not make the gate click it: the rule is rejected at load.
        File.WriteAllText(Path.Combine(paths.ConfigDir, DialogRuleProvider.UserFileName), $$"""
            [ { "id": "bad", "titleContains": "{{title}}", "press": "name=OK" } ]
            """);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var result = await readiness.WaitReadyAsync(host.Pid, new ReadinessOptions { Timeout = TimeSpan.FromSeconds(30), Settle = TimeSpan.FromSeconds(1) }, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal("modal", result.State);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Contains("user", result.NextStep, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Handled);
        Assert.True(result.WaitedSec < 10, $"forbidden dialogs must stop the gate immediately (waited {result.WaitedSec} s)");
        var shot = Assert.Single(result.Screenshots);
        Assert.True(File.Exists(shot));
        Assert.StartsWith(paths.CapturesDir, shot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DialogCategories.Forbidden, Assert.Single(result.Blockers).Category);
        Assert.Contains(result.RuleErrors!, e => e.Contains("'bad'", StringComparison.Ordinal));
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task WaitReady_UnknownDialog_IsBlockedAfterGrace_WithoutClicking()
    {
        const string title = "RevitAgent Unknown Dialog";
        using var host = DialogHostProcess.MessageBox(title, "Something happened.", "OK");
        host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var result = await readiness.WaitReadyAsync(host.Pid, new ReadinessOptions { Timeout = TimeSpan.FromSeconds(60), Settle = TimeSpan.Zero }, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.InRange(result.WaitedSec, 9.5, 30);
        Assert.Equal(DialogCategories.Unknown, Assert.Single(result.Blockers).Category);
        Assert.Contains("revit_handle_dialog", result.NextStep);
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task WaitReadyTool_ThroughRegistry_ReturnsModalBlockedError_WithDetails()
    {
        const string title = "Security Warning Tool Test";
        using var host = DialogHostProcess.MessageBox(title, "Unsigned code.", "OK");
        host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();

        var args = JsonDocument.Parse($$"""{"pid": {{host.Pid}}, "timeout_sec": 20, "settle_sec": 1}""").RootElement;

        // A non-Revit process the gateway did not launch is never a target.
        var notRevit = await registry.InvokeAsync("revit_wait_ready", args, "test", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.PermissionDenied, notRevit.ErrorCode);

        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var result = await registry.InvokeAsync("revit_wait_ready", args, "test", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.ErrorCode);
        var body = result.Structured!.Value;
        Assert.Equal("forbidden", body.GetProperty("details").GetProperty("blockers")[0].GetProperty("category").GetString());
        Assert.True(File.Exists(body.GetProperty("details").GetProperty("screenshots")[0].GetString()));

        var list = await registry.InvokeAsync("revit_list_dialogs", JsonDocument.Parse($$"""{"pid": {{host.Pid}}}""").RootElement, "test", CancellationToken.None);
        Assert.False(list.IsError);
        Assert.Equal(1, list.Structured!.Value.GetProperty("count").GetInt32());

        // revit_get_state result shape consumed by the scenario runner (package D).
        var get = await registry.InvokeAsync("revit_get_state", JsonDocument.Parse($$"""{"pid": {{host.Pid}}}""").RootElement, "test", CancellationToken.None);
        Assert.False(get.IsError);
        var s = get.Structured!.Value;
        Assert.Equal(host.Pid, s.GetProperty("pid").GetInt32());
        Assert.Equal("modal", s.GetProperty("state").GetString());
        Assert.False(s.GetProperty("ready").GetBoolean());
        Assert.True(s.GetProperty("blocked").GetBoolean());
        Assert.True(s.GetProperty("responding").GetBoolean());
        Assert.False(s.GetProperty("main_window_enabled").GetBoolean());
        Assert.True(s.TryGetProperty("bridge_pipe_available", out _));
        var d = s.GetProperty("blocking_dialogs")[0];
        foreach (var field in new[] { "hwnd", "title", "class", "text", "category", "buttons" })
        {
            Assert.True(d.TryGetProperty(field, out _), field);
        }

        Assert.Equal(title, d.GetProperty("title").GetString());
        Assert.Equal("forbidden", d.GetProperty("category").GetString());
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task HandleDialog_DisabledDestructiveButton_DoesNotMakeTheDialogForbidden_CloseIsPressed()
    {
        // Like Revit 2024 Manage > Review Warnings (live 2026-11-26): "Delete Checked..." is disabled, "Close" must be answerable.
        const string title = "RevitAgent Review Warnings Test";
        using var host = DialogHostProcess.ModalForm(title, "Highlighted walls overlap.", "Show|More Info|!Delete Checked...|Expand|Export...|Close");
        var dialog = host.WaitForWindow(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        var b = Assert.Single(readiness.GetState(host.Pid).BlockingDialogs);
        Assert.Equal(BlockerKinds.ModalWindow, b.Kind);
        Assert.Equal(DialogCategories.Unknown, b.Category);
        Assert.False(b.Buttons!.Single(x => x.Text == "Delete Checked...").Enabled);

        var click = readiness.HandleDialog(host.Pid, dialog.Hwnd, "Close", discardChanges: false, TimeSpan.FromSeconds(5));
        Assert.True(click.Handled.DialogClosed);
        Assert.Equal("Close", host.WaitForResult());
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task WaitReadyTool_OwnedRevit_HarmlessDialog_IsSafeDismissed_WithScreenshotAuditAndNotification()
    {
        const string title = "RevitAgent Harmless Notice";
        using var host = DialogHostProcess.MessageBox(title, "A new version of the add-in is available.", "OKCancel", mode: "stay");
        host.WaitForDialog(title);
        var notifier = new RevitAgent.Tests.Notify.FakeNotifier();
        await using var sp = new ServiceCollection()
            .AddSingleton<RevitAgent.Core.Notify.INotifier>(notifier)
            .AddRevitAgentGateway(new AgentPaths(TestGateway.NewTempDir("revitagent_home_")))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var registry = sp.GetRequiredService<ToolRegistry>();

        var result = await registry.InvokeAsync("revit_wait_ready", JsonDocument.Parse($$"""{"pid": {{host.Pid}}, "timeout_sec": 60, "settle_sec": 1}""").RootElement, "test", CancellationToken.None);

        Assert.False(result.IsError, ((TextContent)result.Content[0]).Text);
        var h = Assert.Single(result.Structured!.Value.GetProperty("handled").EnumerateArray());
        Assert.Equal("Cancel", h.GetProperty("button").GetString()); // never OK
        Assert.Equal("safe_dismiss: revit_wait_ready", h.GetProperty("by").GetString());
        Assert.Equal("revit_wait_ready", h.GetProperty("safe_dismiss").GetProperty("operation").GetString());
        Assert.True(File.Exists(h.GetProperty("safe_dismiss").GetProperty("screenshot_path").GetString()));
        Assert.Equal("Cancel", host.WaitForResult());

        var audit = Assert.Single(sp.GetRequiredService<RevitAgent.Core.Audit.AuditLog>().Recent(), e => e.Tool == "safe_dismiss");
        Assert.Contains("revit_wait_ready", audit.Args);
        Assert.Contains(title, audit.Args);
        var deadline = DateTime.UtcNow.AddSeconds(10); // the notification is fire and forget
        while (!notifier.Calls.Any(c => c.Kind == RevitAgent.Core.Notify.NotificationKind.DialogDismissed) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var sent = Assert.Single(notifier.Calls, c => c.Kind == RevitAgent.Core.Notify.NotificationKind.DialogDismissed);
        Assert.Contains(title, sent.Text);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task ReadyHost_WithoutDialogs_IsReady()
    {
        const string title = "RevitAgent Ready Test";
        using var host = DialogHostProcess.MessageBox(title, "Close me.", "OK", mode: "stay");
        var dialog = host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();
        readiness.HandleDialog(host.Pid, dialog.Hwnd, "OK", false, TimeSpan.FromSeconds(5));

        var result = await readiness.WaitReadyAsync(host.Pid, new ReadinessOptions { Timeout = TimeSpan.FromSeconds(20), Settle = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        Assert.True(result.Ready, result.Reason);
        readiness.EnsureNoBlockers(host.Pid);
        var state = readiness.GetState(host.Pid);
        Assert.True(state.Ready);
        Assert.Equal(DialogHostProcess.MainTitle, state.MainWindowTitle);
        Assert.False(state.LaunchedByGateway);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task ActionLock_IsExclusivePerPid()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<IRevitReadiness>();
        using (readiness.AcquireActionLock(424242))
        {
            Assert.Equal(GatewayErrorCodes.Busy, Assert.Throws<GatewayException>(() => readiness.AcquireActionLock(424242)).Code);
            using (readiness.AcquireActionLock(424243))
            {
            }
        }

        using (readiness.AcquireActionLock(424242))
        {
        }
    }
}
