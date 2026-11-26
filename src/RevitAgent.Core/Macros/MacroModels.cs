using System.Text.Json.Nodes;

namespace RevitAgent.Core.Macros;

/// <summary>
/// A verified recipe that composes existing gateway tools (data, not code): parameters, a fixed list of steps
/// (each one a normal tool call through the registry) and a result template. Loaded from YAML by <see cref="MacroLoader"/>.
/// </summary>
public sealed class MacroDefinition
{
    /// <summary>kebab-case, unique.</summary>
    public required string Name { get; init; }

    /// <summary>One line (≤ 120 chars) shown by macro_list.</summary>
    public required string Summary { get; init; }

    /// <summary>Longer text shown only when one macro is described.</summary>
    public string? Description { get; init; }

    /// <summary>Informational prerequisites, e.g. <c>bridge</c>, <c>ui</c>.</summary>
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

    public IReadOnlyList<MacroParam> Params { get; init; } = Array.Empty<MacroParam>();

    public required IReadOnlyList<MacroStep> Steps { get; init; }

    /// <summary>Result template (resolved after the last step); null = no result.</summary>
    public JsonNode? Returns { get; init; }

    /// <summary>Where it was loaded from (embedded resource or file name).</summary>
    public string Source { get; init; } = string.Empty;

    public MacroParam? Param(string name) => Params.FirstOrDefault(p => p.Name == name);
}

/// <summary>One macro parameter. <see cref="Default"/> is already coerced to <see cref="Type"/>.</summary>
public sealed record MacroParam(string Name, string Type, bool Required, JsonNode? Default, string? Description, string ItemType = MacroTypes.String);

/// <summary>One step: a gateway tool call with templated arguments, an optional condition and output checks.</summary>
public sealed record MacroStep(string Id, string Tool, JsonObject Args, string? When, IReadOnlyList<MacroExpect> Expect);

/// <summary>A check on a step's output at <see cref="Path"/>: not empty and/or an exact item count.</summary>
public sealed record MacroExpect(string Path, IReadOnlyList<object> Segments, bool NotEmpty, int? Count, string? Message);

/// <summary>A problem found while loading macros (the macro is not loaded).</summary>
public sealed record MacroIssue(string Source, string? Macro, string Message)
{
    public override string ToString() => $"{Source}{(Macro is null ? string.Empty : $" ({Macro})")}: {Message}";
}

/// <summary>Parameter types a macro may declare.</summary>
public static class MacroTypes
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Array = "array";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { String, Integer, Number, Boolean, Array };

    public static readonly IReadOnlySet<string> ItemTypes = new HashSet<string>(StringComparer.Ordinal) { String, Integer, Number, Boolean };

    /// <summary>Short form used by macro_list: int, string, number, bool, string[].</summary>
    public static string Short(string type, string itemType) => type switch
    {
        Integer => "int",
        Boolean => "bool",
        Array => Short(itemType, String) + "[]",
        _ => type,
    };
}

/// <summary>Error code of a failed step <c>expect</c> check (not a Bridge/Contracts code: macros live only in the gateway).</summary>
public static class MacroErrorCodes
{
    public const string ExpectationFailed = "EXPECTATION_FAILED";
}
