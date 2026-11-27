using System.IO;
using System.Text;
using RevitAgent.Core.Scenario;
using Xunit;

namespace RevitAgent.Tests.Scenarios;

public class ScenarioYamlLimitsTests
{
    // 10^9 leaves once aliases are expanded, in a few hundred bytes of YAML.
    private static string BillionLaughs()
    {
        var sb = new StringBuilder("name: bomb\nsteps:\n  - tool: x\n    args:\n      a0: &a0 [lol, lol, lol, lol, lol, lol, lol, lol, lol, lol]\n");
        for (var i = 1; i <= 8; i++)
        {
            sb.Append($"      a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat("*a" + (i - 1), 10))}]\n");
        }

        return sb.ToString();
    }

    [Fact]
    public void AliasBomb_IsRejectedWithValidationIssue()
    {
        var result = ScenarioLoader.Parse(BillionLaughs(), TestGateway.NewTempDir("revitagent_bomb_"));
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Message.Contains("too large or too deeply nested"));
        Assert.Null(result.Scenario);
    }

    [Fact]
    public void OversizedFile_IsRejected()
    {
        var dir = TestGateway.NewTempDir("revitagent_big_");
        File.WriteAllText(Path.Combine(dir, ScenarioDefinition.FileName), "name: x\n# " + new string('a', ScenarioLoader.MaxFileBytes));
        var result = ScenarioLoader.Load(dir);
        Assert.Contains(result.Issues, i => i.Message.Contains("too large"));
    }

    [Fact]
    public void NormalScenario_StillLoads()
    {
        var result = ScenarioLoader.Parse("name: ok\nsteps:\n  - tool: revit_list_dialogs\n", TestGateway.NewTempDir("revitagent_ok_"));
        Assert.False(result.HasErrors);
    }
}
