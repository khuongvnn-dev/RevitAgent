using RevitAgent.Tests.Ui;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Revit;

/// <summary>
/// launched.json ownership and revit_close. Every process here is a helper the test starts itself
/// (powershell.exe); the user's Revit is never targeted.
/// </summary>
[Collection(DialogProcessCollection.Name)]
public class ProcessOwnershipAndCloseTests
{
    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public void Registry_OwnsRegisteredProcess_SurvivesRestart_AndPrunesWhenItExits()
    {
        using var dummy = DialogHostProcess.Sleeper();
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var registry = new LaunchedProcessRegistry(paths);
        Assert.False(registry.IsOwned(dummy.Pid));

        var entry = registry.Register(dummy.Process, year: 2024);
        Assert.Equal(dummy.Pid, entry.Pid);
        Assert.True(registry.IsOwned(dummy.Pid));
        Assert.True(File.Exists(Path.Combine(paths.Root, LaunchedProcessRegistry.FileName)));

        // A new registry instance (= Host restart) reads the same file.
        var afterRestart = new LaunchedProcessRegistry(paths);
        Assert.True(afterRestart.IsOwned(dummy.Pid));
        Assert.Equal(2024, Assert.Single(afterRestart.List()).Year);

        dummy.Process.Kill();
        dummy.Process.WaitForExit(10000);
        Assert.False(afterRestart.IsOwned(dummy.Pid));
        Assert.Empty(afterRestart.List());
        Assert.DoesNotContain(dummy.Pid.ToString(), File.ReadAllText(afterRestart.FilePath));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public void Registry_IgnoresEntriesWhoseStartTimeDiffers_AndCorruptFiles()
    {
        using var dummy = DialogHostProcess.Sleeper();
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var file = Path.Combine(paths.Root, LaunchedProcessRegistry.FileName);

        // Same pid, different start time = pid reuse: not ours.
        var stale = new[] { new LaunchedProcess { Pid = dummy.Pid, StartTimeUtc = DateTime.UtcNow.AddDays(-3), ExePath = "C:\\x\\Revit.exe" } };
        File.WriteAllText(file, JsonSerializer.Serialize(stale, GatewayJson.Options));
        var registry = new LaunchedProcessRegistry(paths);
        Assert.False(registry.IsOwned(dummy.Pid));
        Assert.Empty(registry.List());

        File.WriteAllText(file, "{ not json");
        Assert.False(new LaunchedProcessRegistry(paths).IsOwned(dummy.Pid));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_RefusesForeignProcesses_WithoutTouchingThem()
    {
        using var foreign = DialogHostProcess.Sleeper();
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();

        async Task<ToolResult> Close(string json) => await registry.InvokeAsync("revit_close", JsonDocument.Parse(json).RootElement, "test", CancellationToken.None);

        var plain = await Close($$"""{"pid": {{foreign.Pid}}}""");
        Assert.Equal(GatewayErrorCodes.PermissionDenied, plain.ErrorCode);
        Assert.Contains("not launched by the gateway", ((TextContent)plain.Content[0]).Text);

        var noConfirm = await Close($$"""{"pid": {{foreign.Pid}}, "force_foreign": true}""");
        Assert.Equal(GatewayErrorCodes.PermissionDenied, noConfirm.ErrorCode);

        var wrongConfirm = await Close($$"""{"pid": {{foreign.Pid}}, "force_foreign": true, "confirm_pid": {{foreign.Pid + 1}}}""");
        Assert.Equal(GatewayErrorCodes.PermissionDenied, wrongConfirm.ErrorCode);

        // Confirmed, but not a Revit.exe → still refused.
        var notRevit = await Close($$"""{"pid": {{foreign.Pid}}, "force_foreign": true, "confirm_pid": {{foreign.Pid}}}""");
        Assert.Equal(GatewayErrorCodes.ValidationFailed, notRevit.ErrorCode);

        Assert.False(foreign.HasExited);

        var missing = await Close("""{"pid": 2147483000}""");
        Assert.Equal(GatewayErrorCodes.NotFound, missing.ErrorCode);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_OwnedProcessWithSavePrompt_IsBlockedWithoutDiscard_ThenAnswersDontSave()
    {
        const string title = "Autodesk Revit Close Test";
        using var host = DialogHostProcess.SavePromptForm(title, "Do you want to save changes to Project1.rvt?");
        host.WaitForWindow(DialogHostProcess.MainTitle);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var processes = sp.GetRequiredService<RevitProcessService>();

        var blocked = await Assert.ThrowsAsync<GatewayException>(() => processes.CloseAsync(new CloseRequest { Pid = host.Pid, Grace = TimeSpan.FromSeconds(20) }, CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.ModalBlocked, blocked.Code);
        Assert.Contains("discard_changes", blocked.Message);
        Assert.False(host.HasExited);
        host.WaitForDialog(title, 5); // the prompt is still open, unanswered
        Assert.Null(host.WaitForResult(1));

        var closed = await processes.CloseAsync(new CloseRequest { Pid = host.Pid, DiscardChanges = true, Grace = TimeSpan.FromSeconds(20) }, CancellationToken.None);
        Assert.True(closed.Closed);
        Assert.Equal("graceful", closed.Method);
        var h = Assert.Single(closed.Handled);
        Assert.Equal("No", h.Button);
        Assert.Equal("revit_close(discard_changes)", h.By);
        Assert.Equal("No", host.WaitForResult());
        Assert.True(host.Process.WaitForExit(10000));
        Assert.False(sp.GetRequiredService<LaunchedProcessRegistry>().IsOwned(host.Pid));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task CloseTool_WithDiscardChanges_ClosesGracefully()
    {
        using var host = DialogHostProcess.SavePromptForm("Autodesk Revit Tool Close", "Save changes to Model.rvt?");
        host.WaitForWindow(DialogHostProcess.MainTitle);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var registry = sp.GetRequiredService<ToolRegistry>();

        var result = await registry.InvokeAsync("revit_close", JsonDocument.Parse($$"""{"pid": {{host.Pid}}, "discard_changes": true, "grace_sec": 20}""").RootElement, "test", CancellationToken.None);

        Assert.False(result.IsError, ((TextContent)result.Content[0]).Text);
        Assert.Equal("graceful", result.Structured!.Value.GetProperty("method").GetString());
        Assert.Equal("No", host.WaitForResult());
        Assert.True(host.Process.WaitForExit(10000));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_NonSaveDialogDuringClose_IsModalBlocked_AndRevitStaysOpen()
    {
        const string title = "RevitAgent Close Question";
        using var host = DialogHostProcess.SavePromptForm(title, "Synchronize with central before closing?");
        host.WaitForWindow(DialogHostProcess.MainTitle);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var processes = sp.GetRequiredService<RevitProcessService>();

        var ex = await Assert.ThrowsAsync<GatewayException>(() => processes.CloseAsync(new CloseRequest { Pid = host.Pid, DiscardChanges = true, Grace = TimeSpan.FromSeconds(20) }, CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.ModalBlocked, ex.Code);
        Assert.False(host.HasExited);
        Assert.Null(host.WaitForResult(1));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_OwnedProcessThatIgnoresWmClose_IsKilledAfterGrace()
    {
        using var host = DialogHostProcess.SavePromptForm("none", "none", mode: "ignoreclose");
        host.WaitForWindow(DialogHostProcess.MainTitle);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var processes = sp.GetRequiredService<RevitProcessService>();

        var sw = Stopwatch.StartNew();
        var closed = await processes.CloseAsync(new CloseRequest { Pid = host.Pid, Grace = TimeSpan.FromSeconds(2) }, CancellationToken.None);
        Assert.Equal("killed_after_grace", closed.Method);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(2));
        Assert.True(host.HasExited);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_OwnedProcessWithoutWindow_IsKilled_AndForceKillWorks()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<LaunchedProcessRegistry>();
        var processes = sp.GetRequiredService<RevitProcessService>();

        using (var noWindow = DialogHostProcess.Sleeper())
        {
            registry.Register(noWindow.Process);
            var closed = await processes.CloseAsync(new CloseRequest { Pid = noWindow.Pid, Grace = TimeSpan.FromSeconds(2) }, CancellationToken.None);
            Assert.Equal("killed_after_grace", closed.Method); // waited the grace period for a main window first
            Assert.Contains("No main window", closed.Note);
            Assert.True(noWindow.HasExited);
        }

        using var form = DialogHostProcess.SavePromptForm("none", "none", mode: "ignoreclose");
        form.WaitForWindow(DialogHostProcess.MainTitle);
        registry.Register(form.Process);
        var killed = await processes.CloseAsync(new CloseRequest { Pid = form.Pid, ForceKill = true }, CancellationToken.None);
        Assert.Equal("killed", killed.Method);
        Assert.True(form.HasExited);
        Assert.Empty(registry.List());
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_OwnedProcess_UpdatePromptWhileClosing_PressesLater_AndRevitExits()
    {
        // Like the MyAddin update prompt on Revit exit (live 2026-11-26): command links "Cập nhật" (1001) / "Để sau" (1002).
        const string title = "MyAddin.App - MyProject";
        using var host = DialogHostProcess.CloseCommandLinks(title, "Phiên bản mới 1.2.0.1 (hiện tại 1.2.0.0).", "Cập nhật|Để sau");
        host.WaitForWindow(DialogHostProcess.MainTitle);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);

        var closed = await sp.GetRequiredService<RevitProcessService>().CloseAsync(new CloseRequest { Pid = host.Pid, Grace = TimeSpan.FromSeconds(30) }, CancellationToken.None);

        Assert.Equal("graceful", closed.Method);
        var h = Assert.Single(closed.Handled);
        Assert.Equal("Để sau", h.Button);
        Assert.Equal("safe_dismiss: revit_close", h.By);
        Assert.Equal("revit_close", h.SafeDismiss?.Operation);
        Assert.Equal("1002", host.WaitForResult());
        Assert.True(host.Process.WaitForExit(10000));
        Assert.Contains(sp.GetRequiredService<RevitAgent.Core.Audit.AuditLog>().Recent(), e => e.Tool == "safe_dismiss" && e.Args.Contains("revit_close"));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Close_OwnedProcess_ReviewWarningsLeftOpen_IsClosedWithClose_ThenRevitCloses()
    {
        const string title = "RevitAgent Review Warnings Close Test";
        using var host = DialogHostProcess.ModalForm(title, "Highlighted walls overlap.", "Show|More Info|!Delete Checked...|Expand|Export...|Close");
        host.WaitForWindow(title);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);

        var closed = await sp.GetRequiredService<RevitProcessService>().CloseAsync(new CloseRequest { Pid = host.Pid, Grace = TimeSpan.FromSeconds(30) }, CancellationToken.None);

        Assert.Equal("graceful", closed.Method);
        var h = Assert.Single(closed.Handled);
        Assert.Equal("Close", h.Button);
        Assert.Equal("safe_dismiss: revit_close", h.By);
        Assert.Equal("Close", host.WaitForResult());
        Assert.True(host.Process.WaitForExit(10000));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task SafeDismiss_ForeignProcess_PressesNothing()
    {
        // A harmless dialog with a Cancel button, but the process was not launched by the gateway: nothing is pressed.
        const string title = "RevitAgent Foreign Notice";
        using var host = DialogHostProcess.MessageBox(title, "A new version of the add-in is available.", "OKCancel", mode: "stay");
        var dialog = host.WaitForDialog(title);
        await using var sp = TestGateway.BuildProvider(out _);
        var readiness = sp.GetRequiredService<RevitReadiness>();

        var raw = Assert.Single(readiness.Detect(host.Pid).Blockers);
        Assert.Null(readiness.TrySafeDismiss(host.Pid, raw, "revit_close"));
        Assert.Empty(readiness.SafeDismissDialogs(host.Pid, "doc_open"));
        Assert.Null(host.WaitForResult(1));
        Assert.Equal(dialog.Hwnd, Assert.Single(readiness.Detect(host.Pid).Blockers).Window.Hwnd);
    }
}
