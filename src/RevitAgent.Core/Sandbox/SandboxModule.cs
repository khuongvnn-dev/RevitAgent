using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.Sandbox;

/// <summary>Sandbox (runs folder) services.</summary>
public sealed class SandboxModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services) => services.TryAddSingleton<SandboxService>();
}
