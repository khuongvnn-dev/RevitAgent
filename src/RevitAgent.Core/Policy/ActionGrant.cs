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
/// </summary>
public sealed record ActionGrant(GrantScope Scope, int? Pid, DateTimeOffset? ExpiresAt, DateTimeOffset GrantedAt)
{
    public bool IsActive(DateTimeOffset now) => ExpiresAt is null || ExpiresAt > now;

    public bool Covers(int pid) => Scope == GrantScope.All || (Scope == GrantScope.Pid && Pid == pid);
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
    private readonly List<ActionGrant> _grants = new();

    public ActionGrantStore()
        : this(TimeProvider.System)
    {
    }

    public ActionGrantStore(TimeProvider time) => _time = time;

    /// <summary>Raised after a grant is added or revoked (dashboard refresh).</summary>
    public event Action? Changed;

    public ActionGrant Grant(GrantScope scope, int? pid, TimeSpan? duration)
    {
        if (scope == GrantScope.Pid && pid is not > 0)
        {
            throw new ArgumentException("A pid grant needs a positive pid.", nameof(pid));
        }

        var now = _time.GetUtcNow();
        var grant = new ActionGrant(scope, scope == GrantScope.Pid ? pid : null, duration is { } d ? now + d : null, now);
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

    /// <summary>Active (unexpired) grants; expired ones are dropped.</summary>
    public IReadOnlyList<ActionGrant> Active()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _grants.RemoveAll(g => !g.IsActive(now));
            return _grants.ToArray();
        }
    }

    public ActionGrant? FindActive(int pid)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return _grants.Where(g => g.IsActive(now) && g.Covers(pid)).OrderBy(g => g.Scope).FirstOrDefault();
        }
    }
}
