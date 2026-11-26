using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Machine;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Machine;

public class MachineToolTests
{
    [Fact]
    public void Tool_PropertiesAndSchema_MatchContract()
    {
        var readiness = new MachineReadiness();
        var tool = new MachineCheckUnattendedTool(readiness);

        Assert.Equal("machine_check_unattended", tool.Name);
        Assert.Equal(ToolPermission.Read, tool.Permission);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));

        var schema = tool.InputSchema;
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("year", out var yearProp));
        Assert.Equal("integer", yearProp.GetProperty("type").GetString());
        Assert.Equal(2024, yearProp.GetProperty("default").GetInt32());
    }

    [Fact]
    public async Task Tool_AutoDiscoveredByGatewayServices_AndCallable()
    {
        await using var sp = TestGateway.BuildProvider(out var paths);

        var registry = sp.GetRequiredService<ToolRegistry>();
        var tool = registry.Tools.FirstOrDefault(t => t.Name == "machine_check_unattended");

        Assert.NotNull(tool);
        Assert.IsType<MachineCheckUnattendedTool>(tool);
        Assert.Equal(ToolPermission.Read, tool.Permission);

        // Execute the tool through registry
        var call = new ToolCall("machine_check_unattended", JsonDocument.Parse("{\"year\": 2024}").RootElement);
        var result = await registry.InvokeAsync(call, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.Structured);

        var structured = result.Structured.Value;
        Assert.True(structured.TryGetProperty("ready_for_unattended", out var readyProp));
        Assert.True(readyProp.ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(2024, structured.GetProperty("year").GetInt32());
        Assert.True(structured.GetProperty("items").GetArrayLength() >= 13);
    }
}
