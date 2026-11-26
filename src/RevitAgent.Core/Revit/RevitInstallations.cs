using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RevitAgent.Core.Revit;

/// <summary>One installed Revit release found on this machine.</summary>
public sealed record RevitInstallation(
    int Year,
    string InstallDir,
    string ExePath,
    string? ProductVersion,
    string? SamplesDir,
    /// <summary>Per-user add-in manifest folder (%APPDATA%\Autodesk\Revit\Addins\&lt;year&gt;).</summary>
    string UserAddinsDir,
    /// <summary>All-users add-in manifest folder (%ProgramData%\Autodesk\Revit\Addins\&lt;year&gt;).</summary>
    string MachineAddinsDir)
{
    /// <summary>Revit 2025+ runs on .NET 8; 2024 and older on .NET Framework 4.8.</summary>
    public bool IsDotNetCore => Year >= 2025;
}

/// <summary>
/// Finds installed Revit versions (shared by the Bridge installer and revit_launch).
/// Sources, in order: HKLM\SOFTWARE\Autodesk\Revit\&lt;year&gt;\REVIT-xx:yyyy\InstallationLocation,
/// the Windows uninstall entries "Revit &lt;year&gt;", and the default
/// "C:\Program Files\Autodesk\Revit &lt;year&gt;" folder. Only folders that contain Revit.exe count.
/// Works with installs on any drive (e.g. D:\Autodesk\Revit 2024).
/// </summary>
public static class RevitInstallations
{
    public const int MinYear = 2019;
    public const int MaxYear = 2030;

    private static readonly Regex YearKey = new(@"^(20\d\d)$", RegexOptions.Compiled);
    private static readonly Regex UninstallName = new(@"^(?:Autodesk\s+)?Revit\s+(20\d\d)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<RevitInstallation> Discover()
    {
        var dirs = new Dictionary<int, string>();

        using (var revit = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\Revit"))
        {
            foreach (var name in revit?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                var m = YearKey.Match(name);
                if (!m.Success)
                {
                    continue;
                }

                var year = int.Parse(m.Groups[1].Value);
                using var yearKey = revit!.OpenSubKey(name);
                foreach (var sub in yearKey?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    if (!sub.StartsWith("REVIT-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var product = yearKey!.OpenSubKey(sub);
                    if (product?.GetValue("InstallationLocation") is string loc && HasExe(loc))
                    {
                        dirs.TryAdd(year, loc);
                    }
                }
            }
        }

        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(root);
            foreach (var id in uninstall?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var entry = uninstall!.OpenSubKey(id);
                if (entry?.GetValue("DisplayName") is not string display)
                {
                    continue;
                }

                var m = UninstallName.Match(display.Trim());
                if (m.Success && entry.GetValue("InstallLocation") is string loc && HasExe(loc))
                {
                    dirs.TryAdd(int.Parse(m.Groups[1].Value), loc);
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        for (var year = MinYear; year <= MaxYear; year++)
        {
            var def = Path.Combine(programFiles, "Autodesk", $"Revit {year}");
            if (!dirs.ContainsKey(year) && HasExe(def))
            {
                dirs[year] = def;
            }
        }

        return dirs.OrderBy(kv => kv.Key).Select(kv => Create(kv.Key, kv.Value)).ToArray();
    }

    public static RevitInstallation? Find(int year) => Discover().FirstOrDefault(i => i.Year == year);

    /// <summary>Year of a running Revit.exe from its file version (e.g. 24.3.60.12 → 2024), or null.</summary>
    public static int? YearOfExecutable(string exePath)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exePath);
            if (v.FileMajorPart is >= 19 and <= 40)
            {
                return 2000 + v.FileMajorPart;
            }

            var m = Regex.Match(v.ProductName ?? string.Empty, @"20\d\d");
            return m.Success ? int.Parse(m.Value) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static RevitInstallation Create(int year, string dir)
    {
        var full = Path.GetFullPath(dir);
        var exe = Path.Combine(full, "Revit.exe");
        string? version = null;
        try
        {
            version = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        }
        catch (Exception)
        {
        }

        string? samples = null;
        using (var rcl = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Autodesk\Revit\{year}\RCL"))
        {
            if (rcl?.GetValue("RVT_S_SAMPLES") is string s && Directory.Exists(s))
            {
                samples = s;
            }
        }

        samples ??= Directory.Exists(Path.Combine(full, "Samples")) ? Path.Combine(full, "Samples") : null;

        return new RevitInstallation(
            year,
            full,
            exe,
            version,
            samples,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", year.ToString()),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", year.ToString()));
    }

    private static bool HasExe(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Revit.exe"));
}
