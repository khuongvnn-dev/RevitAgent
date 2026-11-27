using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Install;

/// <summary>
/// Everything the installer touches, injectable so unit tests run against temp folders and an in-memory
/// trust store (never the real %APPDATA% or registry).
/// </summary>
public sealed class BridgeInstallEnvironment
{
    /// <summary>Where installed copies live: <c>&lt;agent root&gt;\bridge\R&lt;yy&gt;\&lt;stamp&gt;\</c>.</summary>
    public required string InstallRoot { get; init; }

    /// <summary>Per-user add-in manifest folder of a Revit year (%APPDATA%\Autodesk\Revit\Addins\&lt;year&gt;).</summary>
    public required Func<int, string> UserAddinsDir { get; init; }

    public required IBridgeTrustStore Trust { get; init; }

    /// <summary>Installed Revit versions (RevitInstallations.Discover in production).</summary>
    public required Func<IReadOnlyList<RevitInstallation>> Installations { get; init; }

    /// <summary>Folders that contain per-year builds as <c>R&lt;yy&gt;\RevitAgent.Bridge.dll</c>, in preference order.</summary>
    public required Func<IReadOnlyList<string>> PayloadRoots { get; init; }

    /// <summary>Running Revit processes (to report which ones need a restart).</summary>
    public Func<IReadOnlyList<RevitProcessInfo>> RunningRevit { get; init; } = () => Array.Empty<RevitProcessInfo>();

    public static BridgeInstallEnvironment ForCurrentUser(AgentPaths paths, IRevitProcessProbe probe) => new()
    {
        InstallRoot = Path.Combine(paths.Root, "bridge"),
        UserAddinsDir = year => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", year.ToString()),
        Trust = new RegistryBridgeTrustStore(),
        Installations = RevitInstallations.Discover,
        PayloadRoots = DefaultPayloadRoots,
        RunningRevit = probe.ListRevit,
    };

    /// <summary>
    /// REVITAGENT_BRIDGE_PAYLOAD, then <c>&lt;app&gt;\bridge</c>, then the repo build folders
    /// (src\RevitAgent.Bridge\bin\Release and \Debug) found by walking up from the app folder.
    /// </summary>
    public static IReadOnlyList<string> DefaultPayloadRoots()
    {
        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable("REVITAGENT_BRIDGE_PAYLOAD") is { Length: > 0 } env)
        {
            roots.Add(env);
        }

        roots.Add(Path.Combine(AppContext.BaseDirectory, "bridge"));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "src", "RevitAgent.Bridge", "bin");
            if (Directory.Exists(bin))
            {
                roots.Add(Path.Combine(bin, "Release"));
                roots.Add(Path.Combine(bin, "Debug"));
                break;
            }
        }

        return roots;
    }
}

/// <summary>A per-year Bridge build available for installation.</summary>
public sealed record BridgePayload(int Year, string DllPath, string Stamp, string Sha256, DateTime BuiltUtc);

/// <summary>
/// Installs the Bridge per Revit year: copies the per-year build into a content-stamped folder
/// (<c>bridge\R24\2.0.0-1a2b3c4d5e\</c>, so a DLL locked by a running Revit is never overwritten — a changed
/// build goes side by side and the manifest is repointed), writes
/// <c>%APPDATA%\Autodesk\Revit\Addins\&lt;year&gt;\RevitAgent.Bridge.addin</c>, pre-trusts OUR AddInId in
/// Revit's CodeSigning list, removes the retired v1 manifest, and cleans up unlocked old copies.
/// </summary>
public sealed class BridgeInstaller
{
    private readonly BridgeInstallEnvironment _env;

    public BridgeInstaller(BridgeInstallEnvironment env) => _env = env;

    public BridgeInstallEnvironment Environment => _env;

    public static string YearFolder(int year) => "R" + (year % 100).ToString("00");

    public IReadOnlyList<int> InstalledYears() => _env.Installations().Select(i => i.Year).ToArray();

    /// <summary>Newest build for <paramref name="year"/> across payload roots (or only <paramref name="sourceDir"/>).</summary>
    public BridgePayload? FindPayload(int year, string? sourceDir = null)
    {
        var roots = sourceDir is null ? _env.PayloadRoots() : new[] { sourceDir };
        BridgePayload? best = null;
        foreach (var root in roots)
        {
            var dll = Path.Combine(root, YearFolder(year), BridgeWire.AssemblyFileName);
            if (!File.Exists(dll))
            {
                continue;
            }

            var built = File.GetLastWriteTimeUtc(dll);
            if (best is null || built > best.BuiltUtc)
            {
                var hash = Sha256(dll);
                best = new BridgePayload(year, Path.GetFullPath(dll), StampFor(dll, hash), hash, built);
            }
        }

        return best;
    }

    public IReadOnlyList<object> List()
    {
        var running = SafeRunning();
        var result = new List<object>();
        foreach (var inst in _env.Installations())
        {
            var year = inst.Year;
            var manifestPath = ManifestPath(year);
            var manifest = ReadManifest(manifestPath);
            var payload = FindPayload(year);
            string? installedHash = null;
            if (manifest?.Assembly is { } asm && File.Exists(asm))
            {
                installedHash = Sha256(asm);
            }

            result.Add(new Dictionary<string, object?>
            {
                ["year"] = year,
                ["revit_dir"] = inst.InstallDir,
                ["framework"] = inst.IsDotNetCore ? "net8.0-windows" : "net48",
                ["installed"] = manifest is { IsOurs: true } && installedHash is not null,
                ["manifest"] = File.Exists(manifestPath) ? manifestPath : null,
                ["manifest_addin_id"] = manifest?.AddInId,
                ["assembly"] = manifest?.Assembly,
                ["assembly_exists"] = installedHash is not null,
                ["installed_stamp"] = manifest?.Assembly is { } a ? new DirectoryInfo(Path.GetDirectoryName(a)!).Name : null,
                ["trusted"] = _env.Trust.IsTrusted(year, BridgeWire.AddInId),
                ["trust_value"] = _env.Trust.Describe(year, BridgeWire.AddInId),
                ["legacy_manifests"] = FindLegacyManifests(year),
                ["available_build"] = payload is null ? null : new { path = payload.DllPath, stamp = payload.Stamp, built_utc = payload.BuiltUtc },
                ["up_to_date"] = payload is not null && installedHash is not null && string.Equals(payload.Sha256, installedHash, StringComparison.OrdinalIgnoreCase),
                ["running_pids"] = running.Where(p => p.Year == year).Select(p => p.Pid).ToArray(),
            });
        }

        return result;
    }

    /// <summary>Installs for each year (default: every installed Revit). Per-year failures are reported, not thrown.</summary>
    public IReadOnlyList<object> Install(IReadOnlyCollection<int>? years, string? sourceDir = null)
    {
        var installations = _env.Installations();
        var targets = ResolveYears(years, installations);
        var running = SafeRunning();
        var results = new List<object>();
        foreach (var year in targets)
        {
            try
            {
                if (installations.All(i => i.Year != year))
                {
                    results.Add(Failure(year, Contracts.Protocol.GatewayErrorCodes.NotFound, $"Revit {year} is not installed on this machine (bridge_list_installs shows installed versions)."));
                    continue;
                }

                results.Add(InstallYear(year, sourceDir, running));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
            {
                results.Add(Failure(year, Contracts.Protocol.GatewayErrorCodes.Conflict, $"Installing the Bridge for Revit {year} failed: {ex.Message}"));
            }
            catch (GatewayException ex)
            {
                results.Add(Failure(year, ex.Code, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<object> Uninstall(IReadOnlyCollection<int>? years)
    {
        var installations = _env.Installations();
        var targets = years is { Count: > 0 } ? years.Distinct().OrderBy(y => y).ToArray() : installations.Select(i => i.Year).ToArray();
        var running = SafeRunning();
        var results = new List<object>();
        foreach (var year in targets)
        {
            try
            {
                results.Add(UninstallYear(year, running));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(Failure(year, Contracts.Protocol.GatewayErrorCodes.Conflict, $"Uninstalling the Bridge for Revit {year} failed: {ex.Message}"));
            }
        }

        return results;
    }

    private Dictionary<string, object?> UninstallYear(int year, IReadOnlyList<RevitProcessInfo> running)
    {
        var removed = new List<string>();
        var kept = new List<string>();
        var manifestPath = ManifestPath(year);
        var manifest = ReadManifest(manifestPath);
        if (manifest is { IsOurs: true })
        {
            File.Delete(manifestPath);
            removed.Add(manifestPath);
        }
        else if (manifest is not null)
        {
            kept.Add(manifestPath + " (not a RevitAgent manifest; left alone)");
        }

        foreach (var legacy in FindLegacyManifests(year))
        {
            File.Delete(legacy);
            removed.Add(legacy);
        }

        if (_env.Trust.Untrust(year, BridgeWire.AddInId))
        {
            removed.Add(_env.Trust.Describe(year, BridgeWire.AddInId));
        }

        removed.AddRange(_env.Trust.RemoveAllSpellings(year, BridgeWire.LegacyAddInId));

        var yearDir = Path.Combine(_env.InstallRoot, YearFolder(year));
        if (Directory.Exists(yearDir))
        {
            foreach (var dir in Directory.GetDirectories(yearDir))
            {
                if (TryDeleteDirectory(dir))
                {
                    removed.Add(dir);
                }
                else
                {
                    kept.Add(dir + " (locked by a running Revit; run bridge_uninstall again after closing it)");
                }
            }

            TryDelete(Path.Combine(yearDir, "install.json"));
            if (!Directory.EnumerateFileSystemEntries(yearDir).Any())
            {
                Directory.Delete(yearDir);
            }
        }

        return new Dictionary<string, object?>
        {
            ["year"] = year,
            ["ok"] = true,
            ["removed"] = removed,
            ["kept"] = kept,
            ["restart_required_pids"] = running.Where(p => p.Year == year).Select(p => p.Pid).ToArray(),
            ["note"] = "A Revit that is already running keeps its loaded Bridge until it is restarted.",
        };
    }

    public string ManifestPath(int year) => Path.Combine(_env.UserAddinsDir(year), BridgeWire.ManifestFileName);

    private object InstallYear(int year, string? sourceDir, IReadOnlyList<RevitProcessInfo> running)
    {
        var payload = FindPayload(year, sourceDir) ?? throw GatewayException.NotFound(
            $"No Bridge build for Revit {year} ({YearFolder(year)}\\{BridgeWire.AssemblyFileName}) under {string.Join("; ", sourceDir is null ? _env.PayloadRoots() : new[] { sourceDir })}. Build it with scripts/build-bridge.ps1.");

        var manifestPath = ManifestPath(year);
        var existing = ReadManifest(manifestPath);
        if (existing is { IsOurs: false })
        {
            throw new GatewayException(Contracts.Protocol.GatewayErrorCodes.Conflict,
                $"{manifestPath} belongs to another add-in (AddInId {existing.AddInId}); not overwriting it.");
        }

        // 1. Content-stamped copy (never overwrites a file a running Revit may have loaded).
        var yearDir = Path.Combine(_env.InstallRoot, YearFolder(year));
        var targetDir = Path.Combine(yearDir, payload.Stamp);
        var targetDll = Path.Combine(targetDir, BridgeWire.AssemblyFileName);
        var reused = File.Exists(targetDll) && string.Equals(Sha256(targetDll), payload.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!reused)
        {
            if (Directory.Exists(targetDir) && !TryDeleteDirectory(targetDir))
            {
                targetDir = Path.Combine(yearDir, payload.Stamp + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                targetDll = Path.Combine(targetDir, BridgeWire.AssemblyFileName);
            }

            Directory.CreateDirectory(targetDir);
            File.Copy(payload.DllPath, targetDll, overwrite: false);
            var pdb = Path.ChangeExtension(payload.DllPath, ".pdb");
            if (File.Exists(pdb))
            {
                File.Copy(pdb, Path.ChangeExtension(targetDll, ".pdb"), overwrite: false);
            }
        }

        // 2. Manifest (atomic replace).
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        WriteManifest(manifestPath, targetDll);

        // 3. Retired v1 manifests.
        var removedLegacy = new List<string>();
        foreach (var legacy in FindLegacyManifests(year))
        {
            File.Delete(legacy);
            removedLegacy.Add(legacy);
        }

        // 4. Pre-trust OUR AddInId (avoids the "Security - Unsigned Add-In" prompt at startup) and drop the
        //    retired v1 trust values (the v1 script also wrote a braced name Revit cannot parse).
        _env.Trust.Trust(year, BridgeWire.AddInId);
        var removedLegacyTrust = _env.Trust.RemoveAllSpellings(year, BridgeWire.LegacyAddInId);

        // 5. Clean older copies that are not locked.
        var cleaned = new List<string>();
        var locked = new List<string>();
        foreach (var dir in Directory.GetDirectories(yearDir))
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryDeleteDirectory(dir))
            {
                cleaned.Add(dir);
            }
            else
            {
                locked.Add(dir);
            }
        }

        File.WriteAllText(Path.Combine(yearDir, "install.json"), JsonSerializer.Serialize(new
        {
            year,
            stamp = payload.Stamp,
            sha256 = payload.Sha256,
            source = payload.DllPath,
            assembly = targetDll,
            manifest = manifestPath,
            installed_utc = DateTime.UtcNow,
        }, GatewayJson.Options));

        var restart = running.Where(p => p.Year == year).Select(p => p.Pid).ToArray();
        return new Dictionary<string, object?>
        {
            ["year"] = year,
            ["ok"] = true,
            ["stamp"] = payload.Stamp,
            ["source"] = payload.DllPath,
            ["assembly"] = targetDll,
            ["reused_existing_copy"] = reused,
            ["manifest"] = manifestPath,
            ["addin_id"] = BridgeWire.AddInId,
            ["trust_value"] = _env.Trust.Describe(year, BridgeWire.AddInId),
            ["removed_legacy_manifests"] = removedLegacy,
            ["removed_legacy_trust_values"] = removedLegacyTrust,
            ["cleaned_old_copies"] = cleaned,
            ["locked_old_copies"] = locked,
            ["restart_required_pids"] = restart,
            ["note"] = restart.Length > 0
                ? $"Revit {year} (pid {string.Join(", ", restart)}) is running: it loads the Bridge only at startup, so restart it."
                : $"The next Revit {year} started will load the Bridge (bridge_status confirms it).",
        };
    }

    private static IReadOnlyList<int> ResolveYears(IReadOnlyCollection<int>? years, IReadOnlyList<RevitInstallation> installations) =>
        years is { Count: > 0 } ? years.Distinct().OrderBy(y => y).ToArray() : installations.Select(i => i.Year).ToArray();

    private IReadOnlyList<RevitProcessInfo> SafeRunning()
    {
        try
        {
            return _env.RunningRevit();
        }
        catch (Exception)
        {
            return Array.Empty<RevitProcessInfo>();
        }
    }

    private List<string> FindLegacyManifests(int year)
    {
        var found = new List<string>();
        var dir = _env.UserAddinsDir(year);
        if (!Directory.Exists(dir))
        {
            return found;
        }

        foreach (var file in Directory.GetFiles(dir, "*.addin"))
        {
            var m = ReadManifest(file);
            if (m is not null && m.AllIds.Count > 0 && m.AllIds.All(id => string.Equals(id, BridgeWire.LegacyAddInId, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(file);
            }
        }

        return found;
    }

    private static void WriteManifest(string path, string assembly)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("RevitAddIns",
                new XElement("AddIn",
                    new XAttribute("Type", "Application"),
                    new XElement("Name", BridgeWire.AddInName),
                    new XElement("Assembly", assembly),
                    new XElement("AddInId", BridgeWire.AddInId),
                    new XElement("FullClassName", BridgeWire.FullClassName),
                    new XElement("VendorId", BridgeWire.VendorId),
                    new XElement("VendorDescription", "RevitAgent - MCP gateway bridge (installed by bridge_install; remove with bridge_uninstall)"))));
        var tmp = path + ".tmp";
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (var writer = XmlWriter.Create(tmp, settings))
        {
            doc.Save(writer);
        }

        File.Move(tmp, path, overwrite: true);
    }

    internal sealed record ManifestInfo(string? AddInId, string? Assembly, IReadOnlyList<string> AllIds)
    {
        public bool IsOurs => AllIds.Count > 0 && AllIds.All(id =>
            string.Equals(id, BridgeWire.AddInId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, BridgeWire.LegacyAddInId, StringComparison.OrdinalIgnoreCase));
    }

    internal static ManifestInfo? ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Load(path);
            var addins = doc.Root?.Elements().Where(e => e.Name.LocalName == "AddIn").ToArray() ?? Array.Empty<XElement>();
            var ids = addins
                .Select(a => (a.Elements().FirstOrDefault(e => e.Name.LocalName is "AddInId" or "ClientId")?.Value ?? string.Empty).Trim().Trim('{', '}'))
                .Where(s => s.Length > 0)
                .ToArray();
            var first = addins.FirstOrDefault();
            return new ManifestInfo(ids.FirstOrDefault(), first?.Elements().FirstOrDefault(e => e.Name.LocalName == "Assembly")?.Value.Trim(), ids);
        }
        catch (XmlException ex)
        {
            if (string.Equals(Path.GetFileName(path), BridgeWire.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                // Exactly our own file name but unreadable: it is ours to repair (install overwrites, uninstall removes).
                Trace.TraceWarning($"RevitAgent: {path} is corrupt ({ex.Message}); treating it as the RevitAgent manifest.");
                return new ManifestInfo(BridgeWire.AddInId, null, new[] { BridgeWire.AddInId });
            }

            return new ManifestInfo(null, null, Array.Empty<string>());
        }
    }

    private static string StampFor(string dll, string sha256)
    {
        var version = FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "0.0.0";
        var parts = version.Split('.');
        var shortVersion = parts.Length >= 3 ? string.Join(".", parts.Take(3)) : version;
        return shortVersion + "-" + sha256[..10].ToLowerInvariant();
    }

    private static string Sha256(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, object?> Failure(int year, string code, string message) => new()
    {
        ["year"] = year,
        ["ok"] = false,
        ["error"] = new { code, message },
    };
}
