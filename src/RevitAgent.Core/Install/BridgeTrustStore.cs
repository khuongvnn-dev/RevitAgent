using Microsoft.Win32;

namespace RevitAgent.Core.Install;

/// <summary>
/// Revit's per-user "Always Load" trust list for unsigned add-ins. Verified on this machine (Revit 2022/2024):
/// clicking "Always Load" in "Security - Unsigned Add-In" writes
/// <c>HKCU\SOFTWARE\Autodesk\Revit\Autodesk Revit &lt;year&gt;\CodeSigning</c>, value name = the manifest's
/// AddInId in lowercase without braces, REG_DWORD 1. Pre-writing that value for OUR AddInId means Revit
/// loads the Bridge without the modal prompt.
/// </summary>
public interface IBridgeTrustStore
{
    bool IsTrusted(int year, string addInId);

    void Trust(int year, string addInId);

    /// <summary>Removes the value; true when it existed.</summary>
    bool Untrust(int year, string addInId);

    /// <summary>Human-readable location of the value (for reports).</summary>
    string Describe(int year, string addInId);

    /// <summary>
    /// Removes every trust value for <paramref name="addInId"/> in any spelling (with/without braces, any case).
    /// Used for the retired v1 AddInId: the old setup script also wrote a braced value name, which Revit
    /// cannot parse ("Unrecognized Guid format" in every journal). Returns the removed value names.
    /// </summary>
    IReadOnlyList<string> RemoveAllSpellings(int year, string addInId);
}

/// <summary>Real registry implementation (HKCU only; touches only the given AddInId's value).</summary>
public sealed class RegistryBridgeTrustStore : IBridgeTrustStore
{
    public static string KeyPath(int year) => $@"SOFTWARE\Autodesk\Revit\Autodesk Revit {year}\CodeSigning";

    public static string ValueName(string addInId) => addInId.Trim().Trim('{', '}').ToLowerInvariant();

    public bool IsTrusted(int year, string addInId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath(year));
        return key?.GetValue(ValueName(addInId)) is int v && v == 1;
    }

    public void Trust(int year, string addInId)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath(year), writable: true);
        key.SetValue(ValueName(addInId), 1, RegistryValueKind.DWord);
    }

    public bool Untrust(int year, string addInId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath(year), writable: true);
        if (key?.GetValue(ValueName(addInId)) is null)
        {
            return false;
        }

        key.DeleteValue(ValueName(addInId), throwOnMissingValue: false);
        return true;
    }

    public string Describe(int year, string addInId) => $@"HKCU\{KeyPath(year)}\{ValueName(addInId)} (REG_DWORD 1)";

    public IReadOnlyList<string> RemoveAllSpellings(int year, string addInId)
    {
        var removed = new List<string>();
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath(year), writable: true);
        if (key is null)
        {
            return removed;
        }

        var target = ValueName(addInId);
        foreach (var name in key.GetValueNames())
        {
            if (string.Equals(ValueName(name), target, StringComparison.Ordinal))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                removed.Add($@"HKCU\{KeyPath(year)}\{name}");
            }
        }

        return removed;
    }
}

/// <summary>In-memory trust store for tests.</summary>
public sealed class InMemoryBridgeTrustStore : IBridgeTrustStore
{
    private readonly HashSet<string> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Values => _values;

    public bool IsTrusted(int year, string addInId) => _values.Contains(Key(year, addInId));

    public void Trust(int year, string addInId) => _values.Add(Key(year, addInId));

    public bool Untrust(int year, string addInId) => _values.Remove(Key(year, addInId));

    public string Describe(int year, string addInId) => $"memory:{Key(year, addInId)}";

    public IReadOnlyList<string> RemoveAllSpellings(int year, string addInId) =>
        Untrust(year, addInId) ? new[] { Describe(year, addInId) } : Array.Empty<string>();

    private static string Key(int year, string addInId) => year + "/" + RegistryBridgeTrustStore.ValueName(addInId);
}
