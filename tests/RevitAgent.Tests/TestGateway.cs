using System.IO;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Mcp;
using RevitAgent.Host.Config;
using RevitAgent.Host.Services;

namespace RevitAgent.Tests;

/// <summary>
/// Isolated gateway + config for tests: temp REVITAGENT home, temp DPAPI config and a unique pipe
/// name, so tests never touch the user's real %LOCALAPPDATA%\RevitAgent data, the real
/// revitagent-mcp pipe, or a Revit process that happens to be running on the machine.
/// </summary>
internal static class TestGateway
{
    public static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ServiceProvider BuildProvider(out AgentPaths paths)
    {
        paths = new AgentPaths(NewTempDir("revitagent_home_"));
        return GatewayServices.BuildGateway(paths);
    }

    public static HostIpcService CreateIpcService(IAuditLogService logService)
    {
        var sp = BuildProvider(out _);
        return new HostIpcService(
            sp.GetRequiredService<GatewayPipeListener>(),
            sp.GetRequiredService<GatewayEvents>(),
            sp.GetRequiredService<AuditLog>(),
            logService);
    }

    /// <summary>Config service backed by a temp encrypted file with a unique MCP pipe name.</summary>
    public static EncryptedConfigService CreateTempConfigService()
    {
        var manager = new EncryptedConfigManager(Path.Combine(NewTempDir("revitagent_cfg_"), "appsettings.enc"));
        var service = new EncryptedConfigService(manager);
        var cfg = service.LoadConfig();
        cfg.IpcPipeName = "revitagent-test-" + Guid.NewGuid().ToString("N")[..8];
        service.SaveConfig(cfg);
        return service;
    }
}
