using System.Diagnostics;
using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

internal static class RevitToolHelpers
{
    public const int MaxImages = 3;

    /// <summary>
    /// The <c>pid</c> argument; when it is optional and missing, the only running Revit
    /// (required as soon as more than one Revit runs).
    /// </summary>
    public static int ResolvePid(ToolCall call, bool required)
    {
        if (call.GetInt("pid") is { } pid)
        {
            if (pid <= 0)
            {
                throw GatewayException.Validation("Argument 'pid' must be a positive process id (from revit_list_instances).");
            }

            return pid;
        }

        if (required)
        {
            throw GatewayException.Validation("Missing required argument 'pid' (integer, from revit_list_instances).");
        }

        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName(RevitProcessService.ProcessName))
        {
            pids.Add(p.Id);
            p.Dispose();
        }

        return pids.Count switch
        {
            1 => pids[0],
            0 => throw GatewayException.NotFound("No Revit is running. Start one with revit_launch (only if the user asked) or ask the user to start Revit."),
            _ => throw GatewayException.Validation($"{pids.Count} Revit processes are running ({string.Join(", ", pids.Order())}); pass 'pid' (see revit_list_instances)."),
        };
    }

    /// <summary>
    /// revit_* dialog/state tools only target Revit.exe processes or processes the gateway launched, so a wrong pid can
    /// never make the gateway read or click another application's windows. Exited pids pass (the tool reports it).
    /// </summary>
    public static void EnsureRevitTarget(int pid, LaunchedProcessRegistry launched)
    {
        if (!Win32Windows.IsProcessAlive(pid) || launched.IsOwned(pid))
        {
            return;
        }

        string? name = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            // Treated as not Revit below.
        }

        if (!string.Equals(name, RevitProcessService.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            throw GatewayException.Denied(
                $"pid {pid} is not a Revit.exe process ({name ?? "unknown"}). revit_* tools only observe and answer dialogs of Revit (or of processes the gateway launched). Use revit_list_instances for Revit pids.");
        }
    }

    /// <summary>Evidence folder of <c>run_id</c> when given, else null (captures folder).</summary>
    public static string? EvidenceDir(ToolCall call, SandboxService sandbox) =>
        call.GetString("run_id") is { Length: > 0 } runId ? sandbox.EvidenceDir(runId) : null;

    /// <summary>
    /// Folder for dialog screenshots: the explicit <c>run_id</c>'s evidence folder, else the current run's (session, then pid),
    /// else <c>captures\dialogs</c>.
    /// </summary>
    public static string DialogShotDir(ToolCall call, int pid, SandboxService sandbox, AgentPaths paths) =>
        EvidenceDir(call, sandbox) ?? sandbox.Evidence.EvidenceDirFor(call.SessionId, pid) ?? Path.Combine(paths.CapturesDir, "dialogs");

    /// <summary>Attaches up to <see cref="MaxImages"/> saved screenshots as images.</summary>
    public static ToolResult AttachScreenshots(ToolResult result, IEnumerable<string?> paths)
    {
        foreach (var path in paths.Where(p => p is not null && File.Exists(p)).Take(MaxImages))
        {
            try
            {
                result.AddImage(File.ReadAllBytes(path!));
            }
            catch (IOException)
            {
                // Screenshot vanished; the path in the JSON still explains.
            }
        }

        return result;
    }

    public static TimeSpan Seconds(ToolCall call, string name, int defaultValue, int min, int max)
    {
        var v = call.GetInt(name, defaultValue) ?? defaultValue;
        if (v < min || v > max)
        {
            throw GatewayException.Validation($"Argument '{name}' must be between {min} and {max} seconds.");
        }

        return TimeSpan.FromSeconds(v);
    }
}
