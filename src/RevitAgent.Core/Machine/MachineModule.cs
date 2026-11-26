using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.Machine;

/// <summary>Machine readiness services and DI registration.</summary>
public sealed class MachineModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<MachineReadiness>();
    }
}
