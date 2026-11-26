using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>
/// doc_open waits on Revit for minutes; an informational dialog (e.g. 'Unresolved References') used to block it until a
/// human clicked (seen live 2026-11-25: 9 min). The forwarding tool now answers rule-matched dialogs while it waits.
/// </summary>
public class DocOpenDialogWatchTests
{
    private const int Pid = 616161;

    [Fact]
    public async Task DocOpen_AnswersRuleMatchedDialogsWhileWaiting_AndListsThemInTheResult()
    {
        var readiness = new WatchOnlyReadiness(new HandledDialog { Hwnd = "0xABC", Title = "Unresolved References", Button = "Ignore and continue", By = "rule: Unresolved References -> Ignore and continue", Method = "tdm_click_button", DialogClosed = true });
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(5.5)); // the model is "opening": long enough for three watcher ticks (1.5 s each)
            return "{\"id\":\"x\",\"ok\":true,\"result\":{\"opened\":true}}";
        });

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out var paths, pipe, configure: s => s.AddSingleton<IRevitReadiness>(readiness));
        var copy = Path.Combine(paths.RunsDir, "r1", "model.rvt");
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = copy }), "t", CancellationToken.None);
        await server;

        Assert.False(r.IsError, (r.Content[0] as TextContent)?.Text);
        var result = r.Structured!.Value;
        Assert.True(result.GetProperty("opened").GetBoolean());
        var handled = result.GetProperty("handled_dialogs");
        Assert.True(handled.GetArrayLength() is >= 1 and <= 2, handled.ToString()); // at most two tries per dialog window
        Assert.Equal("Unresolved References", handled[0].GetProperty("title").GetString());
        Assert.All(readiness.Pids, p => Assert.Equal(Pid, p));
        Assert.Contains(readiness.Skips, skip => skip.Contains("0xABC")); // after two tries the window is skipped
    }

    [Fact]
    public async Task ToolsWithoutDialogWatch_NeverLookForDialogs()
    {
        var readiness = new WatchOnlyReadiness(new HandledDialog { Hwnd = "0x1", Title = "x" });
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            return "{\"id\":\"x\",\"ok\":true,\"result\":{\"title\":\"m\"}}";
        });

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _, pipe, configure: s => s.AddSingleton<IRevitReadiness>(readiness));
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("doc_info", JsonSerializer.SerializeToElement(new { pid = Pid }), "t", CancellationToken.None);
        await server;

        Assert.False(r.IsError);
        Assert.False(r.Structured!.Value.TryGetProperty("handled_dialogs", out _));
        Assert.Empty(readiness.Pids);
    }

    [Fact]
    public async Task DocOpen_SafeDismissesHarmlessDialogsWhileWaiting_AfterTheRules_AndListsThem()
    {
        var readiness = new DismissingReadiness(
            rule: new HandledDialog { Hwnd = "0xA1", Title = "Unresolved References", Button = "Ignore and continue", By = "rule: Unresolved References -> Ignore and continue", DialogClosed = true },
            dismissed: new HandledDialog
            {
                Hwnd = "0xB2", Title = "MyAddin.App - MyProject", Button = "Để sau", ButtonId = "CommandLink_1002", By = "safe_dismiss: doc_open",
                Method = "tdm_click_button", DialogClosed = true, SafeDismiss = new SafeDismissInfo { Operation = "doc_open", Text = "update available" },
            });
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(4)); // long enough for two watcher ticks (1.5 s each)
            return "{\"id\":\"x\",\"ok\":true,\"result\":{\"opened\":true}}";
        });

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out var paths, pipe, configure: s => s.AddSingleton<IRevitReadiness>(readiness));
        var copy = Path.Combine(paths.RunsDir, "r1", "model.rvt");
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = copy }), "t", CancellationToken.None);
        await server;

        Assert.False(r.IsError, (r.Content[0] as TextContent)?.Text);
        var handled = r.Structured!.Value.GetProperty("handled_dialogs").EnumerateArray().ToList();
        var dismissed = Assert.Single(handled, h => h.GetProperty("by").GetString() == "safe_dismiss: doc_open");
        Assert.Equal("Để sau", dismissed.GetProperty("button").GetString());
        Assert.Equal("doc_open", dismissed.GetProperty("safe_dismiss").GetProperty("operation").GetString());
        Assert.Contains(handled, h => h.GetProperty("hwnd").GetString() == "0xA1");

        Assert.NotEmpty(readiness.DismissCalls);
        Assert.All(readiness.DismissCalls, c => Assert.Equal((Pid, "doc_open"), (c.Pid, c.Operation)));
        Assert.Contains("0xA1", readiness.DismissCalls.First().Skip); // a rule just answered it: not dismissed in the same pass
    }

    [Fact]
    public async Task DocOpen_FailsAfterASafeDismiss_TheErrorDetailsListIt()
    {
        var readiness = new DismissingReadiness(
            rule: null,
            dismissed: new HandledDialog { Hwnd = "0xC3", Title = "Add-in", Button = "Cancel", By = "safe_dismiss: doc_open", DialogClosed = true, SafeDismiss = new SafeDismissInfo { Operation = "doc_open" } });
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var server = BridgeClientProtocolTests.RunFakeBridge(pipe, (_, _) =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(2.5));
            return "{\"id\":\"x\",\"ok\":false,\"error\":{\"code\":\"CONFLICT\",\"message\":\"The open was cancelled.\",\"details\":{\"running_tool\":\"doc_open\"}}}";
        });

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out var paths, pipe, configure: s => s.AddSingleton<IRevitReadiness>(readiness));
        var copy = Path.Combine(paths.RunsDir, "r1", "model.rvt");
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("doc_open", JsonSerializer.SerializeToElement(new { pid = Pid, path = copy }), "t", CancellationToken.None);
        await server;

        Assert.True(r.IsError);
        Assert.Equal("CONFLICT", r.ErrorCode);
        var details = r.Structured!.Value.GetProperty("details");
        Assert.Equal("safe_dismiss: doc_open", details.GetProperty("handled_dialogs")[0].GetProperty("by").GetString());
        Assert.Equal("doc_open", details.GetProperty("error_details").GetProperty("running_tool").GetString());
    }

    /// <summary>Rule pass returns <paramref name="rule"/> on the first tick only; the safe-dismiss pass returns <paramref name="dismissed"/> once.</summary>
    private sealed class DismissingReadiness(HandledDialog? rule, HandledDialog dismissed) : IRevitReadiness
    {
        private int _ruleTicks;
        private int _dismissTicks;

        public ConcurrentQueue<(int Pid, string Operation, HashSet<string> Skip)> DismissCalls { get; } = new();

        public IReadOnlyList<HandledDialog> AnswerRuledDialogs(int pid, IReadOnlySet<string>? skipHwnds = null) =>
            rule is not null && Interlocked.Increment(ref _ruleTicks) == 1 ? new[] { rule } : Array.Empty<HandledDialog>();

        public IReadOnlyList<HandledDialog> SafeDismissDialogs(int pid, string operation, IReadOnlySet<string>? skipHwnds = null)
        {
            DismissCalls.Enqueue((pid, operation, skipHwnds is null ? new HashSet<string>() : new HashSet<string>(skipHwnds)));
            return Interlocked.Increment(ref _dismissTicks) == 1 ? new[] { dismissed } : Array.Empty<HandledDialog>();
        }

        public RevitSnapshot Detect(int pid) => throw new NotSupportedException();

        public RevitState GetState(int pid, bool includeDetails = true, string? screenshotDir = null) => throw new NotSupportedException();

        public Task<ReadinessResult> WaitReadyAsync(int pid, ReadinessOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DialogClickResult HandleDialog(int pid, IntPtr hwnd, string button, bool discardChanges, TimeSpan verifyTimeout, string by = "revit_handle_dialog") => throw new NotSupportedException();

        public void EnsureNoBlockers(int pid)
        {
        }

        public IDisposable AcquireActionLock(int pid) => new MemoryStream();
    }

    /// <summary>Only the watcher entry point is real: returns <paramref name="dialog"/> on every tick unless its hwnd is skipped.</summary>
    private sealed class WatchOnlyReadiness(HandledDialog dialog) : IRevitReadiness
    {
        public ConcurrentQueue<int> Pids { get; } = new();

        public ConcurrentQueue<HashSet<string>> Skips { get; } = new();

        public IReadOnlyList<HandledDialog> AnswerRuledDialogs(int pid, IReadOnlySet<string>? skipHwnds = null)
        {
            Pids.Enqueue(pid);
            Skips.Enqueue(skipHwnds is null ? new HashSet<string>() : new HashSet<string>(skipHwnds));
            return skipHwnds?.Contains(dialog.Hwnd) == true ? Array.Empty<HandledDialog>() : new[] { dialog };
        }

        public RevitSnapshot Detect(int pid) => throw new NotSupportedException();

        public RevitState GetState(int pid, bool includeDetails = true, string? screenshotDir = null) => throw new NotSupportedException();

        public Task<ReadinessResult> WaitReadyAsync(int pid, ReadinessOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DialogClickResult HandleDialog(int pid, IntPtr hwnd, string button, bool discardChanges, TimeSpan verifyTimeout, string by = "revit_handle_dialog") => throw new NotSupportedException();

        public void EnsureNoBlockers(int pid)
        {
        }

        public IDisposable AcquireActionLock(int pid) => new MemoryStream();
    }
}
