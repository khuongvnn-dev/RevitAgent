using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Tools;
using Xunit.Abstractions;

namespace RevitAgent.Tests;

/// <summary>
/// The default <c>core</c> profile lists only <see cref="ToolProfiles.CoreTools"/>. A hint or error message that tells the
/// agent to call a tool outside that list sends it to a tool it cannot see. Rule: a string literal in Core/Bridge code
/// that mentions a registered non-core tool inside longer text is a failure, unless the file is allow-listed below.
/// A literal that is exactly a tool name (a definition, a constant) is never flagged.
/// </summary>
public class CoreHintTests
{
    private static readonly Regex Literal = new("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"", RegexOptions.Compiled);

    /// <summary>
    /// Files allowed to name full-profile tools in prose (repo-relative, '/' separators). Rule: only code that a core-profile
    /// agent never reads the text of, or that exists to document the full tool set. Add a file only with a reason.
    /// </summary>
    private static readonly string[] AllowedFiles =
    {
        // Admin / install flow: bridge_install, bridge_status and revit_list_installed are Admin tools an owner or a
        // full-profile caller runs; the text is the tool's own output.
        "src/RevitAgent.Core/Bridge/BridgeClient.cs",
        "src/RevitAgent.Core/Bridge/BridgeStatusTool.cs",
        "src/RevitAgent.Core/Machine/MachineReadiness.cs",
        "src/RevitAgent.Core/Revit/RevitProcessService.cs",
        "src/RevitAgent.Core/Revit/Tools/RevitListInstalledTool.cs",
        "src/RevitAgent.Core/Revit/Tools/RevitGetStateTool.cs",

        // Definitions and descriptions of full-profile tools that cross-reference each other (read by full-profile callers).
        "src/RevitAgent.Core/Bridge/BridgeToolCatalog.cs",
        "src/RevitAgent.Core/Install/",
        "src/RevitAgent.Core/Scenario/",       // scenario_* and recorder_* tools, the scenario runner and its reports
        "src/RevitAgent.Bridge/Tools/DocumentTools.cs",
        "src/RevitAgent.Bridge/Tools/ScheduleTools.cs",
        "src/RevitAgent.Bridge/Tools/UiTools.cs",

        // UI script internals and descriptions of other tools (full-profile callers only).
        "src/RevitAgent.Core/Ui/ScreenshotPlan.cs",
        "src/RevitAgent.Core/Ui/UiaCore.cs",
        "src/RevitAgent.Core/Ui/Script/UiScriptRunner.cs",
        "src/RevitAgent.Core/RunStore/Tools/RunsGetTool.cs",

        // TODO(lead): remove after merge (these three are being reworded in parallel branches)
        "src/RevitAgent.Core/Revit/RevitReadiness.cs",
        "src/RevitAgent.Core/Ui/UiSafety.cs",
        "src/RevitAgent.Core/Tools/ToolRegistry.cs",
    };

    private readonly ITestOutputHelper _output;

    public CoreHintTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CodeMessages_DoNotNameToolsHiddenInTheCoreProfile()
    {
        await using var sp = TestGateway.BuildProvider(out _);
        var registered = sp.GetRequiredService<ToolRegistry>().Tools.Select(t => t.Name).ToArray();
        var hidden = registered.Except(ToolProfiles.CoreTools, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(hidden);
        var nameRegex = new Regex("(?<![A-Za-z0-9_])(" + string.Join("|", hidden.Select(Regex.Escape)) + ")(?![A-Za-z0-9_])", RegexOptions.Compiled);

        var root = FindRepoRoot();
        var hits = new List<string>();
        foreach (var dir in new[] { "src/RevitAgent.Core", "src/RevitAgent.Bridge" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/") || AllowedFiles.Any(a => rel.StartsWith(a, StringComparison.Ordinal)))
                {
                    continue;
                }

                var lineNo = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNo++;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match lit in Literal.Matches(line))
                    {
                        var body = lit.Value.Trim('"');
                        if (hidden.Contains(body, StringComparer.Ordinal))
                        {
                            continue;
                        }

                        foreach (Match m in nameRegex.Matches(body))
                        {
                            hits.Add($"{rel}:{lineNo} names '{m.Value}'");
                        }
                    }
                }
            }
        }

        foreach (var h in hits)
        {
            _output.WriteLine(h);
        }

        Assert.True(hits.Count == 0, "Messages name tools outside the core profile (reword, or allow-list the file with a reason):\n" + string.Join("\n", hits));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RevitAgent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("RevitAgent.slnx not found above the test binaries.");
    }
}
