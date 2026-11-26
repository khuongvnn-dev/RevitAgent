using System.Text;
using System.Text.Json;

namespace RevitAgent.Core.Revit;

internal static class AddinAllowlistJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// READ-ONLY view of the add-in allowlist (<c>%REVITAGENT_HOME%\config\addin-allowlist.json</c>). This is the only
/// allowlist type MCP tools and the readiness gate may depend on; writing is <see cref="AddinAllowlistStore"/>, used by
/// the dashboard only.
/// </summary>
public class AddinAllowlist
{
    public const string FileName = "addin-allowlist.json";

    private readonly AgentPaths _paths;

    public AddinAllowlist(AgentPaths paths) => _paths = paths;

    public string FilePath => Path.Combine(_paths.ConfigDir, FileName);

    /// <summary>Reads the file. Missing file = empty list (valid); corrupt / unknown version = <see cref="AddinAllowlistSnapshot.Error"/>.</summary>
    public virtual AddinAllowlistSnapshot Load()
    {
        var path = FilePath;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return AddinAllowlistSnapshot.Empty;
                }

                var file = JsonSerializer.Deserialize<AddinAllowlistFile>(File.ReadAllText(path, Encoding.UTF8), AddinAllowlistJson.Options);
                if (file is null)
                {
                    return new AddinAllowlistSnapshot(Array.Empty<AddinAllowlistEntry>(), "file is empty");
                }

                if (file.Version != AddinAllowlistFile.CurrentVersion)
                {
                    return new AddinAllowlistSnapshot(Array.Empty<AddinAllowlistEntry>(), $"unsupported version {file.Version}");
                }

                return new AddinAllowlistSnapshot(file.Entries.Where(e => e is not null).ToArray());
            }
            catch (JsonException ex)
            {
                return new AddinAllowlistSnapshot(Array.Empty<AddinAllowlistEntry>(), "invalid JSON: " + ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 4)
                {
                    return new AddinAllowlistSnapshot(Array.Empty<AddinAllowlistEntry>(), "cannot read file: " + ex.Message);
                }

                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Name Revit shows for RevitAgent's own Bridge add-in (its manifest Name).</summary>
    public const string BridgeAddinName = "RevitAgent Bridge";

    public const string BuiltInBridgeEntryId = "builtin:revitagent-bridge";

    /// <summary>
    /// Built-in entry, never written to the file: RevitAgent's own Bridge exactly where bridge_install deploys it
    /// (<c>%REVITAGENT_HOME%\bridge\R&lt;yy&gt;\&lt;stamp&gt;\RevitAgent.Bridge.dll</c>). Installing the Bridge is itself an Admin action
    /// (dashboard or bridge_install, which deploys only the gateway's own build), so its prompt is pre-approved.
    /// Live finding 2026-11-25: Revit asked for the Bridge despite its CodeSigning trust value and the gateway answered
    /// Do Not Load because it was not listed.
    /// </summary>
    public AddinAllowlistEntry BuiltInBridgeEntry => new()
    {
        Id = BuiltInBridgeEntryId,
        Name = BridgeAddinName,
        PathPattern = Path.Combine(_paths.Root, "bridge", "R??", "*", "RevitAgent.Bridge.dll"),
        Enabled = true,
        Note = "Built-in: RevitAgent Bridge installed by the gateway",
    };

    /// <summary>Decision for the prompt text of a "Security - Unsigned Add-In" dialog (file entries + the built-in Bridge entry).</summary>
    public AddinTrustDecision Decide(IEnumerable<string>? dialogTexts)
    {
        var snapshot = Load();
        return AddinAllowlistPolicy.Decide(dialogTexts, snapshot with { Entries = snapshot.Entries.Append(BuiltInBridgeEntry).ToArray() });
    }
}

/// <summary>
/// Write side of the allowlist. Used by the Host dashboard only: no MCP tool may depend on this type (a test enforces it),
/// so an AI agent can never add itself to the list.
/// </summary>
public sealed class AddinAllowlistStore : AddinAllowlist
{
    private readonly object _gate = new();

    public AddinAllowlistStore(AgentPaths paths)
        : base(paths)
    {
    }

    /// <summary>Replaces the whole list. Entries with an invalid name/pattern are rejected (ArgumentException).</summary>
    public void Save(IEnumerable<AddinAllowlistEntry> entries)
    {
        var list = entries.ToList();
        foreach (var e in list)
        {
            if (string.IsNullOrWhiteSpace(e.Name))
            {
                throw new ArgumentException("Add-in name is required.");
            }

            if (AddinAllowlistPolicy.ValidatePattern(e.PathPattern) is { } problem)
            {
                throw new ArgumentException($"Invalid path pattern '{e.PathPattern}': {problem}.");
            }
        }

        lock (_gate)
        {
            var json = JsonSerializer.Serialize(new AddinAllowlistFile { Entries = list }, AddinAllowlistJson.Options);
            var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>Adds an entry (new id when empty) and saves.</summary>
    public AddinAllowlistEntry Add(AddinAllowlistEntry entry)
    {
        lock (_gate)
        {
            var e = string.IsNullOrWhiteSpace(entry.Id) ? entry with { Id = Guid.NewGuid().ToString("D") } : entry;
            var current = LoadForUpdate();
            Save(current.Append(e));
            return e;
        }
    }

    /// <summary>Replaces the entry with the same id; false when not found.</summary>
    public bool Update(AddinAllowlistEntry entry)
    {
        lock (_gate)
        {
            var current = LoadForUpdate();
            if (current.All(e => e.Id != entry.Id))
            {
                return false;
            }

            Save(current.Select(e => e.Id == entry.Id ? entry : e));
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var current = LoadForUpdate();
            var keep = current.Where(e => e.Id != id).ToList();
            if (keep.Count == current.Count)
            {
                return false;
            }

            Save(keep);
            return true;
        }
    }

    public bool SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var current = LoadForUpdate();
            if (current.All(e => e.Id != id))
            {
                return false;
            }

            Save(current.Select(e => e.Id == id ? e with { Enabled = enabled } : e));
            return true;
        }
    }

    // An unreadable file is never silently overwritten by an incremental change.
    private List<AddinAllowlistEntry> LoadForUpdate()
    {
        var snap = Load();
        if (snap.Error is not null)
        {
            throw new InvalidOperationException($"{FilePath} is unreadable ({snap.Error}); fix or delete it first.");
        }

        return snap.Entries.ToList();
    }
}
