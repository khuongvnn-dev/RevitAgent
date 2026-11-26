using System.Diagnostics;
using System.Text.Json;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit;

/// <summary>One process the gateway started (persisted in <c>%REVITAGENT_HOME%\launched.json</c>).</summary>
public sealed record LaunchedProcess
{
    public int Pid { get; init; }

    /// <summary>Process start time (UTC) — ownership holds only while the live process with this pid has this start time.</summary>
    public DateTime StartTimeUtc { get; init; }

    public string ExePath { get; init; } = string.Empty;

    public int? Year { get; init; }

    public DateTime LaunchedAtUtc { get; init; }

    public bool TiedToGateway { get; init; }
}

/// <summary>
/// Which Revit processes the gateway launched. Ownership survives Host restarts (JSON file next to the gateway
/// data) and is immune to pid reuse: an entry counts only while a live process with that pid has the recorded
/// start time. Stale entries are pruned on every read. Only owned processes may be closed by revit_close
/// without the explicit foreign-process confirmation.
/// </summary>
public sealed class LaunchedProcessRegistry
{
    public const string FileName = "launched.json";

    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly string _file;

    public LaunchedProcessRegistry(AgentPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        _file = Path.Combine(paths.Root, FileName);
    }

    public string FilePath => _file;

    /// <summary>Records <paramref name="process"/> as launched by the gateway.</summary>
    public LaunchedProcess Register(Process process, int? year = null, bool tiedToGateway = false)
    {
        ArgumentNullException.ThrowIfNull(process);
        var start = Win32Windows.ProcessStartUtc(process)
            ?? throw new InvalidOperationException($"Cannot read the start time of pid {process.Id}.");
        string exe;
        try
        {
            exe = Win32Windows.ProcessImagePath(process.Id) ?? process.StartInfo.FileName;
        }
        catch (Exception)
        {
            exe = string.Empty;
        }

        var entry = new LaunchedProcess
        {
            Pid = process.Id,
            StartTimeUtc = start,
            ExePath = exe,
            Year = year,
            LaunchedAtUtc = DateTime.UtcNow,
            TiedToGateway = tiedToGateway,
        };

        lock (_gate)
        {
            var list = ReadLive().Where(e => e.Pid != entry.Pid).ToList();
            list.Add(entry);
            Write(list);
        }

        return entry;
    }

    /// <summary>True when <paramref name="pid"/> is a live process the gateway launched (same start time).</summary>
    public bool IsOwned(int pid) => Get(pid) is not null;

    public LaunchedProcess? Get(int pid)
    {
        lock (_gate)
        {
            return ReadLive().FirstOrDefault(e => e.Pid == pid);
        }
    }

    /// <summary>Live owned processes (stale entries are removed from the file).</summary>
    public IReadOnlyList<LaunchedProcess> List()
    {
        lock (_gate)
        {
            return ReadLive();
        }
    }

    public void Remove(int pid)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var keep = all.Where(e => e.Pid != pid).ToList();
            if (keep.Count != all.Count)
            {
                Write(keep);
            }
        }
    }

    private readonly HashSet<int> _gracefulClosingPids = new();

    /// <summary>All registered entries without pruning gone processes (for watchdog crash detection).</summary>
    public IReadOnlyList<LaunchedProcess> ReadAllEntries()
    {
        lock (_gate)
        {
            return ReadAll();
        }
    }

    /// <summary>Marks a pid as undergoing normal/graceful closure by revit_close.</summary>
    public void BeginGracefulClose(int pid)
    {
        lock (_gate)
        {
            _gracefulClosingPids.Add(pid);
        }
    }

    /// <summary>Cancels graceful close status if close was aborted (e.g. dialog blocked without discard_changes).</summary>
    public void CancelGracefulClose(int pid)
    {
        lock (_gate)
        {
            _gracefulClosingPids.Remove(pid);
        }
    }

    /// <summary>True if pid was marked for graceful closure.</summary>
    public bool IsGracefulClosing(int pid)
    {
        lock (_gate)
        {
            return _gracefulClosingPids.Contains(pid);
        }
    }

    /// <summary>True when a live process <paramref name="pid"/> has start time <paramref name="startUtc"/> (±1 s).</summary>
    public static bool IsSameLiveProcess(int pid, DateTime startUtc) => Check(pid, startUtc) == Liveness.Same;

    private enum Liveness
    {
        Same,
        Gone,
        Unknown,
    }

    /// <summary>Gone only on proof (no such pid, exited, other start time); a transient read error is Unknown.</summary>
    private static Liveness Check(int pid, DateTime startUtc)
    {
        Process p;
        try
        {
            p = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return Liveness.Gone;
        }
        catch (Exception)
        {
            return Liveness.Unknown;
        }

        using (p)
        {
            try
            {
                if (p.HasExited)
                {
                    return Liveness.Gone;
                }

                var actual = p.StartTime.ToUniversalTime();
                return (actual - startUtc).Duration() <= StartTimeTolerance ? Liveness.Same : Liveness.Gone;
            }
            catch (Exception)
            {
                return Liveness.Unknown;
            }
        }
    }

    /// <summary>Entries proven alive; entries proven gone are pruned, unknown ones are kept but not reported as owned.</summary>
    private List<LaunchedProcess> ReadLive()
    {
        var all = ReadAll();
        var states = all.Select(e => (Entry: e, State: Check(e.Pid, e.StartTimeUtc))).ToList();
        if (states.Any(s => s.State == Liveness.Gone))
        {
            Write(states.Where(s => s.State != Liveness.Gone).Select(s => s.Entry).ToList());
        }

        return states.Where(s => s.State == Liveness.Same).Select(s => s.Entry).ToList();
    }

    private List<LaunchedProcess> ReadAll()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(_file))
                {
                    return new List<LaunchedProcess>();
                }

                var json = File.ReadAllText(_file);
                return JsonSerializer.Deserialize<List<LaunchedProcess>>(json, GatewayJson.Options) ?? new List<LaunchedProcess>();
            }
            catch (JsonException)
            {
                // A corrupt file must not grant ownership of anything: start empty.
                return new List<LaunchedProcess>();
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                // Transient sharing violation while another writer replaces the file.
                Thread.Sleep(50);
            }
        }
    }

    private void Write(List<LaunchedProcess> entries)
    {
        var tmp = _file + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, new JsonSerializerOptions(GatewayJson.Options) { WriteIndented = true }));
        File.Move(tmp, _file, overwrite: true);
    }
}
