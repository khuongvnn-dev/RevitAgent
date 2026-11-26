using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Gateway module registering Telegram notification, escalation and configuration services.
/// Discovered automatically by <see cref="GatewayServices.AddRevitAgentGateway"/>.
/// </summary>
public sealed class TelegramModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<TelegramConfigStore>();
        services.TryAddSingleton<TelegramClient>();
        services.TryAddSingleton<TelegramNotifier>();
        services.TryAddSingleton<INotifier>(sp => sp.GetRequiredService<TelegramNotifier>());
        services.TryAddSingleton<TelegramHumanEscalation>();
        services.TryAddSingleton<IHumanEscalation>(sp => sp.GetRequiredService<TelegramHumanEscalation>());
    }
}
