using System.Text.Json;

namespace RevitAgent.Core.RunStore;

public sealed record RunFolder
{
    public string Id { get; init; } = string.Empty;

    public string FolderPath { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string DataDir { get; init; } = string.Empty;

    public string EvidenceDir { get; init; } = string.Empty;

    public string ScenarioDir { get; init; } = string.Empty;

    public string ModelDir { get; init; } = string.Empty;

    public string LogsDir { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public string Suite { get; init; } = string.Empty;

    public string Trigger { get; init; } = string.Empty;
}

public sealed record RunFilter
{
    public string? Suite { get; init; }

    public string? Verdict { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record RunSummary
{
    public string Id { get; init; } = string.Empty;

    public string Suite { get; init; } = string.Empty;

    public string Trigger { get; init; } = "manual";

    public string Verdict { get; init; } = "UNKNOWN";

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string RunDir { get; init; } = string.Empty;

    public string? ReportMd { get; init; }

    public string? ReportHtml { get; init; }

    public string? Machine { get; init; }

    public string? User { get; init; }

    public int? RevitVersion { get; init; }

    public JsonElement? Counts { get; init; }
}

public sealed record AddinInfo(string Name, string? Path, string? Sha256);

public sealed record RunFinishInfo
{
    public DateTimeOffset EndedAt { get; init; }

    public string Verdict { get; init; } = "UNKNOWN";

    public int? Pid { get; init; }

    public int? RevitYear { get; init; }

    public string? RevitBuild { get; init; }

    public string? ModelSource { get; init; }

    public string? ModelCopy { get; init; }

    public string? ModelShaBefore { get; init; }

    public string? ModelShaAfter { get; init; }

    public bool ModelUnchanged { get; init; }

    public string? SessionId { get; init; }

    public string? PreviousRunId { get; init; }

    public string? RerunOf { get; init; }

    public object? Counts { get; init; }

    public IReadOnlyList<AddinInfo>? Addins { get; init; }
}

public sealed record RunFileInfo(string Path, long Size);

public sealed record RunDetails
{
    public string RunId { get; init; } = string.Empty;

    public string RunDir { get; init; } = string.Empty;

    public JsonElement Run { get; init; }

    public IReadOnlyList<RunFileInfo> Files { get; init; } = Array.Empty<RunFileInfo>();
}
