using System.IO;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;
using Xunit;

namespace RevitAgent.Tests.RunStore;

public class RunStoreResolveRunDirTests
{
    private static RevitAgent.Core.RunStore.RunStore Create()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        return new RevitAgent.Core.RunStore.RunStore(paths, new GatewayPolicy());
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share\x")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"2026-11\..\..")]
    [InlineData("/etc")]
    public void ResolveRunDir_RejectsPathsOutsideTheStore(string id)
    {
        var store = Create();
        Directory.CreateDirectory(store.RootDir);
        Assert.Throws<GatewayException>(() => store.ResolveRunDir(id));
    }

    [Fact]
    public void ResolveRunDir_AcceptsOldAndNewLayout()
    {
        var store = Create();
        var oldDir = Directory.CreateDirectory(Path.Combine(store.RootDir, "old-run")).FullName;
        Assert.Equal(oldDir, store.ResolveRunDir("old-run"));

        var newDir = Directory.CreateDirectory(Path.Combine(store.RootDir, "2026-11", "2026-11-27", "120000_x_abc123")).FullName;
        Assert.Equal(newDir, store.ResolveRunDir("120000_x_abc123"));
        Assert.Equal(newDir, store.ResolveRunDir("2026-11/2026-11-27/120000_x_abc123"));
        Assert.Equal(newDir, store.ResolveRunDir(@"2026-11\2026-11-27\120000_x_abc123"));
    }

    [Fact]
    public void ResolveRunDir_IgnoresTamperedIndexRunDir()
    {
        var store = Create();
        var outside = TestGateway.NewTempDir("revitagent_outside_");
        Directory.CreateDirectory(store.RootDir);
        File.WriteAllText(store.IndexFilePath, "{\"id\":\"evil\",\"run_dir\":\"" + outside.Replace("\\", "\\\\") + "\"}\n");
        Assert.Throws<GatewayException>(() => store.ResolveRunDir("evil"));
    }

    [Fact]
    public void RecordSkippedRun_CreatesRootOnFreshInstall()
    {
        var store = Create();
        if (Directory.Exists(store.RootDir)) Directory.Delete(store.RootDir, true);
        store.RecordSkippedRun("nightly");
        Assert.True(File.Exists(store.IndexFilePath));
    }
}
