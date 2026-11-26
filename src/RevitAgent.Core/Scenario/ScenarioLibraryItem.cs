namespace RevitAgent.Core.Scenario;

/// <summary>
/// A scenario discovered in the scenario library.
/// </summary>
public sealed class ScenarioLibraryItem
{
    public required string Directory { get; init; }

    public required string Name { get; init; }

    /// <summary>The scenario's <c>name</c> from scenario.yaml (the folder name is <see cref="Name"/>).</summary>
    public required string ScenarioName { get; init; }

    /// <summary>Alias of <see cref="ScenarioName"/> kept for existing UI bindings.</summary>
    public string Suite => ScenarioName;

    public int? RevitVersion { get; init; }

    public string? Description { get; init; }

    public string? LastVerdict { get; init; }

    public string? LastRunId { get; init; }

    public DateTimeOffset? LastRunTime { get; init; }
}
