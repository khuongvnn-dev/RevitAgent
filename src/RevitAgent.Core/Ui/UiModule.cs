using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.Ui;

/// <summary>UI automation module: registers the engine shared by all ui_* tools (one UIA instance, refs, input lock).</summary>
public sealed class UiModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<Sandbox.EvidenceService>();
        services.AddSingleton<UiAutomationService>();
    }
}
