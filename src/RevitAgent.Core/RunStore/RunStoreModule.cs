using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.RunStore;

/// <summary>
/// Gateway module registering RunStore services.
/// </summary>
public sealed class RunStoreModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<RunStore>();
    }
}
