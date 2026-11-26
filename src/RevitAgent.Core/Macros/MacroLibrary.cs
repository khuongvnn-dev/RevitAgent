using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Composition;

namespace RevitAgent.Core.Macros;

/// <summary>
/// The macros the gateway offers. Built-in macros are the YAML files in <c>Macros\Builtin</c>, embedded in
/// RevitAgent.Core (adding one = a YAML file + rebuilding Core; no new MCP tool, no Bridge rebuild).
/// Invalid files are left out and listed in <see cref="Issues"/>.
/// </summary>
public sealed class MacroLibrary
{
    public const string ResourcePrefix = "RevitAgent.Core.Macros.Builtin.";

    public MacroLibrary()
        : this(MacroLoader.Load(ReadBuiltins()))
    {
    }

    private MacroLibrary(MacroLoadResult loaded)
    {
        Macros = loaded.Macros.OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
        Issues = loaded.Issues.ToArray();
    }

    public IReadOnlyList<MacroDefinition> Macros { get; }

    public IReadOnlyList<MacroIssue> Issues { get; }

    public IReadOnlyList<string> Names => Macros.Select(m => m.Name).ToArray();

    public MacroDefinition? Find(string name) => Macros.FirstOrDefault(m => string.Equals(m.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A library made of the given YAML texts (tests, tools).</summary>
    public static MacroLibrary FromYaml(params (string Source, string Yaml)[] files) => new(MacroLoader.Load(files));

    /// <summary>The embedded built-in macro files: (resource name, YAML text).</summary>
    public static IReadOnlyList<(string Source, string Yaml)> ReadBuiltins()
    {
        var assembly = typeof(MacroLibrary).Assembly;
        var list = new List<(string, string)>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((name[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return list;
    }
}

/// <summary>Registers the macro library (macro_list / macro_run are discovered like every tool).</summary>
public sealed class MacroModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services) => services.TryAddSingleton<MacroLibrary>();
}
