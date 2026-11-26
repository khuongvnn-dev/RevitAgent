using System.IO;
using RevitAgent.Core;
using RevitAgent.Core.Machine;

namespace RevitAgent.Tests.Machine;

public class MachinePresenceTests
{
    [Fact]
    public void Allowlist_MissingFile_ReturnsWarn()
    {
        var home = TestGateway.NewTempDir("revitagent_home_");
        var paths = new AgentPaths(home);
        var readiness = new MachineReadiness(paths);

        var report = readiness.Check(new MachineCheckOptions { Year = 2024, ConfigDir = paths.ConfigDir });
        var item = Assert.Single(report.Items, i => i.Id == "addin_allowlist");

        Assert.Equal(MachineCheckStatus.Warn, item.Status);
        Assert.NotNull(item.Fix);
    }

    [Fact]
    public void Allowlist_FileExistsWithNoEnabledEntries_ReturnsWarn()
    {
        var home = TestGateway.NewTempDir("revitagent_home_");
        var paths = new AgentPaths(home);
        var configDir = paths.ConfigDir;
        Directory.CreateDirectory(configDir);

        var json = @"{ ""version"": 1, ""entries"": [ { ""id"": ""abc"", ""name"": ""Test"", ""enabled"": false } ] }";
        File.WriteAllText(Path.Combine(configDir, "addin-allowlist.json"), json);

        var readiness = new MachineReadiness(paths);
        var report = readiness.Check(new MachineCheckOptions { Year = 2024, ConfigDir = configDir });
        var item = Assert.Single(report.Items, i => i.Id == "addin_allowlist");

        Assert.Equal(MachineCheckStatus.Warn, item.Status);
        Assert.NotNull(item.Fix);
    }

    [Fact]
    public void Allowlist_FileExistsWithEnabledEntry_ReturnsPass()
    {
        var home = TestGateway.NewTempDir("revitagent_home_");
        var paths = new AgentPaths(home);
        var configDir = paths.ConfigDir;
        Directory.CreateDirectory(configDir);

        var json = @"{ ""version"": 1, ""entries"": [ { ""id"": ""abc"", ""name"": ""Test"", ""enabled"": true } ] }";
        File.WriteAllText(Path.Combine(configDir, "addin-allowlist.json"), json);

        var readiness = new MachineReadiness(paths);
        var report = readiness.Check(new MachineCheckOptions { Year = 2024, ConfigDir = configDir });
        var item = Assert.Single(report.Items, i => i.Id == "addin_allowlist");

        Assert.Equal(MachineCheckStatus.Pass, item.Status);
        Assert.Null(item.Fix);
    }

    [Fact]
    public void Telegram_MissingFile_ReturnsWarn()
    {
        var home = TestGateway.NewTempDir("revitagent_home_");
        var paths = new AgentPaths(home);
        var readiness = new MachineReadiness(paths);

        var report = readiness.Check(new MachineCheckOptions { Year = 2024, ConfigDir = paths.ConfigDir });
        var item = Assert.Single(report.Items, i => i.Id == "telegram");

        Assert.Equal(MachineCheckStatus.Warn, item.Status);
        Assert.NotNull(item.Fix);
    }

    [Fact]
    public void Telegram_FileExists_ReturnsPass()
    {
        var home = TestGateway.NewTempDir("revitagent_home_");
        var paths = new AgentPaths(home);
        var configDir = paths.ConfigDir;
        Directory.CreateDirectory(configDir);

        File.WriteAllText(Path.Combine(configDir, "telegram.json"), "{\"bot_token\":\"123:abc\",\"chat_id\":456}");

        var readiness = new MachineReadiness(paths);
        var report = readiness.Check(new MachineCheckOptions { Year = 2024, ConfigDir = configDir });
        var item = Assert.Single(report.Items, i => i.Id == "telegram");

        Assert.Equal(MachineCheckStatus.Pass, item.Status);
        Assert.Null(item.Fix);
    }
}
