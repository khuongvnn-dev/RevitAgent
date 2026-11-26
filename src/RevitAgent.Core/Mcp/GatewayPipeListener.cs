using System.IO.Pipes;
using RevitAgent.Core.Audit;

namespace RevitAgent.Core.Mcp;

/// <summary>
/// Accepts MCP clients on a named pipe (default <c>revitagent-mcp</c>) and runs one
/// <see cref="McpGatewayServer"/> session per connection. The pipe carries the standard MCP stdio
/// framing (newline-delimited JSON-RPC), so <c>RevitAgent.Shim</c> is a plain byte relay.
/// The pipe is restricted to the current Windows user (<see cref="PipeOptions.CurrentUserOnly"/>).
/// </summary>
public sealed class GatewayPipeListener : IAsyncDisposable
{
    public const string DefaultPipeName = "revitagent-mcp";

    private readonly McpGatewayServer _server;
    private readonly AuditLog _audit;
    private readonly List<Task> _sessions = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _sessionCounter;

    public GatewayPipeListener(McpGatewayServer server, AuditLog audit)
    {
        _server = server;
        _audit = audit;
    }

    public string? PipeName { get; private set; }

    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    public int ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count(t => !t.IsCompleted);
            }
        }
    }

    public void Start(string? pipeName = null)
    {
        if (IsRunning)
        {
            return;
        }

        PipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var name = PipeName;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(name, token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        Task[] pending;
        lock (_gate)
        {
            pending = _sessions.ToArray();
        }

        try
        {
            await Task.WhenAll(pending.Append(_acceptLoop ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Sessions end on cancellation; a slow one must not block shutdown.
        }

        _cts.Dispose();
        _cts = null;
        _acceptLoop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _audit.Diagnostic($"Pipe listener '{pipeName}' accept failed; retrying in 1s", ex);
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var id = $"pipe-{Interlocked.Increment(ref _sessionCounter)}";
            var connected = pipe;
            var session = Task.Run(async () =>
            {
                try
                {
                    // The relay Shim's profile line is consumed here, before the MCP server handles anything (no race with tools/list).
                    var (input, announced) = await ShimPreamble.ReadAsync(connected, ct).ConfigureAwait(false);
                    await _server.RunSessionAsync(input, connected, id, ct, announced).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _audit.Diagnostic($"MCP session {id} ended with error", ex);
                }
                finally
                {
                    await connected.DisposeAsync().ConfigureAwait(false);
                }
            });
            lock (_gate)
            {
                _sessions.RemoveAll(t => t.IsCompleted);
                _sessions.Add(session);
            }
        }
    }
}
