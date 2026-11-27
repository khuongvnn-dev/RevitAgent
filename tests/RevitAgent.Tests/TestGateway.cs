using System.IO;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Policy;
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

    /// <summary>
    /// Helper processes the desktop tests drive (the WPF test app and the powershell.exe dialog hosts). UI input reaches only
    /// Revit by default, so the test gateway allows these the way an owner would in the dashboard settings.
    /// </summary>
    public static readonly string[] TestInputProcesses = { "RevitAgent.UiTestApp", "powershell" };

    public static ServiceProvider BuildProvider(out AgentPaths paths)
    {
        paths = new AgentPaths(NewTempDir("revitagent_home_"));
        var sp = GatewayServices.BuildGateway(paths);
        sp.GetRequiredService<GatewayPolicy>().SetAllowedInputProcesses(TestInputProcesses);
        return sp;
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
