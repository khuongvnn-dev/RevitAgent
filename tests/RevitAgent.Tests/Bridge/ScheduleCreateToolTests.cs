using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>
/// schedule_create as the gateway exposes it: schema, Host-side argument checks, the sandbox check of the target document
/// (doc_info probe, then <c>document</c> pinned to the verified path) and the action guard for a Revit the gateway did not launch.
/// The Revit side (field matching, transaction, activation) needs a live Revit.
/// </summary>
public class ScheduleCreateToolTests
{
    private const int Pid = 616161; // the fake Revit of BridgeGatewayToolsTests.BuildGateway

    [Fact]
    public async Task Schema_PermissionAndRequiredArguments()
    {
        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _);
        Assert.True(sp.GetRequiredService<ToolRegistry>().TryGet("schedule_create", out var tool));
        Assert.Equal(ToolPermission.Write, tool.Permission);
        var schema = tool.InputSchema;
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "category", "fields", "pid" }, required.Order());
        var props = schema.GetProperty("properties");
        Assert.Equal("array", props.GetProperty("fields").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("fields").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("array", props.GetProperty("sort_by").GetProperty("type").GetString());
        Assert.False(props.GetProperty("sort_descending").GetProperty("default").GetBoolean());
        Assert.True(props.GetProperty("activate").GetProperty("default").GetBoolean());
        Assert.Equal("string", props.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("document").GetProperty("type").GetString());

        // Descriptions reach every model: keep them short (description + parameter text; pid is shared by every Bridge tool).
        var parameterText = props.EnumerateObject().Where(p => p.Name != "pid").Sum(p => p.Value.GetProperty("description").GetString()!.Length);
        Assert.True(tool.Description.Length + parameterText <= 700, $"schedule_create text is {tool.Description.Length + parameterText} chars");
    }

    [Fact]
    public void SchedulableCategoriesDescription_PointsToScheduleCreate_WithoutAnInventedExample()
    {
        var description = BridgeToolCatalog.All.Single(d => d.Name == "schedule_list_schedulable_categories").Description;
        Assert.Contains("check.schedulable=false", description);
        Assert.Contains("model_query_elements", description);
        Assert.Contains("schedule_create", description);
        Assert.DoesNotContain("Levels", description);
    }

    public static TheoryData<string> InvalidArguments => new()
    {
        """{"fields":["Name"]}""",
        """{"category":"OST_Levels"}""",
        """{"category":"OST_Levels","fields":[]}""",
        """{"category":"OST_Levels","fields":[""]}""",
        """{"category":"OST_Levels","fields":[1]}""",
        """{"category":"OST_Levels","fields":{"a":1}}""",
        """{"category":"OST_Levels","fields":["Name","name"]}""",
        """{"category":"OST_Levels","fields":["Name"],"sort_by":["Elevation"]}""",
        """{"category":"OST_Levels","fields":["Name"],"sort_by":["Name","NAME"]}""",
        """{"category":"OST_Levels","fields":["Name"],"activate":"maybe"}""",
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidArguments_AreRefusedBeforeReachingRevit(string json)
    {
        // No fake Bridge listens on this pipe: reaching it would be DEPENDENCY_FAILED, not VALIDATION_FAILED.
        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        args["pid"] = JsonSerializer.SerializeToElement(Pid);
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync("schedule_create", JsonSerializer.SerializeToElement(args), "t", CancellationToken.None);
        Assert.Equal(GatewayErrorCodes.ValidationFailed, r.ErrorCode);
    }

    [Theory]
    [InlineData("""{"title":"original","path":"C:\\Projects\\original.rvt","is_active":true}""")]
    [InlineData("""{"title":"central","path":"\\\\server\\share\\central.rvt"}""")]
    [InlineData("""{"title":"Project1","path":""}""")] // never saved, not opened by doc_open
    [InlineData("""{"title":"model_detached","path":"","opened_from":"C:\\Projects\\model.rvt"}""")]
    public async Task DocumentsThatAreNotSandboxCopies_AreRefused_AndNeverChanged(string docInfo)
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var requests = new ConcurrentQueue<(string Tool, JsonElement Args)>();
        using var stop = new CancellationTokenSource();
        var server = RunScriptedBridge(pipe, 2, (_, _) => docInfo, requests, stop.Token);

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _, pipe);
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync(
            "schedule_create",
            JsonSerializer.SerializeToElement(new { pid = Pid, document = "original", category = "OST_Levels", fields = new[] { "Name" } }),
            "t",
            CancellationToken.None);
        stop.Cancel();
        await Task.WhenAny(server, Task.Delay(2000));

        Assert.Equal(GatewayErrorCodes.SandboxViolation, r.ErrorCode);
        Assert.Contains("sandbox copies only", (r.Content[0] as TextContent)?.Text);
        var calls = requests.ToArray();
        Assert.Single(calls);
        Assert.Equal("doc_info", calls[0].Tool);
        Assert.Equal("original", calls[0].Args.GetProperty("document").GetString()); // the agent's document key is what gets checked
    }

    [Fact]
    public async Task SandboxCopy_IsForwarded_WithTheDocumentPinnedToItsVerifiedPath()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var requests = new ConcurrentQueue<(string Tool, JsonElement Args)>();
        string? copy = null;
        var server = RunScriptedBridge(pipe, 2, (tool, _) => tool == "doc_info"
            ? JsonSerializer.Serialize(new { title = "model", path = copy, is_active = true })
            : """{"view_id":4242,"name":"Level Schedule","category":{"id":-2000240,"name":"Levels","builtin":"OST_Levels"},"fields":[{"name":"Name","heading":"Name"}],"sort":[{"field":"Name","descending":true}],"activated":true}""",
            requests);

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out var paths, pipe);
        copy = Path.Combine(paths.RunsDir, "r1", "sub", "..", "model.rvt");
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync(
            "schedule_create",
            JsonSerializer.SerializeToElement(new { pid = Pid, category = "OST_Levels", fields = new[] { "Name", "Elevation" }, sort_by = new[] { "Name" }, sort_descending = true, name = "Levels" }),
            "t",
            CancellationToken.None);
        await server;

        Assert.False(r.IsError, (r.Content[0] as TextContent)?.Text);
        Assert.Equal(4242, r.Structured!.Value.GetProperty("view_id").GetInt64());
        var calls = requests.ToArray();
        Assert.Equal(new[] { "doc_info", "schedule_create" }, calls.Select(c => c.Tool));
        Assert.False(calls[0].Args.TryGetProperty("document", out _)); // no document → the active one is resolved and checked
        var forwarded = calls[1].Args;
        Assert.Equal(Path.GetFullPath(copy), forwarded.GetProperty("document").GetString());
        Assert.Equal("OST_Levels", forwarded.GetProperty("category").GetString());
        Assert.Equal(new[] { "Name", "Elevation" }, forwarded.GetProperty("fields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Name", forwarded.GetProperty("sort_by")[0].GetString());
        Assert.True(forwarded.GetProperty("sort_descending").GetBoolean());
        Assert.Equal("Levels", forwarded.GetProperty("name").GetString());
        Assert.False(forwarded.TryGetProperty("pid", out _));
    }

    [Fact]
    public async Task DetachedSandboxCopy_IsVerifiedByTheFileDocOpenOpenedItFrom()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var requests = new ConcurrentQueue<(string Tool, JsonElement Args)>();
        string? source = null;
        var server = RunScriptedBridge(pipe, 2, (tool, _) => tool == "doc_info"
            ? JsonSerializer.Serialize(new { title = "central_detached", path = "", is_detached = true, opened_from = source })
            : """{"view_id":7,"activated":false}""",
            requests);

        await using var sp = BridgeGatewayToolsTests.BuildGateway(out var paths, pipe);
        source = Path.Combine(paths.RunsDir, "r2", "central.rvt");
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync(
            "schedule_create",
            JsonSerializer.SerializeToElement(new { pid = Pid, document = "central_detached", category = "OST_Walls", fields = new[] { "Type" }, activate = false }),
            "t",
            CancellationToken.None);
        await server;

        Assert.False(r.IsError, (r.Content[0] as TextContent)?.Text);
        var calls = requests.ToArray();
        Assert.Equal("central_detached", calls[0].Args.GetProperty("document").GetString());
        Assert.Equal(source, calls[1].Args.GetProperty("document").GetString());
    }

    [Fact]
    public async Task SandboxEnforcementOff_ForwardsWithoutTheDocumentProbe()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var requests = new ConcurrentQueue<(string Tool, JsonElement Args)>();
        var server = RunScriptedBridge(pipe, 1, (_, _) => """{"view_id":1}""", requests);
        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _, pipe);
        sp.GetRequiredService<GatewayPolicy>().EnforceSandbox = false;
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync(
            "schedule_create",
            JsonSerializer.SerializeToElement(new { pid = Pid, document = "original", category = "OST_Levels", fields = new[] { "Name" } }),
            "t",
            CancellationToken.None);
        await server;

        Assert.False(r.IsError, (r.Content[0] as TextContent)?.Text);
        var call = Assert.Single(requests.ToArray());
        Assert.Equal("schedule_create", call.Tool);
        Assert.Equal("original", call.Args.GetProperty("document").GetString());
    }

    [Fact]
    public async Task RevitNotLaunchedByTheGateway_WithoutGrant_IsDenied_BeforeReachingRevit()
    {
        var pipe = BridgeClientProtocolTests.NewPipeName();
        var requests = new ConcurrentQueue<(string Tool, JsonElement Args)>();
        using var stop = new CancellationTokenSource();
        var server = RunScriptedBridge(pipe, 2, (_, _) => "{}", requests, stop.Token);
        await using var sp = BridgeGatewayToolsTests.BuildGateway(out _, pipe);
        sp.GetRequiredService<ActionGrantStore>().RevokeAll();
        var r = await sp.GetRequiredService<ToolRegistry>().InvokeAsync(
            "schedule_create",
            JsonSerializer.SerializeToElement(new { pid = Pid, category = "OST_Levels", fields = new[] { "Name" } }),
            "t",
            CancellationToken.None);
        stop.Cancel();
        await Task.WhenAny(server, Task.Delay(2000));

        Assert.Equal(GatewayErrorCodes.PermissionDenied, r.ErrorCode);
        Assert.Empty(requests);
    }

    /// <summary>
    /// Fake Bridge for <paramref name="connections"/> sequential calls (one per connection, like BridgeClient). The next pipe
    /// instance is created as soon as a client connects, so the pipe never disappears between two calls. Records (tool, args).
    /// </summary>
    internal static Task RunScriptedBridge(string pipeName, int connections, Func<string, JsonElement, string> result, ConcurrentQueue<(string Tool, JsonElement Args)> requests, CancellationToken stop = default)
    {
        static NamedPipeServerStream NewInstance(string name) =>
            new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var first = NewInstance(pipeName);
        return Task.Run(async () =>
        {
            var next = first;
            for (var i = 0; i < connections; i++)
            {
                await using var server = next;
                await server.WaitForConnectionAsync(stop);
                if (i + 1 < connections)
                {
                    next = NewInstance(pipeName);
                }

                var header = new byte[4];
                await server.ReadExactlyAsync(header, stop);
                var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
                await server.ReadExactlyAsync(payload, stop);
                using var request = JsonDocument.Parse(payload);
                var tool = request.RootElement.GetProperty("tool").GetString()!;
                var args = request.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                requests.Enqueue((tool, args));

                var body = Encoding.UTF8.GetBytes("{\"id\":\"x\",\"ok\":true,\"result\":" + result(tool, args) + "}");
                var frame = new byte[4 + body.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
                body.CopyTo(frame, 4);
                await server.WriteAsync(frame, stop);
                await server.FlushAsync(stop);
                try
                {
                    await server.ReadExactlyAsync(new byte[1], stop); // until the client disconnects
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                }
            }
        });
    }
}
