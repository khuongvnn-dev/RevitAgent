using System.Globalization;
using Interop.UIAutomationClient;

namespace RevitAgent.Core.Ui;

/// <summary>One element reference handed to the agent (e12).</summary>
public sealed record RefEntry(string Ref, IUIAutomationElement Element, int[]? RuntimeId, string Type, string Name, string AutomationId);

/// <summary>Refs of one window within one session. Replaced by the next ui_snapshot of that window; ui_find appends.</summary>
public sealed class RefSet
{
    private readonly Dictionary<string, RefEntry> _refs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _next = 1;

    public RefSet(long hwnd, int pid, string title)
    {
        Hwnd = hwnd;
        Pid = pid;
        Title = title;
    }

    public long Hwnd { get; }

    public int Pid { get; }

    public string Title { get; }

    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _refs.Count;
            }
        }
    }

    public string Add(UiNode node)
    {
        lock (_gate)
        {
            var name = "e" + _next.ToString(CultureInfo.InvariantCulture);
            _next++;
            _refs[name] = new RefEntry(name, node.Element, node.RuntimeId, node.Type, node.Name, node.AutomationId);
            return name;
        }
    }

    public RefEntry? Get(string reference)
    {
        lock (_gate)
        {
            return _refs.TryGetValue(reference, out var e) ? e : null;
        }
    }

    /// <summary>Existing ref of an element with this RuntimeId (so ui_find reuses refs instead of minting duplicates).</summary>
    public string? FindByRuntimeId(int[]? runtimeId)
    {
        if (runtimeId is null || runtimeId.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            foreach (var e in _refs.Values)
            {
                if (e.RuntimeId is { } r && r.AsSpan().SequenceEqual(runtimeId))
                {
                    return e.Ref;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Refs per (MCP session, window). A ref given without an explicit window resolves against the most recently used
/// ref set of the session (restricted to the pid when one is given). Thread-safe.
/// </summary>
public sealed class RefStore
{
    private const int MaxSetsPerSession = 16;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<RefSet>> _sessions = new(StringComparer.Ordinal);

    /// <summary>Starts a fresh ref set for a window (a new snapshot invalidates the window's previous refs).</summary>
    public RefSet Replace(string session, TopLevelWindow window)
    {
        lock (_gate)
        {
            var sets = SetsOf(session);
            sets.RemoveAll(s => s.Hwnd == window.Hwnd);
            var set = new RefSet(window.Hwnd, window.ProcessId, window.Title);
            sets.Insert(0, set);
            Trim(sets);
            return set;
        }
    }

    /// <summary>The window's current ref set, created when missing (ui_find appends to it).</summary>
    public RefSet GetOrCreate(string session, TopLevelWindow window)
    {
        lock (_gate)
        {
            var sets = SetsOf(session);
            var set = sets.FirstOrDefault(s => s.Hwnd == window.Hwnd);
            if (set is null)
            {
                set = new RefSet(window.Hwnd, window.ProcessId, window.Title);
                sets.Insert(0, set);
                Trim(sets);
            }
            else
            {
                Touch(sets, set);
            }

            return set;
        }
    }

    /// <summary>
    /// The ref set a bare ref refers to: the given window's set, else the most recent set of <paramref name="pid"/>,
    /// else the session's most recent set.
    /// </summary>
    public RefSet? Find(string session, long? hwnd, int? pid)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var sets) || sets.Count == 0)
            {
                return null;
            }

            RefSet? set = hwnd is { } h
                ? sets.FirstOrDefault(s => s.Hwnd == h)
                : pid is { } p ? sets.FirstOrDefault(s => s.Pid == p) : sets[0];
            if (set is not null)
            {
                Touch(sets, set);
            }

            return set;
        }
    }

    /// <summary>The session's most recently used window (default window for calls without targeting arguments).</summary>
    public long? CurrentWindow(string session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var sets) && sets.Count > 0 ? sets[0].Hwnd : null;
        }
    }

    private List<RefSet> SetsOf(string session)
    {
        if (!_sessions.TryGetValue(session, out var sets))
        {
            if (_sessions.Count > 64)
            {
                // Drop the stalest session (clients that disconnected long ago).
                var stale = _sessions.OrderBy(kv => kv.Value.Count == 0 ? DateTime.MinValue : kv.Value.Max(s => s.LastUsed)).First().Key;
                _sessions.Remove(stale);
            }

            sets = new List<RefSet>();
            _sessions[session] = sets;
        }

        return sets;
    }

    private static void Touch(List<RefSet> sets, RefSet set)
    {
        set.LastUsed = DateTime.UtcNow;
        sets.Remove(set);
        sets.Insert(0, set);
    }

    private static void Trim(List<RefSet> sets)
    {
        sets.RemoveAll(s => !Native.NativeMethods.IsWindow(new IntPtr(s.Hwnd)));
        while (sets.Count > MaxSetsPerSession)
        {
            sets.RemoveAt(sets.Count - 1);
        }
    }
}
