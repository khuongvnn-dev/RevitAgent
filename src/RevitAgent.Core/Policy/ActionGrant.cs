namespace RevitAgent.Core.Policy;

/// <summary>What an <see cref="ActionGrant"/> covers.</summary>
public enum GrantScope
{
    /// <summary>One Revit process (<see cref="ActionGrant.Pid"/>).</summary>
    Pid,

    /// <summary>Every Revit the gateway did not launch.</summary>
    All,
}

/// <summary>
/// The owner's time-limited permission for agents to ACT on a Revit the gateway did not launch
/// ("Let AI act on my Revit"). <see cref="ExpiresAt"/> null = until the owner turns it off.
/// A pid grant also records <see cref="ProcessStart"/>: it covers that process only, not a later one that reuses the pid.
/// </summary>
public sealed record ActionGrant(GrantScope Scope, int? Pid, DateTimeOffset? ExpiresAt, DateTimeOffset GrantedAt, DateTime? ProcessStart = null)
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;

    public bool Covers(int pid) => Scope == GrantScope.All || (Scope == GrantScope.Pid && Pid == pid);

    /// <summary>
    /// A pid grant holds only while the process with that pid has the start time seen when it was granted (pid reuse).
    /// An unknown start time fails closed.
    /// </summary>
    public bool IsSameProcess(DateTime? currentStart) =>
        Scope == GrantScope.All ||
        (ProcessStart is { } granted && currentStart is { } now && (now - granted).Duration() <= StartTolerance);
}

/// <summary>Source of action grants. The default (<see cref="ActionGrantStore"/>) starts empty = no grant.</summary>
public interface IActionGrantProvider
{
    /// <summary>The active grant covering <paramref name="pid"/>, or null.</summary>
    ActionGrant? FindActive(int pid);
}

/// <summary>
/// In-memory grants, set only from the dashboard. Nothing is persisted: restarting the Host revokes everything.
/// </summary>
public sealed class ActionGrantStore : IActionGrantProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<int, DateTime?> _processStart;
    private readonly List<ActionGrant> _grants = new();

    public ActionGrantStore()
        : this(TimeProvider.System)
    {
    }

    public ActionGrantStore(TimeProvider time)
        : this(time, SystemProcessStart)
    {
    }

    /// <param name="time">Clock for expiry.</param>
    /// <param name="processStart">Start time of the live process with a pid (null when it is not running or unreadable).</param>
    public ActionGrantStore(TimeProvider time, Func<int, DateTime?> processStart)
    {
        _time = time;
        _processStart = processStart;
    }

    /// <summary>Raised after a grant is added or revoked (dashboard refresh).</summary>
    public event Action? Changed;

    public ActionGrant Grant(GrantScope scope, int? pid, TimeSpan? duration)
    {
        if (scope == GrantScope.Pid && pid is not > 0)
        {
            throw new ArgumentException("A pid grant needs a positive pid.", nameof(pid));
        }

        var now = _time.GetUtcNow();
        var start = scope == GrantScope.Pid ? StartOf(pid!.Value) : null;
        var grant = new ActionGrant(scope, scope == GrantScope.Pid ? pid : null, duration is { } d ? now + d : null, now, start);
        lock (_gate)
        {
            _grants.RemoveAll(g => g.Scope == scope && g.Pid == grant.Pid);
            _grants.Add(grant);
        }

        Changed?.Invoke();
        return grant;
    }

    public void Revoke(GrantScope scope, int? pid = null)
    {
        int removed;
        lock (_gate)
        {
            removed = _grants.RemoveAll(g => g.Scope == scope && g.Pid == (scope == GrantScope.Pid ? pid : null));
        }

        if (removed > 0)
        {
            Changed?.Invoke();
        }
    }

    public void RevokeAll()
    {
        int removed;
        lock (_gate)
        {
            removed = _grants.RemoveAll(_ => true);
        }

        if (removed > 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Active grants; expired ones, and pid grants whose process is gone or was replaced (pid reuse), are dropped.</summary>
    public IReadOnlyList<ActionGrant> Active()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _grants.RemoveAll(g => !g.IsActive(now) || !IsSameProcess(g));
            return _grants.ToArray();
        }
    }

    public ActionGrant? FindActive(int pid)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return _grants.Where(g => g.IsActive(now) && g.Covers(pid) && IsSameProcess(g)).OrderBy(g => g.Scope).FirstOrDefault();
        }
    }

    private bool IsSameProcess(ActionGrant g) => g.Scope == GrantScope.All || g.IsSameProcess(g.Pid is { } p ? StartOf(p) : null);

    private DateTime? StartOf(int pid)
    {
        try
        {
            return _processStart(pid);
        }
        catch (Exception)
        {
            return null; // unknown = not the granted process (fail closed)
        }
    }

    private static DateTime? SystemProcessStart(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
