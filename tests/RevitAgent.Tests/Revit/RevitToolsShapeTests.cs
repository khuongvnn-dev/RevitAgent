using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Revit;

/// <summary>Registration, permissions and result shapes. Never launches Revit and never targets a running one.</summary>
public class RevitToolsShapeTests
{
    [Theory]
    [InlineData("revit_list_installed", ToolPermission.Read)]
    [InlineData("revit_list_instances", ToolPermission.Read)]
    [InlineData("revit_get_state", ToolPermission.Read)]
    [InlineData("revit_list_dialogs", ToolPermission.Read)]
    [InlineData("revit_wait_ready", ToolPermission.Interact)]
    [InlineData("revit_handle_dialog", ToolPermission.Interact)]
    [InlineData("revit_launch", ToolPermission.Admin)]
    [InlineData("revit_close", ToolPermission.Admin)]
    [InlineData("sandbox_create_run", ToolPermission.Write)]
    [InlineData("sandbox_copy_model", ToolPermission.Write)]
    [InlineData("sandbox_list_runs", ToolPermission.Read)]
    public async Task Tool_IsRegistered_WithHonestPermission_AndAnObjectSchema(string name, ToolPermission permission)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        Assert.True(registry.TryGet(name, out var tool));
        Assert.Equal(permission, tool.Permission);
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.True(tool.Description.Length > 80, "descriptions are written for an AI agent");
    }

    [Fact]
    public async Task ListInstances_ReturnsShapeOnly_AndSendsNothing()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var result = await registry.InvokeAsync("revit_list_instances", JsonDocument.Parse("""{"limit": 5}""").RootElement, "test", CancellationToken.None);

        Assert.False(result.IsError);
        var body = result.Structured!.Value;
        Assert.True(body.GetProperty("count").GetInt32() >= 0);
        foreach (var i in body.GetProperty("instances").EnumerateArray())
        {
            Assert.True(i.GetProperty("pid").GetInt32() > 0);
            Assert.False(string.IsNullOrEmpty(i.GetProperty("state").GetString()));
            Assert.False(i.GetProperty("launched_by_gateway").GetBoolean()); // fresh temp home owns nothing
        }
    }

    [Fact]
    public async Task ListInstalled_ReturnsShape()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var result = await registry.InvokeAsync("revit_list_installed", null, "test", CancellationToken.None);

        Assert.False(result.IsError);
        var body = result.Structured!.Value;
        Assert.Equal(body.GetProperty("count").GetInt32(), body.GetProperty("installs").GetArrayLength());
        foreach (var i in body.GetProperty("installs").EnumerateArray())
        {
            Assert.InRange(i.GetProperty("year").GetInt32(), RevitInstallations.MinYear, RevitInstallations.MaxYear);
            Assert.EndsWith("Revit.exe", i.GetProperty("exe_path").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(i.TryGetProperty("bridge_manifest_installed", out _));
        }
    }

    [Theory]
    [InlineData("""{"year": 2024, "args": ["C:\\models\\a.rvt"]}""", GatewayErrorCodes.ValidationFailed)] // models only via sandbox copies + doc_open
    [InlineData("""{"year": 2024, "args": ["journal.txt"]}""", GatewayErrorCodes.ValidationFailed)]
    [InlineData("""{"year": 2099}""", GatewayErrorCodes.ValidationFailed)]
    [InlineData("""{}""", GatewayErrorCodes.ValidationFailed)]
    [InlineData("""{"exe_path": "C:\\Windows\\notepad.exe"}""", GatewayErrorCodes.ValidationFailed)]
    public async Task Launch_ValidatesBeforeStartingAnything(string args, string code)
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var result = await registry.InvokeAsync("revit_launch", JsonDocument.Parse(args).RootElement, "test", CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(sp.GetRequiredService<LaunchedProcessRegistry>().List());
    }

    [Fact]
    public async Task HandleDialog_RejectsBadHwnd_AndForeignWindows()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var bad = await registry.InvokeAsync("revit_handle_dialog", JsonDocument.Parse("""{"pid": 4, "hwnd": "nope", "button": "OK"}""").RootElement, "test", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, bad.ErrorCode);

        // This test process is neither Revit.exe nor gateway-launched → refused before anything is read or clicked.
        var self = Environment.ProcessId;
        foreach (var (tool, json) in new[]
        {
            ("revit_handle_dialog", $$"""{"pid": {{self}}, "hwnd": "0x7FFFFFF0", "button": "OK"}"""),
            ("revit_get_state", $$"""{"pid": {{self}}}"""),
            ("revit_list_dialogs", $$"""{"pid": {{self}}}"""),
            ("revit_wait_ready", $$"""{"pid": {{self}}, "timeout_sec": 1}"""),
        })
        {
            var r = await registry.InvokeAsync(tool, JsonDocument.Parse(json).RootElement, "test", CancellationToken.None);
            Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
        }
    }

    [Fact]
    public async Task GetState_UnknownPid_ReportsExited()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registry = sp.GetRequiredService<ToolRegistry>();
        var result = await registry.InvokeAsync("revit_get_state", JsonDocument.Parse("""{"pid": 2147483000}""").RootElement, "test", CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("exited", result.Structured!.Value.GetProperty("state").GetString());
        Assert.False(result.Structured!.Value.GetProperty("ready").GetBoolean());
    }
}
