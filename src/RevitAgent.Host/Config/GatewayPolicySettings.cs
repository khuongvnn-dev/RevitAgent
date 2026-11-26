namespace RevitAgent.Host.Config;

using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;

/// <summary>Maps the persisted gateway-policy fields of <see cref="AppConfig"/> onto the live <see cref="GatewayPolicy"/>.</summary>
public static class GatewayPolicySettings
{
    public static readonly IReadOnlyList<string> PermissionNames = new[]
    {
        nameof(ToolPermission.Read), nameof(ToolPermission.Interact), nameof(ToolPermission.Write), nameof(ToolPermission.Admin),
    };

    /// <summary>Unknown or empty text falls back to Admin (the default of the user's own tool).</summary>
    public static ToolPermission ParsePermission(string? text) =>
        Enum.TryParse<ToolPermission>(text?.Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : ToolPermission.Admin;

    public static void Apply(AppConfig config, GatewayPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(policy);
        policy.MaxPermission = ParsePermission(config.GatewayMaxPermission);
        policy.EnforceSandbox = config.GatewayEnforceSandbox;
    }
}
