using System.IO;

namespace RevitAgent.Tests.Scenarios;

/// <summary>
/// The shipped examples (examples\scenarios\*) must stay valid and runnable: copied to a temp folder with a dummy
/// model, validated against the fake gateway and replayed end to end. Real-Revit confirmation is the integrator's job.
/// </summary>
public class ExampleScenarioTests
{
    [Theory]
    [InlineData("levels-inventory")]
    [InlineData("room-schedule-ui")]
    public async Task ShippedExample_ValidatesAndRunsAgainstFakes(string name)
    {
        await using var host = new ScenarioHost();
        var dir = CopyExample(name);

        var v = await host.CallOkAsync("scenario_validate", new { scenario_dir = dir });
        Assert.True(v.GetProperty("ok").GetBoolean(), v.GetRawText());
        Assert.Equal(0, v.GetProperty("warning_count").GetInt32());

        var run = await host.CallOkAsync("scenario_run", new { scenario_dir = dir, pid = FakeRevit.ExistingPid });
        Assert.Equal("PASS", run.GetProperty("verdict").GetString());
    }

    private static string CopyExample(string name)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "RevitAgent.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var src = Path.Combine(root!, "examples", "scenarios", name);
        var dst = Path.Combine(TestGateway.NewTempDir("revitagent_example_"), name);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }

        Directory.CreateDirectory(Path.Combine(dst, "input"));
        File.WriteAllText(Path.Combine(dst, "input", "model.rvt"), "dummy model");
        return dst;
    }
}
