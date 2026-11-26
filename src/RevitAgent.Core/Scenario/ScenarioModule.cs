using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Schedule;

namespace RevitAgent.Core.Scenario;

/// <summary>
/// Gateway module configuring Scenario library, batch execution, and schedule services.
/// </summary>
public sealed class ScenarioModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<ScenarioLibraryService>();
        services.TryAddSingleton<ScenarioBatchRunner>();
        services.TryAddSingleton<ScheduleConfigStore>();
        services.TryAddSingleton<ScheduleService>();
    }
}
