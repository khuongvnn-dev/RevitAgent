using System.Runtime.InteropServices;
using RevitAgent.Tests.Ui;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Revit;

/// <summary>
/// Telegram escalation of forbidden dialogs against REAL dialogs of a helper process registered as "gateway-launched", with a fake
/// <see cref="IHumanEscalation"/>: answer → that exact button is pressed; timeout → the safe button; no safe button → nothing;
/// dialog closed in Revit → question withdrawn; not owned / not opted in → never asked.
/// </summary>
[Collection(DialogProcessCollection.Name)]
public class EscalationDialogTests
{
    private const string DeleteTitle = "Delete Elements";
    private const string DeleteText = "Are you sure you want to delete the 3 selected elements?";

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private sealed class FakeEscalation : IHumanEscalation
    {
        public Func<HumanQuestion, CancellationToken, Task<HumanAnswer>> Handler { get; set; } = (_, _) => throw new InvalidOperationException("not expected");

        public List<HumanQuestion> Asked { get; } = new();

        public TimeSpan? DefaultTimeout { get; init; }

        public Task<HumanAnswer> AskAsync(HumanQuestion q, CancellationToken ct = default)
        {
            Asked.Add(q);
            return Handler(q, ct);
        }
    }

    private static HumanAnswer Answer(string choice, bool timedOut = false, string? reason = null) =>
        new(choice, timedOut ? "system" : "owner", timedOut, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, timedOut ? reason ?? HumanAnswerReasons.Timeout : null);

    private static RevitReadiness Build(ServiceProvider sp, AgentPaths paths, FakeEscalation fake) => new(
        paths, sp.GetRequiredService<DialogRuleProvider>(), sp.GetRequiredService<LaunchedProcessRegistry>(), sp.GetRequiredService<GatewayEvents>(),
        sp.GetRequiredService<AddinAllowlist>(), sp.GetRequiredService<AuditLog>(), fake);

    private static ReadinessOptions Options(bool escalate = true) =>
        new() { Timeout = TimeSpan.FromSeconds(60), Settle = TimeSpan.FromSeconds(1), EscalateForbiddenDialogs = escalate };

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Answer_PressesThatExactButton_AndRecordsHumanDecision()
    {
        using var host = DialogHostProcess.TaskDialog(DeleteTitle, DeleteText);   // Yes / No / Close
        host.WaitForDialog(DeleteTitle);
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var events = new List<string>();
        sp.GetRequiredService<GatewayEvents>().Status += (c, m) => events.Add(c + ": " + m);
        var fake = new FakeEscalation { Handler = (q, _) => Task.FromResult(Answer("Yes")) };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.True(result.Ready || result.State == RevitStates.Exited, result.Reason);   // the TaskDialog helper process ends with its dialog
        Assert.Equal("6", host.WaitForResult());   // IDYES: the owner's choice, although it is "dangerous"
        var q = Assert.Single(fake.Asked);
        Assert.Equal(DeleteTitle, q.Title);
        Assert.Contains("delete", q.Text);
        Assert.Contains("No", q.Options);
        Assert.Equal("No", q.SafeOption);
        Assert.True(q.Timeout > TimeSpan.Zero && q.Timeout <= ReadinessOptions.HumanWaitBudgetFor(fake));
        var h = Assert.Single(result.Handled);
        Assert.Equal("telegram", h.Human!.Via);
        Assert.Equal("Yes", h.Human.Choice);
        Assert.Equal("owner", h.Human.AnsweredBy);
        Assert.False(h.Human.TimedOut);
        Assert.Null(h.Error);
        var audit = Assert.Single(sp.GetRequiredService<AuditLog>().Recent(), e => e.Tool == "human_dialog_decision");
        Assert.Contains("Yes", audit.Args);
        Assert.Contains(events, e => e.StartsWith("human_dialog:") && e.Contains("Yes"));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Owner_GetsTheConfiguredQuestionTime_AndTheReadinessClockPausesWhileAsking()
    {
        // Before: the question timeout was capped by what was left of the readiness wait (here 3 s, not the owner's 30 min),
        // and an answer that took longer than the wait ended it with TARGET_NOT_READY.
        using var host = DialogHostProcess.TaskDialog(DeleteTitle, DeleteText);
        host.WaitForDialog(DeleteTitle);
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation
        {
            DefaultTimeout = TimeSpan.FromMinutes(30),
            Handler = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct); // the owner takes longer than the whole readiness timeout
                return Answer("No");
            },
        };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options() with { Timeout = TimeSpan.FromSeconds(3) }, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(fake.Asked).Timeout);
        Assert.True(result.Ready || result.State == RevitStates.Exited, result.Reason);
        Assert.Equal("7", host.WaitForResult());   // IDNO: the owner's answer was pressed after the 3 s wait "expired"
        Assert.Equal("No", Assert.Single(result.Handled).Human!.Choice);
    }

    [Fact]
    public void EscalatingLaunchAndWait_GetTheOwnerTimeOnTopOfTheirBudget()
    {
        var fake = new FakeEscalation { DefaultTimeout = TimeSpan.FromMinutes(10) };
        var call = new ToolCall("x", System.Text.Json.JsonSerializer.SerializeToElement(new { escalate_dialogs = true }), "t");
        var plain = new ToolCall("x", System.Text.Json.JsonSerializer.SerializeToElement(new { }), "t");

        var launch = new RevitAgent.Core.Revit.Tools.RevitLaunchTool(null!, fake);
        Assert.Equal(launch.Timeout + TimeSpan.FromMinutes(21), launch.TimeoutFor(call));
        Assert.Equal(launch.Timeout, launch.TimeoutFor(plain));

        var wait = new RevitAgent.Core.Revit.Tools.RevitWaitReadyTool(null!, null!, null!, fake);
        Assert.Equal(wait.Timeout + TimeSpan.FromMinutes(21), wait.TimeoutFor(call));
        Assert.Equal(TimeSpan.FromMinutes(61), ReadinessOptions.HumanWaitBudgetFor(null)); // unknown question time: 2 × 30 min + 1
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Timeout_PressesTheSafeButton()
    {
        using var host = DialogHostProcess.TaskDialog(DeleteTitle, DeleteText);
        host.WaitForDialog(DeleteTitle);
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation { Handler = (q, _) => Task.FromResult(Answer(q.SafeOption, timedOut: true)) };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.True(result.Ready || result.State == RevitStates.Exited, result.Reason);
        Assert.Equal("7", host.WaitForResult());   // IDNO
        var h = Assert.Single(result.Handled);
        Assert.True(h.Human!.TimedOut);
        Assert.Equal("No", h.Human.Choice);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Timeout_NoSafeButton_PressesNothing_AndStaysBlocked()
    {
        using var host = DialogHostProcess.MessageBox("Licensing", "Your license has expired.", "OK");
        host.WaitForDialog("Licensing");
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation { Handler = (q, _) => Task.FromResult(Answer(q.SafeOption, timedOut: true)) };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Empty(result.Handled);
        Assert.Equal(string.Empty, Assert.Single(fake.Asked).SafeOption);
        Assert.Null(host.WaitForResult(2));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Timeout_LicenseWithCancel_PressesCancel_NeverOk()
    {
        using var host = DialogHostProcess.MessageBox("Licensing", "Your license has expired.", "OKCancel");
        host.WaitForDialog("Licensing");
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation { Handler = (q, _) => Task.FromResult(Answer(q.SafeOption, timedOut: true)) };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.True(result.Ready, result.Reason);
        Assert.Equal("Cancel", host.WaitForResult());
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task OwnerNeverReached_TelegramOff_PressesNothing()
    {
        using var host = DialogHostProcess.MessageBox("Licensing", "Your license has expired.", "OKCancel");
        host.WaitForDialog("Licensing");
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation { Handler = (q, _) => Task.FromResult(Answer(q.SafeOption, timedOut: true, reason: "Telegram channel is disabled or unconfigured")) };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Empty(result.Handled);
        Assert.Null(host.WaitForResult(2));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task AnswerNotAmongTheButtons_PressesNothing()
    {
        using var host = DialogHostProcess.MessageBox("Licensing", "Your license has expired.", "OKCancel");
        host.WaitForDialog("Licensing");
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation { Handler = (q, _) => Task.FromResult(Answer("Retry")) };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Empty(result.Handled);
        Assert.Null(host.WaitForResult(2));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task DialogClosedInRevit_WithdrawsTheQuestion_AndContinues()
    {
        using var host = DialogHostProcess.MessageBox("Licensing", "Your license has expired.", "OKCancel");
        var dialog = host.WaitForDialog("Licensing");
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var cancelled = new TaskCompletionSource<string?>();
        var fake = new FakeEscalation();
        fake.Handler = async (q, ct) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                PostMessage(dialog.Hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);   // the owner answers in Revit himself (here: closes the window)
            });
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult(q.CancelNote?.Invoke());
                throw;
            }

            return Answer("Cancel");
        };

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.True(result.Ready, result.Reason);
        Assert.Empty(result.Handled);
        Assert.Equal("Đã được xử lý trực tiếp trong Revit.", await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task NotOwned_NeverAsked()
    {
        using var host = DialogHostProcess.TaskDialog(DeleteTitle, DeleteText);
        host.WaitForDialog(DeleteTitle);
        await using var sp = TestGateway.BuildProvider(out var paths);
        var fake = new FakeEscalation();

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Empty(fake.Asked);
        Assert.Null(host.WaitForResult(2));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task NotOptedIn_NeverAsked()
    {
        using var host = DialogHostProcess.TaskDialog(DeleteTitle, DeleteText);
        host.WaitForDialog(DeleteTitle);
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var fake = new FakeEscalation();

        var result = await Build(sp, paths, fake).WaitReadyAsync(host.Pid, Options(escalate: false), CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Empty(fake.Asked);
        Assert.Null(host.WaitForResult(2));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task RevitHandleDialog_StillRefusesForbidden_EvenWhenOwned()
    {
        using var host = DialogHostProcess.TaskDialog(DeleteTitle, DeleteText);
        var dialog = host.WaitForDialog(DeleteTitle);
        await using var sp = TestGateway.BuildProvider(out var paths);
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var readiness = Build(sp, paths, new FakeEscalation());

        var ex = Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, "No", discardChanges: false, TimeSpan.FromSeconds(1)));
        Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        Assert.Null(host.WaitForResult(1));
    }
}

