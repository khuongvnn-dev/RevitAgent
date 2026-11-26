using System.IO;
using RevitAgent.Core.Scenario;

namespace RevitAgent.Tests.Scenarios;

public class CheckEvaluatorTests
{
    private readonly string _run;

    public CheckEvaluatorTests()
    {
        _run = TestGateway.NewTempDir("revitagent_eval_");
        var data = Path.Combine(_run, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "levels.json"), """
            {"category":"Levels","count":3,"elements":[
              {"id":1,"name":"Level 1","elevation":0.0},
              {"id":2,"name":"Level 2","elevation":3000.04},
              {"id":3,"name":"Roof","elevation":6000}],
             "doc":{"title":"model.rvt","Room Name":"Office"}}
            """);
        File.WriteAllText(Path.Combine(data, "schedule.json"), """{"rows":[{"Name":"A"},{"Name":"B"}],"columns":["Name","Area"]}""");
        File.WriteAllText(Path.Combine(data, "step_status.json"), """
            {"externalRunId":"","source":"steps","ok":true,"message":"",
             "steps":[{"stepCode":"levels","status":"SUCCEEDED"},{"stepCode":"bad","status":"FAILED"},{"stepCode":"ui-1","status":"BLOCKED","kind":"ui"}]}
            """);
        File.WriteAllText(Path.Combine(_run, "run.log"), "[10:00] start\n[10:01] WARN slow\n[10:02] end\n");
        File.WriteAllText(Path.Combine(data, "addin.log"), "INFO ok\n");
    }

    private CheckEvaluation Eval(string expect, string severity = CheckSeverity.Fail) =>
        new CheckEvaluator(_run).Evaluate(new ScenarioCheck { Id = "C1", Title = "test", Expect = expect, Severity = severity });

    [Theory]
    [InlineData("count(levels.json -> elements) == 3", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements) == 2", Verdicts.Fail)]
    [InlineData("count(levels.json -> elements) >= 1", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements) >= 4", Verdicts.Fail)]
    [InlineData("count(levels.json -> elements) != 0", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements) < 10", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements) <= 3", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements) > 3", Verdicts.Fail)]
    [InlineData("count(levels.json -> elements[name=\"Level 1\"]) == 1", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements[name!=\"Roof\"]) == 2", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements[name starts_with \"Level\"]) == 2", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements[name contains \"oo\"]) == 1", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements[name not_contains \"Level\"]) == 1", Verdicts.Pass)]
    [InlineData("count(levels.json -> elements[name starts_with \"Level\", id != 2]) == 1", Verdicts.Pass)]
    [InlineData("count(schedule.json -> columns) == 2", Verdicts.Pass)]
    [InlineData("count(levels.json) == 1", Verdicts.Pass)]
    [InlineData("count(missing.json -> elements) == 3", Verdicts.NotMeasurable)]
    [InlineData("count(levels.json -> elemnts[name=\"X\"]) == 0", Verdicts.NotMeasurable)]
    [InlineData("count(levels.json -> elements[name=\"X\"]) == 0", Verdicts.Pass)]
    public void Count(string expect, string verdict) => Assert.Equal(verdict, Eval(expect).Verdict);

    [Theory]
    [InlineData("value(levels.json -> count) == 3", Verdicts.Pass)]
    [InlineData("value(levels.json -> count) == 4", Verdicts.Fail)]
    [InlineData("value(levels.json -> elements[name=\"Level 2\"].elevation) == 3000 +- 0.05", Verdicts.Pass)]
    [InlineData("value(levels.json -> elements[name=\"Level 2\"].elevation) == 3000", Verdicts.Fail)]
    [InlineData("value(levels.json -> elements[name=\"Level 2\"].elevation) > 2999.9", Verdicts.Pass)]
    [InlineData("value(levels.json -> doc.title) == \"model.rvt\"", Verdicts.Pass)]
    [InlineData("value(levels.json -> doc.\"Room Name\") == \"Office\"", Verdicts.Pass)]
    [InlineData("value(levels.json -> category) == \"Levels\"", Verdicts.Pass)]
    [InlineData("value(levels.json -> category) == \"levels\"", Verdicts.Fail)]
    [InlineData("value(levels.json -> category) != \"levels\"", Verdicts.Pass)]
    [InlineData("value(levels.json -> count) == count(levels.json -> elements)", Verdicts.Pass)]
    [InlineData("value(levels.json -> elements.name) == \"Level 1\"", Verdicts.NotMeasurable)]
    [InlineData("value(levels.json -> nothing) == 1", Verdicts.NotMeasurable)]
    [InlineData("value(missing.json -> count) == 3", Verdicts.NotMeasurable)]
    [InlineData("value(levels.json -> category) < \"z\"", Verdicts.NotMeasurable)]
    public void Value(string expect, string verdict) => Assert.Equal(verdict, Eval(expect).Verdict);

    [Theory]
    [InlineData("step_ok(levels)", Verdicts.Pass)]
    [InlineData("step_ok(bad)", Verdicts.Fail)]
    [InlineData("step_ok(never-ran)", Verdicts.Fail)]
    [InlineData("step_ok(ui-1)", Verdicts.NotMeasurable)]
    public void StepOk(string expect, string verdict) => Assert.Equal(verdict, Eval(expect).Verdict);

    [Fact]
    public void StepOk_UnreadableStatus_IsNotMeasurable()
    {
        File.WriteAllText(Path.Combine(_run, "data", "step_status.json"), """{"ok":false,"message":"tracking down","steps":[]}""");
        Assert.Equal(Verdicts.NotMeasurable, Eval("step_ok(levels)").Verdict);
        File.Delete(Path.Combine(_run, "data", "step_status.json"));
        Assert.Equal(Verdicts.NotMeasurable, Eval("step_ok(levels)").Verdict);
    }

    [Theory]
    [InlineData("has_line(run.log, \"WARN\")", Verdicts.Pass)]
    [InlineData("has_line(run.log, \"^\\[10:0[0-9]\\] end$\")", Verdicts.Pass)]
    [InlineData("has_line(run.log, \"ERROR\")", Verdicts.Fail)]
    [InlineData("no_line(run.log, \"ERROR|Exception\")", Verdicts.Pass)]
    [InlineData("no_line(run.log, \"WARN\")", Verdicts.Fail)]
    [InlineData("no_line(addin.log, \"Fatal\")", Verdicts.Pass)]
    [InlineData("has_line(missing.log, \"x\")", Verdicts.NotMeasurable)]
    public void LogLines(string expect, string verdict) => Assert.Equal(verdict, Eval(expect).Verdict);

    [Theory]
    [InlineData("count(levels.json -> elements) == 3 and step_ok(bad)", Verdicts.Fail)]
    [InlineData("count(levels.json -> elements) == 3 and count(missing.json -> x) == 1", Verdicts.NotMeasurable)]
    [InlineData("count(levels.json -> elements) == 9 and count(missing.json -> x) == 1", Verdicts.Fail)]
    [InlineData("count(levels.json -> elements) == 3 and step_ok(levels)", Verdicts.Pass)]
    [InlineData("step_ok(bad) or step_ok(levels)", Verdicts.Pass)]
    [InlineData("step_ok(bad) or count(missing.json -> x) == 1", Verdicts.NotMeasurable)]
    [InlineData("step_ok(bad) or has_line(run.log, \"ERROR\")", Verdicts.Fail)]
    [InlineData("not step_ok(bad)", Verdicts.Pass)]
    [InlineData("not step_ok(levels)", Verdicts.Fail)]
    [InlineData("not count(missing.json -> x) == 1", Verdicts.NotMeasurable)]
    [InlineData("step_ok(bad) and step_ok(levels) or step_ok(levels)", Verdicts.Pass)]
    [InlineData("step_ok(levels) and (step_ok(bad) or step_ok(levels))", Verdicts.Pass)]
    [InlineData("step_ok(levels) and (step_ok(bad) or has_line(run.log, \"ERROR\"))", Verdicts.Fail)]
    public void Logic_IsThreeValued(string expect, string verdict) => Assert.Equal(verdict, Eval(expect).Verdict);

    [Fact]
    public void Evaluation_KeepsEachTerm()
    {
        var r = Eval("count(levels.json -> elements) == 3 and step_ok(bad)");
        Assert.Equal(2, r.Terms.Count);
        Assert.Equal(Verdicts.Pass, r.Terms[0].Verdict);
        Assert.Equal(Verdicts.Fail, r.Terms[1].Verdict);
    }

    [Theory]
    [InlineData("TBD")]
    [InlineData("count(levels.json -> elements) == TBD")]
    [InlineData("value(levels.json -> elements[name=\"Level 2\"].elevation) == 3000 +- TBD")]
    [InlineData("count(levels.json -> elements) >= pending")]
    public void PendingThreshold_IsNotMeasurable_EvenWhenDataWouldPass(string expect) =>
        Assert.Equal(Verdicts.NotMeasurable, Eval(expect).Verdict);

    [Fact]
    public void PendingInOneTermOfAnd_StillFailsWhenAnotherTermFails() =>
        Assert.Equal(Verdicts.Fail, Eval("count(levels.json -> elements) == TBD and step_ok(bad)").Verdict);

    [Fact]
    public void ExpressionThatCannotBeParsed_IsNotMeasurable_NeverPass()
    {
        var r = Eval("count(levels.json -> elements) approx 3");
        Assert.Equal(Verdicts.NotMeasurable, r.Verdict);
        Assert.Contains("cannot be read", r.Detail);
    }

    [Fact]
    public void FileOutsideRunFolder_IsRejected_NotRead()
    {
        var r = Eval("count(..\\..\\outside.json -> x) == 1");
        Assert.Equal(Verdicts.NotMeasurable, r.Verdict);
        Assert.Contains("outside the run folder", r.Detail);
    }

    [Fact]
    public void Evaluation_ListsFilesAndKey_AndCarriesSeverityAndNote()
    {
        var r = new CheckEvaluator(_run).Evaluate(new ScenarioCheck
        {
            Id = "C9",
            Title = "t",
            Expect = "count(levels.json -> elements) == 3 and has_line(run.log, \"end\")",
            Severity = CheckSeverity.Warn,
            Note = "how it is measured",
        });
        Assert.Contains(Path.Combine("data", "levels.json"), r.Files);
        Assert.Contains("run.log", r.Files);
        Assert.Equal(64, r.Key.Length);
        Assert.Contains("= 3", r.Terms[0].Detail);
        Assert.Equal(CheckSeverity.Warn, r.Severity);
        Assert.Equal("how it is measured", r.Note);
    }

    [Theory]
    [InlineData("count(levels.json -> elements) == 3", true)]
    [InlineData("value(levels.json -> count) == 3 +- 1", true)]
    [InlineData("step_ok(x)", true)]
    [InlineData("has_line(run.log, \"a\")", true)]
    [InlineData("count(a.json -> x) >= 1 and (step_ok(s) or not has_line(run.log, \"E\"))", true)]
    [InlineData("TBD", true)]
    [InlineData("count(levels.json -> elements)", false)]
    [InlineData("count(levels.json -> elements) == ", false)]
    [InlineData("count(levels.json -> elements[bad cond]) == 1", false)]
    [InlineData("count(elements) == 1", false)]
    [InlineData("count(levels.json -> elements) < 3 +- 1", false)]
    [InlineData("step_ok(a, b)", false)]
    [InlineData("has_line(run.log, \"(unclosed\")", false)]
    [InlineData("step_ok(a) and", false)]
    [InlineData("(step_ok(a)", false)]
    [InlineData("frobnicate(x) == 1", false)]
    [InlineData("whatever", false)]
    [InlineData("", false)]
    public void SyntaxProblem_FlagsInvalidExpressions(string expect, bool ok) =>
        Assert.Equal(ok, CheckExpression.SyntaxProblem(expect) is null);

    [Fact]
    public void ReferencedStepsAndFiles_ComeFromTheExpression()
    {
        var e = CheckExpression.Parse("step_ok(levels) and count(levels.json -> elements) >= 1 and no_line(run.log, \"E\")");
        Assert.Equal(new[] { "levels" }, e.ReferencedSteps());
        Assert.Equal(new[] { "levels.json", "run.log" }, e.ReferencedFiles());
    }
}
