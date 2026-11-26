using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Framing;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Bridge;

/// <summary>Tunables of <see cref="BridgeClient"/> (tests point the pipe name at a fake server).</summary>
public sealed class BridgeClientOptions
{
    /// <summary>Pipe name for a Revit pid. Default <see cref="BridgePipeNames.ForProcess"/>.</summary>
    public Func<int, string> PipeNameForPid { get; init; } = BridgePipeNames.ForProcess;

    /// <summary>How long to wait for the Bridge pipe to accept a connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Margin by which the Bridge's own timeout is shorter than the Host budget, so the Bridge answers TIMEOUT first.</summary>
    public TimeSpan BridgeTimeoutMargin { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>How long a call waits for the in-flight call to the same Revit before it gets BUSY.</summary>
    public TimeSpan QueueWait { get; init; } = BridgeClient.DefaultQueueWait;
}

/// <summary>
/// Host-side client of the in-Revit Bridge: one short-lived pipe connection per call to
/// <c>\\.\pipe\revitagent-bridge-&lt;pid&gt;</c> (current-user only), 4-byte length + UTF-8 JSON frames.
/// Maps transport failures to agent-actionable gateway errors and raises
/// <see cref="GatewayEvents.BridgeStateChanged"/> when a pid's Bridge becomes reachable/unreachable.
/// </summary>
public sealed class BridgeClient
{
    private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
    {
        GatewayErrorCodes.ValidationFailed, GatewayErrorCodes.NotFound, GatewayErrorCodes.Conflict, GatewayErrorCodes.PermissionDenied,
        GatewayErrorCodes.TargetNotReady, GatewayErrorCodes.Busy, GatewayErrorCodes.Timeout, GatewayErrorCodes.DependencyFailed,
        GatewayErrorCodes.SandboxViolation, GatewayErrorCodes.ModalBlocked, GatewayErrorCodes.Internal,
    };

    private readonly GatewayEvents _events;
    private readonly IRevitProcessProbe _processes;
    private readonly BridgeClientOptions _options;
    private readonly ConcurrentDictionary<int, bool> _state = new();
    private readonly ConcurrentDictionary<int, PidSlot> _slots = new();

    /// <summary>Default for <see cref="BridgeClientOptions.QueueWait"/>: Revit runs one API request at a time, so calls queue per pid.</summary>
    public static readonly TimeSpan DefaultQueueWait = TimeSpan.FromSeconds(15);

    private sealed class PidSlot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile string? Running;
    }

    // Answered by the Bridge without the Revit API thread, so they never need to wait.
    private static bool BypassesQueue(string tool) =>
        tool is BridgeToolNames.Ping or BridgeToolNames.DialogListEvents or BridgeToolNames.CommandListPostable;

    public BridgeClient(GatewayEvents events, IRevitProcessProbe processes, BridgeClientOptions options)
    {
        _events = events;
        _processes = processes;
        _options = options;
    }

    public string PipeName(int pid) => _options.PipeNameForPid(pid);

    /// <summary>Last known reachability per pid (true = last call succeeded).</summary>
    public IReadOnlyDictionary<int, bool> KnownStates => _state;

    /// <summary>
    /// True when the Bridge pipe of <paramref name="pid"/> exists. Uses WaitNamedPipe with a 1 ms wait:
    /// it neither connects nor consumes a pipe instance, and unlike enumerating <c>\\.\pipe\</c> it cannot
    /// miss an entry while other pipes are being created. ERROR_FILE_NOT_FOUND = no instance exists;
    /// a timeout means it exists but all instances are busy.
    /// </summary>
    public bool IsPipePresent(int pid)
    {
        var path = @"\\.\pipe\" + PipeName(pid);
        if (WaitNamedPipe(path, 1))
        {
            return true;
        }

        return Marshal.GetLastWin32Error() != ErrorFileNotFound;
    }

    private const int ErrorFileNotFound = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WaitNamedPipe(string name, int timeoutMs);

    /// <summary>Pipe check with one short re-check (a server may be between two pipe instances).</summary>
    private async Task<bool> IsPipePresentSettledAsync(int pid, CancellationToken cancellationToken)
    {
        if (IsPipePresent(pid))
        {
            return true;
        }

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return IsPipePresent(pid);
    }

    /// <summary>Calls <paramref name="tool"/> in the Bridge of Revit <paramref name="pid"/> and returns its JSON result.</summary>
    /// <param name="budget">Total time the caller grants; the Bridge gets a slightly shorter execution timeout.</param>
    public async Task<JsonElement> CallAsync(int pid, string tool, JsonElement? args, TimeSpan budget, CancellationToken cancellationToken)
    {
        EnsureRevitProcess(pid);
        if (BypassesQueue(tool))
        {
            return await CallCoreAsync(pid, tool, args, budget, cancellationToken).ConfigureAwait(false);
        }

        // Serialize per Revit pid: the wait counts against the budget; cancellation releases (or never takes) the slot.
        var slot = _slots.GetOrAdd(pid, _ => new PidSlot());
        var wait = _options.QueueWait < budget ? _options.QueueWait : budget;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool entered;
        try
        {
            entered = await slot.Gate.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new GatewayException(GatewayErrorCodes.Timeout, $"Call to '{tool}' was cancelled while waiting for the previous call to Revit pid {pid}.", new { pid, tool });
        }

        if (!entered)
        {
            var busyTool = slot.Running;
            throw new GatewayException(
                GatewayErrorCodes.Busy,
                $"Revit pid {pid} is still running '{busyTool ?? "another call"}' after {sw.Elapsed.TotalSeconds:0}s; calls to one Revit run one at a time. Retry when it finishes.",
                new { pid, busy_tool = busyTool, waited_ms = (long)sw.ElapsedMilliseconds });
        }

        slot.Running = tool;
        try
        {
            return await CallCoreAsync(pid, tool, args, budget - sw.Elapsed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            slot.Running = null;
            slot.Gate.Release();
        }
    }

    private async Task<JsonElement> CallCoreAsync(int pid, string tool, JsonElement? args, TimeSpan budget, CancellationToken cancellationToken)
    {
        var pipeName = PipeName(pid);
        if (!await IsPipePresentSettledAsync(pid, cancellationToken).ConfigureAwait(false))
        {
            SetState(pid, false);
            throw NotLoaded(pid, pipeName);
        }

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, connectCts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SetState(pid, false);
            if (!IsPipePresent(pid))
            {
                throw NotLoaded(pid, pipeName);
            }

            throw GatewayException.Dependency(
                $"The RevitAgent Bridge pipe of Revit pid {pid} exists but did not accept a connection within {_options.ConnectTimeout.TotalSeconds:0}s. Revit may be hung or still starting; check revit_get_state and retry.",
                new { pid, pipe = pipeName });
        }
        catch (UnauthorizedAccessException)
        {
            SetState(pid, false);
            throw GatewayException.Denied(
                $"Access to the Bridge pipe of Revit pid {pid} was denied. Revit and the gateway must run as the same Windows user at the same elevation level.",
                new { pid, pipe = pipeName });
        }

        var bridgeTimeout = budget - _options.BridgeTimeoutMargin;
        if (bridgeTimeout < TimeSpan.FromSeconds(1))
        {
            bridgeTimeout = TimeSpan.FromSeconds(1);
        }

        var request = new BridgeRequest
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Tool = tool,
            Args = args is { ValueKind: JsonValueKind.Object } a ? a : null,
            TimeoutSec = (int)Math.Ceiling(bridgeTimeout.TotalSeconds),
        };

        string responseJson;
        try
        {
            await LengthPrefixedPipeFramer.WriteMessageAsync(pipe, JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
            responseJson = await LengthPrefixedPipeFramer.ReadMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new GatewayException(
                GatewayErrorCodes.Timeout,
                $"The Bridge in Revit pid {pid} did not answer '{tool}' within {budget.TotalSeconds:0}s. The call may still be running in Revit; check bridge_status and dialog_list_events before retrying.",
                new { pid, tool });
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
        {
            SetState(pid, false);
            throw GatewayException.Dependency(
                $"The connection to the Bridge in Revit pid {pid} broke during '{tool}' (Revit closed, crashed or restarted?). Check revit_list_instances / bridge_status.",
                new { pid, tool });
        }

        BridgeResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<BridgeResponse>(responseJson);
        }
        catch (JsonException)
        {
            response = null;
        }

        if (response is null)
        {
            throw new GatewayException(GatewayErrorCodes.Internal, $"The Bridge in Revit pid {pid} sent an unreadable response to '{tool}'. Reinstall the Bridge (bridge_install) and restart Revit.");
        }

        SetState(pid, true);
        if (response.Ok)
        {
            return response.Result ?? JsonSerializer.SerializeToElement(new { });
        }

        var error = response.Error ?? new BridgeError { Message = "The Bridge reported an error without details." };
        var code = KnownCodes.Contains(error.Code) ? error.Code : GatewayErrorCodes.Internal;
        throw new GatewayException(code, string.IsNullOrWhiteSpace(error.Message) ? $"Bridge tool '{tool}' failed ({error.Code})." : error.Message, error.Details);
    }

    /// <summary>Cheap liveness/version probe (no Revit API involved; works while Revit is busy).</summary>
    public Task<JsonElement> PingAsync(int pid, CancellationToken cancellationToken) =>
        CallAsync(pid, BridgeToolNames.Ping, null, TimeSpan.FromSeconds(8), cancellationToken);

    private void EnsureRevitProcess(int pid)
    {
        if (pid <= 0)
        {
            throw GatewayException.Validation("Argument 'pid' must be a positive Revit process id (see revit_list_instances or bridge_status).");
        }

        var process = _processes.Get(pid) ?? throw GatewayException.NotFound(
            $"No running process with pid {pid}. List Revit instances with revit_list_instances or bridge_status.");
        if (!process.IsRevit)
        {
            throw GatewayException.Validation($"Process {pid} is '{process.ProcessName}', not Revit. Pass the pid of a Revit.exe process.");
        }
    }

    private static GatewayException NotLoaded(int pid, string pipeName) =>
        GatewayException.Dependency(
            $"RevitAgent Bridge is not loaded in Revit pid {pid}. Install it with bridge_install and restart that Revit. " +
            "(If Revit was started moments ago it may still be loading, or a startup dialog such as 'Security - Unsigned Add-In' for another add-in may be waiting: check revit_list_dialogs.)",
            new { pid, pipe = pipeName });

    private void SetState(int pid, bool connected)
    {
        var changed = true;
        _state.AddOrUpdate(pid, connected, (_, old) =>
        {
            changed = old != connected;
            return connected;
        });
        if (changed)
        {
            _events.RaiseBridgeStateChanged(pid, connected);
        }
    }
}
