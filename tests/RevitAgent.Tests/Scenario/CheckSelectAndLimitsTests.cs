using System.IO;
using RevitAgent.Core.Scenario;
using Xunit;

namespace RevitAgent.Tests.Scenarios;

public class CheckSelectAndLimitsTests
{
    private readonly string _run;

    public CheckSelectAndLimitsTests()
    {
        _run = TestGateway.NewTempDir("revitagent_select_");
        var data = Path.Combine(_run, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "model.json"), """{"rooms":[{"name":"A","doors":[{"mark":"D1"}]},{"name":"B","doors":[{"mark":"D2"}]}],"empty":[]}""");
        File.WriteAllText(Path.Combine(data, "list.json"), """[{"id":1},{"id":2}]""");
    }

    private CheckEvaluation Eval(string expect) =>
        new CheckEvaluator(_run).Evaluate(new ScenarioCheck { Id = "C1", Title = "t", Expect = expect, Severity = CheckSeverity.Fail });

    [Fact]
    public void TypoAtDepthTwo_IsNotMeasurable()
    {
        Assert.Equal(Verdicts.NotMeasurable, Eval("count(model.json -> rooms.dorrs) == 0").Verdict);
        Assert.Equal(Verdicts.NotMeasurable, Eval("count(model.json -> rooms.doors.mrak) == 0").Verdict);
    }

    [Fact]
    public void ArrayRoot_TypoIsNotMeasurable_AndRealFieldWorks()
    {
        Assert.Equal(Verdicts.NotMeasurable, Eval("count(list.json -> idd) == 0").Verdict);
        Assert.Equal(Verdicts.Pass, Eval("count(list.json -> id) == 2").Verdict);
    }

    [Fact]
    public void RealEmptyMatch_IsZero()
    {
        Assert.Equal(Verdicts.Pass, Eval("count(model.json -> rooms.doors[mark=\"nope\"]) == 0").Verdict);
        Assert.Equal(Verdicts.Pass, Eval("count(model.json -> empty) == 0").Verdict);
    }

    [Fact]
    public void LongOrDeeplyNestedExpression_IsParseError()
    {
        Assert.NotNull(CheckExpression.SyntaxProblem(new string('x', CheckExpression.MaxLength + 1)));
        var deep = new string('(', 1000) + "true" + new string(')', 1000);
        Assert.Contains("nested", CheckExpression.SyntaxProblem(deep));
        var nots = string.Concat(Enumerable.Repeat("not ", 200)) + "true";
        Assert.Contains("nested", CheckExpression.SyntaxProblem(nots));
        Assert.Null(CheckExpression.SyntaxProblem("((true))"));
    }
}
