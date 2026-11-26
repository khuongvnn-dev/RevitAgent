using RevitAgent.Core.Macros;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Policy;

namespace RevitAgent.Core.Tools.Builtin;

/// <summary>Self-description of the gateway: version, folders, policy and the tool catalog grouped by area.</summary>
public sealed class GatewayInfoTool : GatewayTool
{
    private readonly IServiceProvider _services;
    private readonly AgentPaths _paths;
    private readonly GatewayPolicy _policy;

    public GatewayInfoTool(IServiceProvider services, AgentPaths paths, GatewayPolicy policy)
    {
        _services = services;
        _paths = paths;
        _policy = policy;
    }

    public override string Name => "gateway_get_info";

    public override string Description =>
        "Describe the RevitAgent gateway: version, sandbox/run folders, current policy, macro names (macro_list) and all tools grouped by area with their permission. Start here if unsure which tool to use.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // Resolve lazily: the registry depends on all tools, including this one.
        var registry = (ToolRegistry)_services.GetService(typeof(ToolRegistry))!;
        var groups = registry.Tools
            .OrderBy(t => t.Name)
            .GroupBy(t => t.Name.Split('_')[0])
            .ToDictionary(g => g.Key, g => g.Select(t => new { name = t.Name, permission = t.Permission.ToString() }).ToArray());
        var profile = ((SessionProfiles?)_services.GetService(typeof(SessionProfiles)))?.Get(call.SessionId) ?? ToolProfiles.Core;
        var listed = ToolProfiles.Filter(registry.Tools, profile).Count;

        return Task.FromResult(ToolResult.Json(new
        {
            name = McpGatewayServer.ServerName,
            version = McpGatewayServer.Version,
            paths = new { root = _paths.Root, runs = _paths.RunsDir, audit = _paths.AuditDir, captures = _paths.CapturesDir, config = _paths.ConfigDir },
            policy = new { max_permission = _policy.MaxPermission.ToString(), enforce_sandbox = _policy.EnforceSandbox },
            profile,
            tools_listed = listed,
            tools_total = registry.Tools.Count,
            hint = listed < registry.Tools.Count ? "tools/list shows the core profile; start the Shim with --profile full to list all tools." : null,
            macros = (_services.GetService(typeof(MacroLibrary)) as MacroLibrary)?.Names ?? Array.Empty<string>(),
            tools = groups,
        }));
    }
}
