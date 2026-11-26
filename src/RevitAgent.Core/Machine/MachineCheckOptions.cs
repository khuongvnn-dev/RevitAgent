namespace RevitAgent.Core.Machine;

/// <summary>Options for machine readiness check.</summary>
public sealed record MachineCheckOptions
{
    /// <summary>Revit release year to check (default 2024).</summary>
    public int Year { get; init; } = 2024;

    /// <summary>Override named pipe name for host check (default: revitagent-mcp).</summary>
    public string? PipeName { get; init; }

    /// <summary>Override runs directory for disk check (for testing).</summary>
    public string? RunsDir { get; init; }

    /// <summary>Override config directory for allowlist/telegram check (for testing).</summary>
    public string? ConfigDir { get; init; }
}
