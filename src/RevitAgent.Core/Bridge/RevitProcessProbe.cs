using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RevitAgent.Core.Revit;

namespace RevitAgent.Core.Bridge;

/// <summary>A running process as seen by the Bridge tools (read-only facts, no interaction).</summary>
public sealed record RevitProcessInfo(int Pid, string ProcessName, string? ExePath, int? Year, DateTime? StartTime)
{
    public bool IsRevit => string.Equals(ProcessName, "Revit", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Looks up Revit processes. Injectable so tests never depend on (or touch) real Revit processes.</summary>
public interface IRevitProcessProbe
{
    /// <summary>The process with <paramref name="pid"/>, or null when it is not running.</summary>
    RevitProcessInfo? Get(int pid);

    /// <summary>All running Revit.exe processes of the current session's machine.</summary>
    IReadOnlyList<RevitProcessInfo> ListRevit();
}

/// <summary>
/// Reads process facts with PROCESS_QUERY_LIMITED_INFORMATION only (image path, start time): it never
/// opens a Revit process for reading memory, injecting, or sending messages.
/// </summary>
public sealed class SystemRevitProcessProbe : IRevitProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public RevitProcessInfo? Get(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return Describe(p);
        }
        catch (ArgumentException)
        {
            return null; // not running
        }
        catch (InvalidOperationException)
        {
            return null; // exited meanwhile
        }
    }

    public IReadOnlyList<RevitProcessInfo> ListRevit()
    {
        var result = new List<RevitProcessInfo>();
        foreach (var p in Process.GetProcessesByName("Revit"))
        {
            using (p)
            {
                try
                {
                    result.Add(Describe(p));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return result.OrderBy(r => r.Pid).ToArray();
    }

    private static RevitProcessInfo Describe(Process p)
    {
        var exe = ImagePath(p.Id);
        DateTime? start = null;
        try
        {
            start = p.StartTime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }

        return new RevitProcessInfo(p.Id, p.ProcessName, exe, exe is null ? null : RevitInstallations.YearOfExecutable(exe), start);
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
