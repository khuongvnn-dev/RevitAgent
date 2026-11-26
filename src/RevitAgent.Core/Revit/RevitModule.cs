using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.Revit;

/// <summary>Revit process control, readiness gate and dialog handling services (package C).</summary>
public sealed class RevitModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<DialogRuleProvider>();
        services.TryAddSingleton<AddinAllowlist>();       // read-only: tools and the readiness gate
        services.TryAddSingleton<AddinAllowlistStore>();  // write side: dashboard only (no MCP tool may use it)
        services.TryAddSingleton<LaunchedProcessRegistry>();
        services.TryAddSingleton<Sandbox.EvidenceService>();
        services.TryAddSingleton<RevitReadiness>();
        services.TryAddSingleton<IRevitReadiness>(sp => sp.GetRequiredService<RevitReadiness>());
        services.TryAddSingleton<RevitProcessService>();
        services.TryAddSingleton<RevitWatchdog>();
    }
}
