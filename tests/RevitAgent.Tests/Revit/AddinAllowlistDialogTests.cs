using RevitAgent.Tests.Ui;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Revit;

/// <summary>
/// The allowlist against a REAL TaskDialog titled "Security - Unsigned Add-In" with command buttons 1001 / 1002 / 1003, owned by a
/// helper process this test starts. "Owned" = registered in the LaunchedProcessRegistry like a Revit started by revit_launch.
/// </summary>
[Collection(DialogProcessCollection.Name)]
public class AddinAllowlistDialogTests
{
    private const string Title = "Security - Unsigned Add-In";
    private const string Text = "Name:                MyAddin.App|Location:            D:\\Work\\MyProject\\wt-ticket-1\\Outcome\\MyAddin.dll";
    private const string ListedPattern = @"D:\Work\MyProject\*\Outcome\MyAddin.dll";

    private static readonly ReadinessOptions Options = new() { Timeout = TimeSpan.FromSeconds(60), Settle = TimeSpan.FromSeconds(1) };

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Owned_Listed_PressesLoadOnce_ThenReady_WithAuditAndEvent()
    {
        using var host = DialogHostProcess.UnsignedAddin(Title, Text);
        host.WaitForDialog(Title);
        await using var sp = TestGateway.BuildProvider(out var paths);
        var entry = sp.GetRequiredService<AddinAllowlistStore>().Add(new AddinAllowlistEntry { Name = "MyAddin.App", PathPattern = ListedPattern });
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var events = new List<string>();
        sp.GetRequiredService<GatewayEvents>().Status += (cat, msg) => events.Add(cat + ": " + msg);

        // The dialog is read through UIA first: it really is a TaskDialog with the three command buttons.
        var b = Assert.Single(sp.GetRequiredService<IRevitReadiness>().GetState(host.Pid).Blockers);
        Assert.Equal(DialogCategories.Forbidden, b.Category);
        Assert.Contains(b.Buttons!, x => x.Id == "CommandButton_1001");
        Assert.Contains(b.Buttons!, x => x.Id == "CommandButton_1002");
        Assert.Contains(b.Buttons!, x => x.Id == "CommandButton_1003");

        var result = await sp.GetRequiredService<IRevitReadiness>().WaitReadyAsync(host.Pid, Options, CancellationToken.None);

        Assert.True(result.Ready, result.Reason);
        Assert.Equal("1002", host.WaitForResult());
        var h = Assert.Single(result.Handled);
        Assert.True(h.DialogClosed, h.Error);
        Assert.Equal("tdm_click_button", h.Method);
        Assert.Equal("CommandButton_1002", h.ButtonId);
        Assert.Equal("MyAddin.App", h.AddinTrust!.Name);
        Assert.Equal(@"D:\Work\MyProject\wt-ticket-1\Outcome\MyAddin.dll", h.AddinTrust.Location);
        Assert.Equal(entry.Id, h.AddinTrust.MatchedEntryId);
        Assert.Equal("load_once", h.AddinTrust.Decision);
        Assert.NotEmpty(result.Screenshots);

        var audit = Assert.Single(sp.GetRequiredService<AuditLog>().Recent(), e => e.Tool == "addin_trust");
        Assert.True(audit.Ok);
        Assert.Contains(entry.Id, audit.Args);
        Assert.Contains("MyAddin.App", audit.Args);
        Assert.Contains(File.ReadAllLines(Directory.GetFiles(paths.AuditDir, "*.jsonl").Single()), l => l.Contains("addin_trust"));
        Assert.Contains(events, e => e.StartsWith("addin_trust:") && e.Contains("Load Once"));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task Owned_Unlisted_PressesDoNotLoad()
    {
        using var host = DialogHostProcess.UnsignedAddin(Title, Text);
        host.WaitForDialog(Title);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<AddinAllowlistStore>().Add(new AddinAllowlistEntry { Name = "Another.App", PathPattern = ListedPattern });
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);

        var result = await sp.GetRequiredService<IRevitReadiness>().WaitReadyAsync(host.Pid, Options, CancellationToken.None);

        Assert.True(result.Ready, result.Reason);
        Assert.Equal("1003", host.WaitForResult());
        var h = Assert.Single(result.Handled);
        Assert.Equal("do_not_load", h.AddinTrust!.Decision);
        Assert.Null(h.AddinTrust.MatchedEntryId);
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task NotOwned_IsNeverTouched_EvenWhenListed()
    {
        using var host = DialogHostProcess.UnsignedAddin(Title, Text);
        host.WaitForDialog(Title);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<AddinAllowlistStore>().Add(new AddinAllowlistEntry { Name = "MyAddin.App", PathPattern = ListedPattern });

        var result = await sp.GetRequiredService<IRevitReadiness>().WaitReadyAsync(host.Pid, Options, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Empty(result.Handled);
        Assert.Equal(DialogCategories.Forbidden, Assert.Single(result.Blockers).Category);
        Assert.Null(host.WaitForResult(2));
        Assert.DoesNotContain(sp.GetRequiredService<AuditLog>().Recent(), e => e.Tool == "addin_trust");
    }

    [DesktopTheory] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    [InlineData("Name:                MyAddin.App|(no location line)")]                                            // parse failure
    [InlineData("Name:  MyAddin.App|Location:  D:\\Work\\MyProject\\x\\Outcome\\MyAddin.dll|Your license has expired.")] // other forbidden category
    public async Task Owned_Unclear_StaysHumanOnly(string text)
    {
        using var host = DialogHostProcess.UnsignedAddin(Title, text);
        host.WaitForDialog(Title);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<AddinAllowlistStore>().Add(new AddinAllowlistEntry { Name = "MyAddin.App", PathPattern = ListedPattern });
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);

        var result = await sp.GetRequiredService<IRevitReadiness>().WaitReadyAsync(host.Pid, Options, CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(GatewayErrorCodes.ModalBlocked, result.Code);
        Assert.Empty(result.Handled);
        Assert.Null(host.WaitForResult(2));
    }

    [DesktopFact] // real dialog windows: skipped with REVITAGENT_SKIP_UI_TESTS=1
    public async Task RevitHandleDialog_StillRefuses_EvenForOwnedListedAddin()
    {
        using var host = DialogHostProcess.UnsignedAddin(Title, Text);
        var dialog = host.WaitForDialog(Title);
        await using var sp = TestGateway.BuildProvider(out _);
        sp.GetRequiredService<AddinAllowlistStore>().Add(new AddinAllowlistEntry { Name = "MyAddin.App", PathPattern = ListedPattern });
        sp.GetRequiredService<LaunchedProcessRegistry>().Register(host.Process);
        var readiness = sp.GetRequiredService<IRevitReadiness>();

        foreach (var button in new[] { "CommandButton_1002", "CommandButton_1003", "Load Once" })
        {
            var ex = Assert.Throws<GatewayException>(() => readiness.HandleDialog(host.Pid, dialog.Hwnd, button, discardChanges: true, TimeSpan.FromSeconds(1)));
            Assert.Equal(GatewayErrorCodes.PermissionDenied, ex.Code);
        }

        Assert.True(Win32Windows.IsVisible(dialog.Hwnd));
        Assert.Null(host.WaitForResult(1));
    }
}
