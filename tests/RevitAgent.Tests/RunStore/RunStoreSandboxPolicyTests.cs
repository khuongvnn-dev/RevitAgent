using System.IO;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Tools;
using Xunit;

namespace RevitAgent.Tests.RunStore;

public class RunStoreSandboxPolicyTests
{
    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"C:\")]
    [InlineData(@"D:")]
    [InlineData(@"\\server\share\runs")]
    [InlineData("//server/share/runs")]
    [InlineData("runs")]
    [InlineData(@"..\runs")]
    public void RunStoreOptions_Validate_RejectsRootsThatWouldWidenTheSandbox(string root)
    {
        Assert.NotNull(RunStoreOptions.Validate(root));
    }

    [Fact]
    public void RunStoreOptions_Validate_RejectsSystemFoldersAndTheWholeProfile_AcceptsDedicatedFolders()
    {
        Assert.NotNull(RunStoreOptions.Validate(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(RunStoreOptions.Validate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RevitRuns")));
        Assert.NotNull(RunStoreOptions.Validate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.NotNull(RunStoreOptions.Validate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar));

        Assert.Null(RunStoreOptions.Validate(null));
        Assert.Null(RunStoreOptions.Validate("  "));
        Assert.Null(RunStoreOptions.Validate(@"D:\RevitAgentRuns"));
        Assert.Null(RunStoreOptions.Validate(TestGateway.NewTempDir("custom_runs_")));
        Assert.Null(RunStoreOptions.Validate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RevitAgentRuns")));
    }

    [Fact]
    public void RunStoreOptions_Save_RefusesAnInvalidRoot()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        Assert.Throws<ArgumentException>(() => RunStoreOptions.Save(paths, new RunStoreOptions { Root = @"D:\" }));
        Assert.False(File.Exists(Path.Combine(paths.ConfigDir, RunStoreOptions.FileName)));
    }

    [Fact]
    public void HandEditedDriveRoot_FallsBackToTheDefaultFolder_AndNeverWidensTheSandbox()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        Directory.CreateDirectory(paths.ConfigDir);
        File.WriteAllText(Path.Combine(paths.ConfigDir, RunStoreOptions.FileName), "{\"root\": \"D:\\\\\"}");

        var loaded = RunStoreOptions.Load(paths);
        Assert.Equal(@"D:\", loaded.Root);
        Assert.Equal(paths.RunsDir, loaded.GetEffectiveRoot(paths));
        Assert.False(new GatewayPolicy().IsInSandbox(@"D:\Projects\original.rvt", paths));
    }

    [Fact]
    public void RunStoreOptions_DefaultRoot_IsAgentPathsRunsDir()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var options = RunStoreOptions.Load(paths);

        Assert.Null(options.Root);
        Assert.Equal(paths.RunsDir, options.GetEffectiveRoot(paths));
    }

    [Fact]
    public void RunStoreOptions_SaveAndLoad_PersistsConfiguredRoot()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var customRoot = TestGateway.NewTempDir("custom_runs_");

        RunStoreOptions.Save(paths, new RunStoreOptions { Root = customRoot });

        var configFile = Path.Combine(paths.ConfigDir, RunStoreOptions.FileName);
        Assert.True(File.Exists(configFile));

        var loaded = RunStoreOptions.Load(paths);
        Assert.Equal(customRoot, loaded.Root);
        Assert.Equal(customRoot, loaded.GetEffectiveRoot(paths));
    }

    [Fact]
    public void GatewayPolicy_SandboxCheck_UsesConfiguredRoot_AndAllowsBothNewAndOldRoots()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var customRoot = TestGateway.NewTempDir("custom_runs_");
        var policy = new GatewayPolicy();

        // 1. Initially, paths.RunsDir is in sandbox
        var defaultRunFile = Path.Combine(paths.RunsDir, "run1", "model.rvt");
        Assert.True(policy.IsInSandbox(defaultRunFile, paths));

        // 2. Configure custom root
        RunStoreOptions.Save(paths, new RunStoreOptions { Root = customRoot });

        // 3. Custom root paths are now in sandbox
        var customRunFile = Path.Combine(customRoot, "2026-11", "2026-11-25", "100000_test_abcdef", "model", "model.rvt");
        Assert.True(policy.IsInSandbox(customRunFile, paths));

        // 4. Old root paths also remain in sandbox (changing root affects new runs only)
        Assert.True(policy.IsInSandbox(defaultRunFile, paths));

        // 5. Unrelated paths outside both are denied
        var outside = @"C:\Outside\Model.rvt";
        Assert.False(policy.IsInSandbox(outside, paths));
        var ex = Assert.Throws<GatewayException>(() => policy.EnsureInSandbox(outside, paths, "Model"));
        Assert.Equal(GatewayErrorCodes.SandboxViolation, ex.Code);
        Assert.Contains(customRoot, ex.Message);
    }

    [Fact]
    public void GatewayPolicy_SandboxCheck_RejectsUNCPaths_EvenWithCustomRoot()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var customRoot = TestGateway.NewTempDir("custom_runs_");
        RunStoreOptions.Save(paths, new RunStoreOptions { Root = customRoot });
        var policy = new GatewayPolicy();

        Assert.False(policy.IsInSandbox(@"\\server\share\runs\model.rvt", paths));
        Assert.False(policy.IsInSandbox(@"\\?\C:\some\path.rvt", paths));
    }

    [Fact]
    public void GatewayPolicy_SandboxCheck_RejectsJunctionInsideConfiguredRoot()
    {
        var paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        var customRoot = TestGateway.NewTempDir("custom_runs_");
        RunStoreOptions.Save(paths, new RunStoreOptions { Root = customRoot });

        var outside = TestGateway.NewTempDir("outside_model_");
        File.WriteAllText(Path.Combine(outside, "secret.rvt"), "model bytes");

        var link = Path.Combine(customRoot, "run1", "lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        // Create junction under customRoot pointing to outside
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mk.WaitForExit(10_000);
        }

        Assert.True(Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint), "junction created");

        var policy = new GatewayPolicy();
        // Path traversing through junction must be rejected as sandbox violation
        Assert.False(policy.IsInSandbox(Path.Combine(link, "secret.rvt"), paths));
        // Direct non-junction path in custom root is allowed
        Assert.True(policy.IsInSandbox(Path.Combine(customRoot, "run1", "safe.rvt"), paths));
    }
}
