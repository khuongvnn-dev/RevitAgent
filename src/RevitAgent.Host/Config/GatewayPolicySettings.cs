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

    /// <summary>
    /// Null or empty text means "never configured" and keeps the default, Admin: that is a deliberate default for the
    /// user's own tool (it needs revit_launch / revit_close, and bridge_install needs owner approval anyway). Any other
    /// text that is not a defined <see cref="ToolPermission"/> is a damaged or unknown saved value and fails closed to Read.
    /// </summary>
    public static ToolPermission ParsePermission(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ToolPermission.Admin;
        }

        return Enum.TryParse<ToolPermission>(text.Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : ToolPermission.Read;
    }

    public static void Apply(AppConfig config, GatewayPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(policy);
        policy.MaxPermission = ParsePermission(config.GatewayMaxPermission);
        policy.EnforceSandbox = config.GatewayEnforceSandbox;
        policy.SetAllowedInputProcesses(CleanProcessNames(config.GatewayAllowedInputProcesses));
    }

    /// <summary>"notepad, Foo.exe; bar" → ["notepad", "Foo", "bar"] (separators: comma, semicolon, new line).</summary>
    public static List<string> ParseProcessList(string? text) =>
        CleanProcessNames((text ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Trimmed, without ".exe", no empty or repeated names.</summary>
    public static List<string> CleanProcessNames(IEnumerable<string?>? names) =>
        (names ?? Array.Empty<string?>())
            .Select(n => (n ?? string.Empty).Trim())
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4].Trim() : n)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
