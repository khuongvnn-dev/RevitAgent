using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Policy;

/// <summary>A question to the machine owner: "may this agent run this machine-wide tool?".</summary>
/// <param name="Tool">Tool asking (for example bridge_install).</param>
/// <param name="SessionId">MCP session / agent that called it.</param>
/// <param name="Years">Revit years the call affects.</param>
/// <param name="Timeout">How long the owner has; no answer in time = deny.</param>
public sealed record OwnerApprovalRequest(string Tool, string SessionId, IReadOnlyList<int> Years, TimeSpan Timeout)
{
    /// <summary>The default time the owner has to answer.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public string YearsText => Years.Count == 0 ? "no installed version" : string.Join(", ", Years);

    /// <summary>What exactly is asked (for example the pid, open documents and consequences of revit_close); appended to <see cref="Describe"/>.</summary>
    public string? Detail { get; init; }

    /// <summary>English one-liner shown to the owner on every channel.</summary>
    public string Describe() => Tool switch
    {
        "bridge_uninstall" => $"'{SessionId}' asks to UNINSTALL the RevitAgent Bridge for Revit {YearsText}",
        "bridge_install" => $"'{SessionId}' asks to install the RevitAgent Bridge for Revit {YearsText}",
        _ => $"'{SessionId}' asks to run {Tool} for Revit {YearsText}",
    } + (string.IsNullOrWhiteSpace(Detail) ? string.Empty : ": " + Detail);
}

/// <summary>The owner's decision (or the reason there was none). Anything but <see cref="Approved"/> is a deny.</summary>
public sealed record OwnerDecision(bool Approved, string Channel, string By, string Reason, bool Explicit = true)
{
    /// <summary>True when the request was cancelled by the client before anyone decided (audited as "cancelled").</summary>
    public bool Cancelled { get; init; }

    public static OwnerDecision Deny(string channel, string by, string reason, bool isExplicit = true) => new(false, channel, by, reason, isExplicit);

    public static OwnerDecision Approve(string channel, string by) => new(true, channel, by, "approved");
}

/// <summary>One way to reach the owner (dashboard card, Telegram, ...).</summary>
public interface IOwnerApprovalChannel
{
    string Name { get; }

    /// <summary>False when this channel cannot reach a human right now (no dashboard open, Telegram not configured).</summary>
    bool IsAvailable { get; }

    /// <summary>Asks and waits. Returns a non-explicit deny on timeout/failure; cancelled when another channel answered.</summary>
    Task<OwnerDecision> AskAsync(OwnerApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Owner approval for tools that change shared machine state. There is deliberately no way for an agent to approve.</summary>
public interface IOwnerApproval
{
    Task<OwnerDecision> RequestAsync(OwnerApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Shared by the Bridge install tools.</summary>
public static class OwnerApprovalExtensions
{
    public const string RequiredMessage =
        "Owner approval required. Ask the owner to approve on the RevitAgent dashboard, use its Install Bridge button, or configure Telegram.";

    /// <summary>Throws PERMISSION_DENIED unless the owner approved.</summary>
    public static async Task EnsureApprovedAsync(this IOwnerApproval approval, OwnerApprovalRequest request, CancellationToken cancellationToken)
    {
        var decision = await approval.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (!decision.Approved)
        {
            throw GatewayException.Denied(
                $"{RequiredMessage} ({request.Tool}: {decision.Reason}.)",
                new { needs = "owner_approval", channel = decision.Channel, reason = decision.Reason });
        }
    }
}

/// <summary>
/// Asks every available channel at once; the first approval or explicit denial wins. No available channel, or
/// no answer within the timeout = deny. Every decision is audited (tool <c>owner_approval</c>).
/// </summary>
public sealed class OwnerApprovalService : IOwnerApproval
{
    public const string AuditTool = "owner_approval";

    private readonly IReadOnlyList<IOwnerApprovalChannel> _channels;
    private readonly AuditLog _audit;

    public OwnerApprovalService(IEnumerable<IOwnerApprovalChannel> channels, AuditLog audit)
    {
        _channels = channels.ToArray();
        _audit = audit;
    }

    public async Task<OwnerDecision> RequestAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
    {
        OwnerDecision decision;
        try
        {
            decision = await DecideAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client gave up before the owner answered: still leave an audit entry.
            Record(request, OwnerDecision.Deny("none", "system", "cancelled by the client", isExplicit: false) with { Cancelled = true });
            throw;
        }

        Record(request, decision);
        return decision;
    }

    private async Task<OwnerDecision> DecideAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
    {
        var available = new List<IOwnerApprovalChannel>();
        foreach (var channel in _channels)
        {
            try
            {
                if (channel.IsAvailable)
                {
                    available.Add(channel);
                }
            }
            catch (Exception ex)
            {
                _audit.Diagnostic($"Owner approval channel {channel.Name} availability check failed", ex);
            }
        }

        if (available.Count == 0)
        {
            return OwnerDecision.Deny("none", "system", "no approval channel is available (no dashboard open and Telegram not configured)");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout + TimeSpan.FromSeconds(5)); // safety net; channels enforce the timeout themselves
        var pending = available.ToDictionary(c => AskSafeAsync(c, request, cts.Token), c => c);
        OwnerDecision? last = null;
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Keys).ConfigureAwait(false);
                pending.Remove(done);
                var decision = await done.ConfigureAwait(false);
                if (decision.Approved || decision.Explicit)
                {
                    return decision;
                }

                last = decision;
            }
        }
        finally
        {
            cts.Cancel();
            foreach (var task in pending.Keys)
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return last ?? OwnerDecision.Deny("none", "system", "no answer", isExplicit: false);
    }

    private async Task<OwnerDecision> AskSafeAsync(IOwnerApprovalChannel channel, OwnerApprovalRequest request, CancellationToken ct)
    {
        try
        {
            return await channel.AskAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OwnerDecision.Deny(channel.Name, "system", "no answer in time", isExplicit: false);
        }
        catch (Exception ex)
        {
            _audit.Diagnostic($"Owner approval channel {channel.Name} failed", ex);
            return OwnerDecision.Deny(channel.Name, "system", $"channel {channel.Name} failed", isExplicit: false);
        }
    }

    private void Record(OwnerApprovalRequest request, OwnerDecision decision) =>
        _audit.Record(new AuditEntry(
            DateTimeOffset.Now,
            Guid.NewGuid().ToString("N")[..12],
            request.SessionId,
            AuditTool,
            ToolPermission.Admin.ToString(),
            AuditLog.SummarizeArgs(JsonSerializer.SerializeToElement(new
            {
                kind = "owner_approval",
                tool = request.Tool,
                years = request.Years,
                channel = decision.Channel,
                decision = decision.Approved ? "approved" : decision.Cancelled ? "cancelled" : "denied",
                by = decision.By,
                reason = decision.Reason,
            })),
            decision.Approved,
            decision.Approved ? null : Contracts.Protocol.GatewayErrorCodes.PermissionDenied,
            0,
            Note: $"{request.Tool} {(decision.Approved ? "approved" : decision.Cancelled ? "cancelled" : "denied")} via {decision.Channel} by {decision.By}"));
}

/// <summary>
/// The dashboard channel. Core only holds the pending requests; the Host's dashboard calls <see cref="Attach"/>
/// when it is running and shows <see cref="Pending"/> as a card with Approve / Deny. Without a dashboard (headless
/// Shim) it is unavailable.
/// </summary>
public sealed class DashboardApprovalChannel : IOwnerApprovalChannel
{
    private readonly object _gate = new();
    private readonly List<PendingApproval> _pending = new();
    private volatile bool _attached;

    public string Name => "dashboard";

    public bool IsAvailable => _attached;

    /// <summary>Raised when a request is added or resolved (handlers marshal to the UI thread).</summary>
    public event Action? Changed;

    /// <summary>The dashboard is running and will show requests.</summary>
    public void Attach() => _attached = true;

    public IReadOnlyList<PendingApproval> Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending.ToArray();
            }
        }
    }

    public async Task<OwnerDecision> AskAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
    {
        var item = new PendingApproval(request);
        lock (_gate)
        {
            _pending.Add(item);
        }

        Changed?.Invoke();
        try
        {
            using var timeout = new CancellationTokenSource(request.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var registration = linked.Token.Register(() => item.Complete(OwnerDecision.Deny(Name, "system", "no answer in time", isExplicit: false)));
            return await item.Decision.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(item);
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Called by the dashboard buttons.</summary>
    public void Resolve(PendingApproval item, bool approved) =>
        item.Complete(approved ? OwnerDecision.Approve(Name, "owner (dashboard)") : OwnerDecision.Deny(Name, "owner (dashboard)", "denied by the owner"));
}

/// <summary>A request waiting for the owner on the dashboard.</summary>
public sealed class PendingApproval
{
    private readonly TaskCompletionSource<OwnerDecision> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingApproval(OwnerApprovalRequest request) => Request = request;

    public OwnerApprovalRequest Request { get; }

    public Task<OwnerDecision> Decision => _tcs.Task;

    public void Complete(OwnerDecision decision) => _tcs.TrySetResult(decision);
}

/// <summary>Registers the owner-approval services; the Host's dashboard attaches to <see cref="DashboardApprovalChannel"/>.</summary>
public sealed class OwnerApprovalModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<DashboardApprovalChannel>();
        services.AddSingleton<IOwnerApprovalChannel>(sp => sp.GetRequiredService<DashboardApprovalChannel>());
        services.TryAddSingleton<IOwnerApproval, OwnerApprovalService>();
    }
}
