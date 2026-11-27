using System.IO;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;
using RevitAgent.Host.Config;

namespace RevitAgent.Tests.Host;

public class SettingsPolicyTests
{
    [Fact]
    public void SaveConfig_AppliesMaxPermissionAndSandboxToGatewayPolicy_AndPersistsThem()
    {
        using var rig = new HostRig();
        var policy = new GatewayPolicy();
        var vm = rig.NewSettings(policy);
        Assert.Equal(ToolPermission.Admin, policy.MaxPermission);
        Assert.True(policy.EnforceSandbox);
        Assert.Equal(new[] { "Read", "Interact", "Write", "Admin" }, vm.PermissionOptions);

        vm.SelectedMaxPermission = "Interact";
        vm.EnforceSandbox = false;
        vm.SaveConfigCommand.Execute(null);

        Assert.True(vm.IsConfigStatusSuccess);
        Assert.Equal(ToolPermission.Interact, policy.MaxPermission);
        Assert.False(policy.EnforceSandbox);

        var loaded = rig.Config.LoadConfig();
        Assert.Equal("Interact", loaded.GatewayMaxPermission);
        Assert.False(loaded.GatewayEnforceSandbox);

        // A fresh policy + view model (next start) picks the persisted values up again.
        var nextPolicy = new GatewayPolicy();
        var next = rig.NewSettings(nextPolicy);
        Assert.Equal("Interact", next.SelectedMaxPermission);
        Assert.False(next.EnforceSandbox);
        Assert.Equal(ToolPermission.Interact, nextPolicy.MaxPermission);
        Assert.False(nextPolicy.EnforceSandbox);
    }

    [Fact]
    public void GatewayPolicySettings_Apply_UsesConfigAndFailsClosedToReadForUnknownText()
    {
        var policy = new GatewayPolicy { MaxPermission = ToolPermission.Read, EnforceSandbox = false };
        GatewayPolicySettings.Apply(new AppConfig { GatewayMaxPermission = "write", GatewayEnforceSandbox = true }, policy);
        Assert.Equal(ToolPermission.Write, policy.MaxPermission);
        Assert.True(policy.EnforceSandbox);

        GatewayPolicySettings.Apply(new AppConfig { GatewayMaxPermission = "nonsense" }, policy);
        Assert.Equal(ToolPermission.Read, policy.MaxPermission);

        GatewayPolicySettings.Apply(new AppConfig { GatewayMaxPermission = "7" }, policy);
        Assert.Equal(ToolPermission.Read, policy.MaxPermission);

        // Null or empty text keeps the deliberate default.
        Assert.Equal(ToolPermission.Admin, GatewayPolicySettings.ParsePermission(null));
        Assert.Equal(ToolPermission.Admin, GatewayPolicySettings.ParsePermission(""));
    }

    [Fact]
    public void AllowedInputProcesses_DefaultEmpty_AreParsedAppliedAndPersisted()
    {
        Assert.Empty(new AppConfig().GatewayAllowedInputProcesses);
        Assert.Empty(new GatewayPolicy().AllowedInputProcesses);
        Assert.Equal(new[] { "notepad", "Foo", "bar" }, GatewayPolicySettings.ParseProcessList(" notepad, Foo.exe;\nbar , NOTEPAD.EXE,"));

        using var rig = new HostRig();
        var policy = new GatewayPolicy();
        var vm = rig.NewSettings(policy);
        Assert.Equal(string.Empty, vm.AllowedInputProcesses);

        vm.AllowedInputProcesses = "notepad.exe, mspaint";
        vm.SaveConfigCommand.Execute(null);
        Assert.True(vm.IsConfigStatusSuccess);
        Assert.Equal(new[] { "notepad", "mspaint" }, policy.AllowedInputProcesses);
        Assert.Equal(new[] { "notepad", "mspaint" }, rig.Config.LoadConfig().GatewayAllowedInputProcesses);

        var nextPolicy = new GatewayPolicy();
        var next = rig.NewSettings(nextPolicy);
        Assert.Equal("notepad, mspaint", next.AllowedInputProcesses);
        Assert.True(nextPolicy.IsInputProcessAllowed("MSPaint.exe"));
    }

    [Fact]
    public async Task LoweredMaxPermission_IsEnforcedByTheToolRegistry()
    {
        using var rig = new HostRig();
        var vm = rig.NewSettings(rig.Policy);
        vm.SelectedMaxPermission = "Read";
        vm.SaveConfigCommand.Execute(null);

        var result = await rig.Registry.InvokeAsync("revit_close", null, "test", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(rig.Tools["revit_close"].Calls);
    }
}
