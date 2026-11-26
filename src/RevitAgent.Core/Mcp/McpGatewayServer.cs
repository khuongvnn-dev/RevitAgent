using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Mcp;

/// <summary>
/// Runs MCP sessions (official ModelContextProtocol SDK) on top of the <see cref="ToolRegistry"/>.
/// The SDK owns the protocol (JSON-RPC ids of any type, notifications, initialize, versions);
/// we only answer tools/list and tools/call. tools/list follows the session's <see cref="ToolProfiles">profile</see>;
/// tools/call runs any registered tool.
/// </summary>
public sealed class McpGatewayServer
{
    public const string ServerName = "revitagent";

    private readonly ToolRegistry _registry;
    private readonly GatewayEvents _events;
    private readonly IServiceProvider _services;
    private readonly SessionProfiles _profiles;
    private readonly ILoggerFactory _loggerFactory;

    public McpGatewayServer(ToolRegistry registry, GatewayEvents events, IServiceProvider services, SessionProfiles profiles, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _events = events;
        _services = services;
        _profiles = profiles;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public static string Version =>
        typeof(McpGatewayServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "2.0.0";

    /// <summary>
    /// Serves one MCP session over a duplex stream pair (named pipe or stdio) until the client disconnects.
    /// <paramref name="profile"/> fixes the tool profile (headless Shim); null = what the Shim or client announced, else core.
    /// </summary>
    public async Task RunSessionAsync(Stream input, Stream output, string sessionId, CancellationToken cancellationToken, string? profile = null)
    {
        var transport = new StreamServerTransport(input, output, ServerName, _loggerFactory);
        await using var server = McpServer.Create(transport, BuildOptions(sessionId, profile), _loggerFactory, _services);
        _events.RaiseClientConnected(sessionId);
        try
        {
            await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profiles.Remove(sessionId);
            _events.RaiseClientDisconnected(sessionId);
        }
    }

    /// <summary>Serves one MCP session on this process's stdin/stdout (headless mode).</summary>
    public Task RunStdioAsync(CancellationToken cancellationToken, string? profile = null) =>
        RunSessionAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), "stdio-" + Environment.ProcessId, cancellationToken, profile);

    public McpServerOptions BuildOptions(string sessionId, string? profile = null)
    {
        string? announced = null; // set by the relay Shim's profile notification (one per session)

        // Fixed (headless) > announced by the Shim > declared by the client in its capabilities > core.
        string Resolve(McpServer server)
        {
            var effective = profile ?? Volatile.Read(ref announced) ?? ToolProfiles.FromClientCapabilities(server.ClientCapabilities) ?? ToolProfiles.Core;
            _profiles.Set(sessionId, effective);
            return effective;
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Title = "RevitAgent gateway", Version = Version },
            ServerInstructions = AgentInstructions.Text,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (ctx, ct) => ValueTask.FromResult(ListTools(_registry.Tools, Resolve(ctx.Server))),
                CallToolHandler = async (ctx, ct) =>
                {
                    var p = ctx.Params ?? throw new ArgumentException("tools/call without params");
                    var effective = Resolve(ctx.Server);
                    if (_registry.TryGet(p.Name, out _) && !ToolProfiles.Lists(effective, p.Name))
                    {
                        // Same behaviour for every client: a tool hidden from tools/list cannot be called by guessing its name.
                        return ToMcpResult(ToolResult.Error(GatewayException.NotFound(
                            $"Tool '{p.Name}' is not available in profile '{effective}'. Use macro_list / macro_run, or restart the client with the Shim flag --profile full.",
                            new { profile = effective, tool = p.Name })));
                    }

                    JsonElement? args = p.Arguments is null ? null : JsonSerializer.SerializeToElement(p.Arguments);
                    var result = await _registry.InvokeAsync(new ToolCall(p.Name, args, sessionId), ct).ConfigureAwait(false);
                    return ToMcpResult(result);
                },
                NotificationHandlers = new[]
                {
                    new KeyValuePair<string, Func<JsonRpcNotification, CancellationToken, ValueTask>>(ToolProfiles.NotificationMethod, (n, ct) =>
                    {
                        if (ToolProfiles.FromNotification(n.Params) is { } p)
                        {
                            Volatile.Write(ref announced, p);
                        }

                        return ValueTask.CompletedTask;
                    }),
                },
            },
        };
    }

    /// <summary>tools/list of <paramref name="profile"/>, exactly as the server sends it.</summary>
    public static ListToolsResult ListTools(IEnumerable<IGatewayTool> tools, string profile) =>
        new() { Tools = ToolProfiles.Filter(tools, profile).Select(ToMcpTool).ToList() };

    public static Tool ToMcpTool(IGatewayTool t) => new()
    {
        Name = t.Name,
        Description = $"[{t.Permission}] {t.Description}",
        InputSchema = t.InputSchema,
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = t.Permission == ToolPermission.Read,
            DestructiveHint = t.Permission >= ToolPermission.Write,
            OpenWorldHint = false,
        },
    };

    public static CallToolResult ToMcpResult(ToolResult r)
    {
        var blocks = new List<ContentBlock>();
        foreach (var c in r.Content)
        {
            switch (c)
            {
                case TextContent t:
                    blocks.Add(new TextContentBlock { Text = t.Text });
                    break;
                case ImageContent i:
                    blocks.Add(ImageContentBlock.FromBytes(i.Data, i.MimeType));
                    break;
            }
        }

        return new CallToolResult { Content = blocks, IsError = r.IsError };
    }
}
