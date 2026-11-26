using System.IO;
using RevitAgent.Core.Tools;
using RevitAgent.Host.Services;

namespace RevitAgent.Tests.Host;

/// <summary>Dashboard behaviour against fake tools: nothing here can reach a real Revit process.</summary>
public class DashboardViewModelTests
{
    private static async Task<HostRig> RigWithInstancesAsync(params RevitAgent.Core.Revit.RevitInstance[] instances)
    {
        var rig = new HostRig();
        rig.Instances.AddRange(instances);
        await rig.Dashboard.RefreshAsync();
        return rig;
    }

    [Fact]
    public async Task Refresh_ListsInstancesWithStateBridgeAndOwnership_AndNeverCallsATool()
    {
        using var rig = await RigWithInstancesAsync(
            HostRig.Instance(100, 2024, "ready", launchedByGateway: false, bridge: false),
            HostRig.Instance(200, 2025, "modal", launchedByGateway: true, bridge: true, document: "Tower"));

        var items = rig.Dashboard.RevitInstances;
        Assert.Equal(2, items.Count);
        Assert.Equal(100, items[0].Pid);
        Assert.Equal("ready", items[0].State);
        Assert.Equal("✗", items[0].BridgeText);
        Assert.Equal("✗", items[0].LaunchedText);
        Assert.Equal("Tower", items[1].Title);
        Assert.Equal("modal", items[1].State);
        Assert.Equal("✓", items[1].BridgeText);
        Assert.Equal("✓", items[1].LaunchedText);
        Assert.Equal(new IntPtr(0x1A2B), items[0].MainWindow);

        // Listing alone is allowed; acting on a process never happens without a user command.
        Assert.All(rig.Tools.Values, t => Assert.Empty(t.Calls));
        Assert.Empty(rig.Captured);
    }

    [Fact]
    public async Task Refresh_KeepsSelectionAcrossPolls_AndDropsItWhenRevitExits()
    {
        using var rig = await RigWithInstancesAsync(HostRig.Instance(100), HostRig.Instance(200));
        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[1];

        await rig.Dashboard.RefreshAsync();
        Assert.Equal(200, rig.Dashboard.SelectedRevitInstance?.Pid);

        rig.Instances.RemoveAll(i => i.Pid == 200);
        await rig.Dashboard.RefreshAsync();
        Assert.Null(rig.Dashboard.SelectedRevitInstance);
        Assert.Single(rig.Dashboard.RevitInstances);
    }

    [Fact]
    public async Task PollTick_RefreshesEveryThirdTick_WithoutBlockingTheCaller()
    {
        using var rig = new HostRig();
        rig.Instances.Add(HostRig.Instance(321));

        rig.Dashboard.OnPollTick();
        rig.Dashboard.OnPollTick();
        Assert.Empty(rig.Dashboard.RevitInstances);

        rig.Dashboard.OnPollTick();
        for (int i = 0; i < 100 && rig.Dashboard.RevitInstances.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(rig.Dashboard.RevitInstances);
    }

    [Fact]
    public async Task CloseRevit_IsDisabledForForeignRevit_AndNeverCallsTheTool()
    {
        using var rig = await RigWithInstancesAsync(HostRig.Instance(100, launchedByGateway: false));
        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];

        Assert.False(rig.Dashboard.CloseRevitCommand.CanExecute(null));
        Assert.Equal(new LocalizationService().GetString("Str_Tooltip_BtnCloseRevitForeign"), rig.Dashboard.CloseRevitTooltip);

        await rig.Dashboard.CloseRevitCommand.ExecuteAsync(null); // guarded inside as well
        Assert.Empty(rig.Tools["revit_close"].Calls);
    }

    [Fact]
    public async Task CloseRevit_IsEnabledOnlyForGatewayLaunched_AndCallsToolWithPidAndDashboardSession()
    {
        using var rig = await RigWithInstancesAsync(HostRig.Instance(555, launchedByGateway: true));
        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];

        Assert.True(rig.Dashboard.CloseRevitCommand.CanExecute(null));
        await rig.Dashboard.CloseRevitCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.Tools["revit_close"].Calls);
        Assert.Equal("dashboard", call.SessionId);
        Assert.Equal(555, call.GetInt("pid"));
    }

    [Fact]
    public async Task ListDialogs_CallsReadOnlyTool_AndShowsTheDialogs()
    {
        using var rig = new HostRig();
        rig.Tools["revit_list_dialogs"].Handler = _ => ToolResult.Json(new
        {
            count = 1,
            dialogs = new[]
            {
                new { hwnd = "0x10", title = "Unresolved References", text = "Some links are missing", category = "info", buttons = new[] { new { text = "Ignore" }, new { text = "Close" } } },
            },
        });
        rig.Instances.Add(HostRig.Instance(77, state: "modal"));
        await rig.Dashboard.RefreshAsync();
        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];

        await rig.Dashboard.ListDialogsCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.Tools["revit_list_dialogs"].Calls);
        Assert.Equal(77, call.GetInt("pid"));
        Assert.Equal("dashboard", call.SessionId);
        Assert.True(rig.Dashboard.HasDialogResults);
        var line = Assert.Single(rig.Dashboard.DialogResults);
        Assert.Contains("Unresolved References", line);
        Assert.Contains("Ignore", line);
        Assert.Contains(rig.Notifications.History, n => n.Message.Contains("77"));
        Assert.Empty(rig.Tools["revit_close"].Calls);
    }

    [Fact]
    public async Task ListDialogs_ToolError_ShowsAnErrorNotification()
    {
        using var rig = new HostRig();
        rig.Tools["revit_list_dialogs"].Handler = _ => ToolResult.Error(new GatewayException("NOT_FOUND", "Revit 9 is gone"));
        rig.Instances.Add(HostRig.Instance(9));
        await rig.Dashboard.RefreshAsync();
        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];

        await rig.Dashboard.ListDialogsCommand.ExecuteAsync(null);

        Assert.False(rig.Dashboard.HasDialogResults);
        Assert.Contains(rig.Notifications.History, n => n.Message.Contains("NOT_FOUND"));
    }

    [Fact]
    public async Task CaptureScreenshot_CapturesSelectedMainWindow_SavesUnderCapturesDir_AndShowsPreview()
    {
        using var rig = await RigWithInstancesAsync(HostRig.Instance(42, hwnd: "0xBEEF"));
        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];

        await rig.Dashboard.CaptureScreenshotCommand.ExecuteAsync(null);

        Assert.Equal(new IntPtr(0xBEEF), Assert.Single(rig.Captured));
        Assert.True(rig.Dashboard.HasScreenshot);
        Assert.NotNull(rig.Dashboard.ScreenshotPreview);
        var file = Assert.Single(Directory.GetFiles(rig.Paths.CapturesDir, "*.png"));
        Assert.Contains("revit42", Path.GetFileName(file));
    }

    [Fact]
    public async Task CaptureScreenshot_WithoutSelection_IsDisabled_AndWithoutWindowDoesNothing()
    {
        using var rig = await RigWithInstancesAsync(HostRig.Instance(42, hwnd: null));
        Assert.False(rig.Dashboard.CaptureScreenshotCommand.CanExecute(null));

        rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];
        await rig.Dashboard.CaptureScreenshotCommand.ExecuteAsync(null);

        Assert.Empty(rig.Captured);
        Assert.False(rig.Dashboard.HasScreenshot);
    }

    [Fact]
    public async Task InstalledVersions_AreReadThroughTheRegistry_WithBridgeStatus()
    {
        using var rig = new HostRig();
        rig.Tools["revit_list_installed"].Handler = _ => ToolResult.Json(new
        {
            count = 2,
            installs = new object[]
            {
                new { year = 2024, exe_path = @"C:\Revit 2024\Revit.exe", bridge_manifest_installed = true },
                new { year = 2026, exe_path = @"C:\Revit 2026\Revit.exe", bridge_manifest_installed = false },
            },
        });

        await rig.Dashboard.RefreshInstalledAsync();

        Assert.Equal(new[] { 2026, 2024 }, rig.Dashboard.InstalledVersions.Select(i => i.Year));
        Assert.Equal("✓", rig.Dashboard.InstalledVersions[1].BridgeText);
        Assert.Equal(2026, rig.Dashboard.SelectedInstalled?.Year);
        Assert.Equal("dashboard", Assert.Single(rig.Tools["revit_list_installed"].Calls).SessionId);
    }

    [Fact]
    public async Task InstalledVersions_AreReReadOnlyWhenABridgeManifestAppearsOrDisappears_NotEvery30s()
    {
        using var rig = new HostRig();
        var addins = TestGateway.NewTempDir("addins_2024_");
        var manifest = Path.Combine(addins, RevitAgent.Core.Revit.RevitProcessService.BridgeManifestName);
        rig.Tools["revit_list_installed"].Handler = _ => ToolResult.Json(new
        {
            count = 1,
            installs = new object[] { new { year = 2024, exe_path = @"C:\Revit 2024\Revit.exe", user_addins_dir = addins, bridge_manifest_installed = File.Exists(manifest) } },
        });

        await rig.Dashboard.InitializeAsync();
        async Task TicksAsync(int n)
        {
            for (var i = 0; i < n; i++)
            {
                rig.Dashboard.OnPollTick();
            }

            await Task.Delay(300);
        }

        await TicksAsync(90);
        Assert.Single(rig.Tools["revit_list_installed"].Calls); // only the initial read: nothing changed, no audit line every 30 s

        File.WriteAllText(manifest, "<RevitAddIns/>");
        await TicksAsync(30);
        Assert.Equal(2, rig.Tools["revit_list_installed"].Calls.Count);
        Assert.Equal("✓", Assert.Single(rig.Dashboard.InstalledVersions).BridgeText);

        await TicksAsync(60);
        Assert.Equal(2, rig.Tools["revit_list_installed"].Calls.Count);
    }

    [Fact]
    public void BridgeButtons_AreDisabledWithTooltip_WhenBridgeToolsAreNotRegistered()
    {
        using var rig = new HostRig(); // no bridge_install / bridge_uninstall in the registry
        rig.Dashboard.InstalledVersions.Add(new RevitAgent.Host.Models.InstalledRevitItem { Year = 2024 });
        rig.Dashboard.SelectedInstalled = rig.Dashboard.InstalledVersions[0];

        Assert.False(rig.Dashboard.AreBridgeToolsAvailable);
        Assert.False(rig.Dashboard.InstallBridgeCommand.CanExecute(null));
        Assert.False(rig.Dashboard.UninstallBridgeCommand.CanExecute(null));
        Assert.Equal("Bridge installer không có trong bản build này", rig.Dashboard.BridgeInstallTooltip);
        Assert.Equal(rig.Dashboard.BridgeInstallTooltip, rig.Dashboard.BridgeUninstallTooltip);

        var en = new LocalizationService();
        en.SetLanguage(LocalizationService.CultureEnglish);
        Assert.Equal("Bridge installer not available in this build", en.GetString("Str_Tooltip_BridgeUnavailable"));
    }

    [Fact]
    public async Task BridgeButtons_WhenToolsExist_CallThemWithTheSelectedYear()
    {
        using var rig = new HostRig(new[] { "revit_list_installed", "bridge_install", "bridge_uninstall" });
        rig.Dashboard.InstalledVersions.Add(new RevitAgent.Host.Models.InstalledRevitItem { Year = 2025 });
        rig.Dashboard.SelectedInstalled = rig.Dashboard.InstalledVersions[0];

        Assert.True(rig.Dashboard.AreBridgeToolsAvailable);
        Assert.True(rig.Dashboard.InstallBridgeCommand.CanExecute(null));

        await rig.Dashboard.InstallBridgeCommand.ExecuteAsync(null);
        await rig.Dashboard.UninstallBridgeCommand.ExecuteAsync(null);

        var install = Assert.Single(rig.Tools["bridge_install"].Calls);
        Assert.Equal("dashboard", install.SessionId);
        Assert.Equal("[2025]", install.Arguments.GetProperty("years").GetRawText());
        var uninstall = Assert.Single(rig.Tools["bridge_uninstall"].Calls);
        Assert.Equal("[2025]", uninstall.Arguments.GetProperty("years").GetRawText());
    }

    [Fact]
    public async Task LaunchRevit_CallsRevitLaunchWithSelectedYear_AndReportsResult()
    {
        using var rig = new HostRig();
        rig.Tools["revit_launch"].Handler = _ => ToolResult.Json(new { pid = 4321, year = 2024, ready = true, state = "ready" });
        rig.Dashboard.InstalledVersions.Add(new RevitAgent.Host.Models.InstalledRevitItem { Year = 2024 });
        rig.Dashboard.SelectedInstalled = rig.Dashboard.InstalledVersions[0];

        await rig.Dashboard.LaunchRevitCommand.ExecuteAsync(null);

        var call = Assert.Single(rig.Tools["revit_launch"].Calls);
        Assert.Equal(2024, call.GetInt("year"));
        Assert.Equal("dashboard", call.SessionId);
        Assert.False(rig.Dashboard.IsLaunching);
        Assert.Contains(rig.Notifications.History, n => n.Message.Contains("4321"));
    }

    [Fact]
    public void LaunchRevit_IsDisabledWithoutAnInstalledYear()
    {
        using var rig = new HostRig();
        Assert.False(rig.Dashboard.LaunchRevitCommand.CanExecute(null));
    }

    [Fact]
    public void ConnectCard_ClaudeCodeCommandAndJsonContainTheShimPath_AndAreCopiedToTheClipboard()
    {
        var baseDir = TestGateway.NewTempDir("revitagent_shim_");
        var shim = Path.Combine(baseDir, "RevitAgent.Shim.exe");
        File.WriteAllText(shim, "stub");
        using var rig = new HostRig(baseDirectory: baseDir);

        Assert.True(rig.Dashboard.IsShimAvailable);
        Assert.Equal(shim, rig.Dashboard.ShimPath);
        Assert.Equal($"claude mcp add revitagent -- \"{shim}\"", rig.Dashboard.ClaudeCodeCommand);
        Assert.Contains(shim.Replace("\\", "\\\\"), rig.Dashboard.ClaudeDesktopJson);
        using var doc = System.Text.Json.JsonDocument.Parse(rig.Dashboard.ClaudeDesktopJson);
        Assert.Equal(shim, doc.RootElement.GetProperty("mcpServers").GetProperty("revitagent").GetProperty("command").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("mcpServers").GetProperty("revitagent").GetProperty("args").GetArrayLength());

        rig.Dashboard.CopyClaudeCodeCommand.Execute(null);
        rig.Dashboard.CopyClaudeJsonCommand.Execute(null);
        Assert.Equal(new[] { rig.Dashboard.ClaudeCodeCommand, rig.Dashboard.ClaudeDesktopJson }, rig.Clipboard);
    }

    [Fact]
    public async Task ConnectCard_ShowsPipeAndSessionCount()
    {
        using var rig = new HostRig();
        await rig.Dashboard.RefreshAsync();

        Assert.Equal(0, rig.Dashboard.ActiveSessions);
        Assert.StartsWith(@"\\.\pipe\", rig.Dashboard.PipeFullName);
    }

    [Fact]
    public void ShimLocator_PrefersSiblingExe_ThenDevLayout_ElseNull()
    {
        var root = TestGateway.NewTempDir("revitagent_layout_");

        var hostBin = Path.Combine(root, "src", "RevitAgent.Host", "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(hostBin);
        Assert.Null(ShimLocator.Resolve(hostBin));

        var shimBin = Path.Combine(root, "src", "RevitAgent.Shim", "bin", "Debug", "net10.0-windows");
        Directory.CreateDirectory(shimBin);
        var devShim = Path.Combine(shimBin, "RevitAgent.Shim.exe");
        Assert.Equal(devShim, ShimLocator.DevLayoutCandidate(hostBin));
        File.WriteAllText(devShim, "stub");
        Assert.Equal(devShim, ShimLocator.Resolve(hostBin));

        var sibling = Path.Combine(hostBin, "RevitAgent.Shim.exe");
        File.WriteAllText(sibling, "stub");
        Assert.Equal(sibling, ShimLocator.Resolve(hostBin));
    }

    [Fact]
    public async Task DashboardNeverCallsAnyTool_ByItselfAfterInitialize_ExceptListingInstalledVersions()
    {
        using var rig = new HostRig();
        rig.Instances.Add(HostRig.Instance(1, launchedByGateway: true));
        rig.Instances.Add(HostRig.Instance(2));

        await rig.Dashboard.InitializeAsync();
        for (int i = 0; i < 9; i++)
        {
            rig.Dashboard.OnPollTick();
        }

        await Task.Delay(150);

        foreach (var name in new[] { "revit_launch", "revit_close", "revit_list_dialogs" })
        {
            Assert.Empty(rig.Tools[name].Calls);
        }

        Assert.Empty(rig.Captured);
    }
}
