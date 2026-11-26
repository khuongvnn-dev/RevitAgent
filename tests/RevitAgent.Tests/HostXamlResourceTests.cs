using System.IO;
using System.Text.RegularExpressions;

namespace RevitAgent.Tests;

/// <summary>
/// A StaticResource key that is not defined crashes the dashboard at start (XamlParseException), and the Shim then
/// silently falls back to headless mode (seen 2026-11-25: ScenarioRunnerView used a key that only MainWindow defined).
/// A separately compiled view resolves StaticResource only against its own file and the App dictionaries.
/// </summary>
public sealed class HostXamlResourceTests
{
    private static readonly Regex StaticRef = new(@"\{StaticResource\s+([A-Za-z_][\w.]*)\s*[,}]", RegexOptions.Compiled);
    private static readonly Regex KeyDef = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(HostDir(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            data.Add(Path.GetRelativePath(HostDir(), f));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EveryStaticResource_IsDefinedInTheSameFileOrInTheAppDictionaries(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(HostDir(), relativePath));
        var known = new HashSet<string>(KeyDef.Matches(text).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        known.UnionWith(AppDictionaryKeys());

        var missing = StaticRef.Matches(text).Select(m => m.Groups[1].Value).Where(k => !known.Contains(k)).Distinct().ToArray();

        Assert.True(missing.Length == 0, $"{relativePath} uses StaticResource key(s) defined neither in the file nor in App.xaml's dictionaries: {string.Join(", ", missing)}");
    }

    private static IEnumerable<string> AppDictionaryKeys()
    {
        var app = File.ReadAllText(Path.Combine(HostDir(), "App.xaml"));
        foreach (Match m in Regex.Matches(app, @"Source=""/?([^""]+\.xaml)"""))
        {
            var dict = Path.Combine(HostDir(), m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            foreach (Match k in KeyDef.Matches(File.ReadAllText(dict)))
            {
                yield return k.Groups[1].Value;
            }
        }

        foreach (Match k in KeyDef.Matches(app))
        {
            yield return k.Groups[1].Value;
        }
    }

    private static string HostDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RevitAgent.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("RevitAgent.slnx not found"), "src", "RevitAgent.Host");
    }
}
