using RevitAgent.Core.Ui.Script;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Ui;

/// <summary>Windowless: step output naming of ui_run_script (a step must not replace run data).</summary>
public sealed class UiScriptOutputTests
{
    [Theory]
    [InlineData(null, "shot.png", "shot.png")]
    [InlineData("a", "shot.png", "a.png")]
    [InlineData("sub/b.PNG", "shot.png", "sub/b.PNG")]
    public void ScreenshotName_AddsOrKeepsPng(string? requested, string def, string expected) =>
        Assert.Equal(expected, UiScriptRunner.NormalizeOutputName(requested, def, ".png"));

    [Theory]
    [InlineData("result.json")]
    [InlineData("run-info.json")]
    [InlineData("model.rvt")]
    [InlineData("report.md")]
    public void ScreenshotName_RefusesNonImageExtensions(string requested) =>
        Assert.Throws<GatewayException>(() => UiScriptRunner.NormalizeOutputName(requested, "shot.png", ".png"));
}
