using System.Text.Json.Serialization;

namespace RevitAgent.Core.Revit;

/// <summary>One add-in the owner trusts (file <c>%REVITAGENT_HOME%\config\addin-allowlist.json</c>, written only by the dashboard).</summary>
public sealed record AddinAllowlistEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    /// <summary>Add-in name as Revit shows it in the "Name:" line (compared case-insensitively).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Glob over the normalized full path of the add-in DLL: <c>*</c> = any chars except <c>\</c>, <c>**</c> = any chars, <c>?</c> = one char except <c>\</c>.</summary>
    [JsonPropertyName("path_pattern")]
    public string PathPattern { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    [JsonPropertyName("added_at")]
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>On-disk shape of the allowlist file.</summary>
public sealed record AddinAllowlistFile
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("entries")]
    public List<AddinAllowlistEntry> Entries { get; init; } = new();
}

/// <summary>Result of reading the allowlist file. <see cref="Error"/> set = unreadable / unknown version (decisions fail closed).</summary>
public sealed record AddinAllowlistSnapshot(IReadOnlyList<AddinAllowlistEntry> Entries, string? Error = null)
{
    public static AddinAllowlistSnapshot Empty { get; } = new(Array.Empty<AddinAllowlistEntry>());
}

/// <summary>What to press on the "Security - Unsigned Add-In" dialog.</summary>
public enum AddinTrustAction
{
    /// <summary>Not decided here: the dialog stays human-only (parse failure, unreadable allowlist, ...).</summary>
    NoDecision,

    /// <summary>Press "Load Once" (CommandButton_1002): the add-in is on the allowlist.</summary>
    LoadOnce,

    /// <summary>Press "Do Not Load" (CommandButton_1003): the add-in is not on the allowlist.</summary>
    DoNotLoad,
}

/// <summary>Decision for one unsigned-add-in prompt.</summary>
public sealed record AddinTrustDecision(AddinTrustAction Action, string? Name, string? Location, string? MatchedEntryId, string Reason)
{
    public string ButtonId => Action switch
    {
        AddinTrustAction.LoadOnce => AddinAllowlistPolicy.LoadOnceButtonId,
        AddinTrustAction.DoNotLoad => AddinAllowlistPolicy.DoNotLoadButtonId,
        _ => string.Empty,
    };

    public string Label => Action switch
    {
        AddinTrustAction.LoadOnce => "Load Once",
        AddinTrustAction.DoNotLoad => "Do Not Load",
        _ => "none",
    };

    /// <summary>load_once | do_not_load | none.</summary>
    public string Code => Action switch
    {
        AddinTrustAction.LoadOnce => "load_once",
        AddinTrustAction.DoNotLoad => "do_not_load",
        _ => "none",
    };
}

/// <summary>What the gateway did with an unsigned-add-in prompt (carried by <see cref="HandledDialog"/>).</summary>
public sealed record AddinTrustInfo
{
    public string? Name { get; init; }

    public string? Location { get; init; }

    public string? MatchedEntryId { get; init; }

    /// <summary>load_once | do_not_load.</summary>
    public string Decision { get; init; } = string.Empty;
}
