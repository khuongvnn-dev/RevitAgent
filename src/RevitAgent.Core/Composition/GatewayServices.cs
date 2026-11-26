using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Composition;

/// <summary>
/// A feature module (UI automation, Revit process control, Bridge client, scenarios...).
/// Public non-abstract implementations in a gateway assembly are discovered automatically and get a
/// chance to register their own services. Tools and observers are discovered separately, so a module
/// only registers the services its tools depend on.
/// </summary>
public interface IGatewayModule
{
    void ConfigureServices(IServiceCollection services);
}

/// <summary>Builds the gateway's DI container (used by the WPF Host, the headless Shim and tests).</summary>
public static class GatewayServices
{
    /// <summary>
    /// Registers core services, then every <see cref="IGatewayModule"/>, <see cref="IGatewayTool"/> and
    /// <see cref="IToolCallObserver"/> found in <paramref name="assemblies"/> (default: RevitAgent.Core).
    /// </summary>
    public static IServiceCollection AddRevitAgentGateway(this IServiceCollection services, AgentPaths? paths = null, params Assembly[] assemblies)
    {
        var scan = assemblies.Length > 0 ? assemblies : new[] { typeof(GatewayServices).Assembly };

        services.AddSingleton(paths ?? new AgentPaths());
        services.AddSingleton<AuditLog>();
        services.AddSingleton<GatewayPolicy>();
        services.AddSingleton<GatewayEvents>();
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<Mcp.SessionProfiles>();
        services.AddSingleton<Mcp.McpGatewayServer>();
        services.AddSingleton<Mcp.GatewayPipeListener>();

        foreach (var type in scan.SelectMany(SafeTypes).Where(IsConcrete))
        {
            if (typeof(IGatewayModule).IsAssignableFrom(type))
            {
                var module = (IGatewayModule)Activator.CreateInstance(type)!;
                module.ConfigureServices(services);
            }
        }

        foreach (var type in scan.SelectMany(SafeTypes).Where(IsConcrete))
        {
            if (typeof(IGatewayTool).IsAssignableFrom(type))
            {
                services.AddSingleton(type);
                services.AddSingleton(typeof(IGatewayTool), sp => sp.GetRequiredService(type));
            }

            if (typeof(IToolCallObserver).IsAssignableFrom(type))
            {
                services.AddSingleton(type);
                services.AddSingleton(typeof(IToolCallObserver), sp => sp.GetRequiredService(type));
            }
        }

        return services;
    }

    /// <summary>Convenience for headless use and tests.</summary>
    public static ServiceProvider BuildGateway(AgentPaths? paths = null, params Assembly[] assemblies) =>
        new ServiceCollection().AddRevitAgentGateway(paths, assemblies).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

    private static bool IsConcrete(Type t) =>
        t is { IsClass: true, IsAbstract: false, IsPublic: true } && !t.ContainsGenericParameters &&
        t.GetCustomAttribute<ExcludeFromGatewayAttribute>() is null;

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try
        {
            return a.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}

/// <summary>Keeps a public tool/module class out of automatic discovery (e.g. test doubles).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ExcludeFromGatewayAttribute : Attribute
{
}
