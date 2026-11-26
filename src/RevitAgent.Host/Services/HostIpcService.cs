namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using RevitAgent.Core;
    using RevitAgent.Core.Audit;
    using RevitAgent.Core.Mcp;

    /// <summary>
    /// Dashboard-facing wrapper around the gateway's MCP pipe listener (RevitAgent.Core).
    /// AI clients connect through RevitAgent.Shim (stdio relay) to <c>\\.\pipe\revitagent-mcp</c>;
    /// every tool call is executed by the Core <see cref="RevitAgent.Core.Tools.ToolRegistry"/> and
    /// mirrored into the dashboard log.
    /// </summary>
    public sealed class HostIpcService : IHostIpcService
    {
        private readonly GatewayPipeListener _listener;
        private readonly GatewayEvents _events;
        private readonly IAuditLogService _logService;
        private readonly HashSet<int> _bridgePids = new();
        private readonly object _gate = new();

        public HostIpcService(GatewayPipeListener listener, GatewayEvents events, AuditLog audit, IAuditLogService logService)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _events = events ?? throw new ArgumentNullException(nameof(events));
            _logService = logService ?? throw new ArgumentNullException(nameof(logService));

            _events.ClientConnected += id => _logService.AddLog("MCP", "Client Connected", $"AI client connected ({id}).");
            _events.ClientDisconnected += id => _logService.AddLog("MCP", "Client Disconnected", $"AI client disconnected ({id}).");
            _events.Status += (category, message) => _logService.AddLog(category, category, message);
            _events.BridgeStateChanged += OnBridgeStateChanged;
            audit.Recorded += e => _logService.AddLog(
                e.Ok ? "TOOL" : "ERROR",
                e.Tool,
                e.Ok ? $"OK {e.DurationMs} ms · {e.Args}" : $"{e.ErrorCode} {e.DurationMs} ms · {e.Args}");
        }

        public bool IsRunning => _listener.IsRunning;

        public string ActivePipeName => _listener.PipeName ?? GatewayPipeListener.DefaultPipeName;

        public bool IsBridgeConnected
        {
            get
            {
                lock (_gate)
                {
                    return _bridgePids.Count > 0;
                }
            }
        }

        public event Action<bool>? BridgeConnectionChanged;

        public event Action<bool>? ServerStateChanged;

        public Task StartServerAsync(string pipeName, CancellationToken ct = default)
        {
            if (_listener.IsRunning)
            {
                return Task.CompletedTask;
            }

            _listener.Start(string.IsNullOrWhiteSpace(pipeName) ? GatewayPipeListener.DefaultPipeName : pipeName);
            ServerStateChanged?.Invoke(true);
            _logService.AddLog("IPC", "Gateway Started", $"MCP gateway listening on \\\\.\\pipe\\{ActivePipeName}");
            return Task.CompletedTask;
        }

        public async Task StopServerAsync()
        {
            if (!_listener.IsRunning)
            {
                return;
            }

            await _listener.StopAsync().ConfigureAwait(false);
            ServerStateChanged?.Invoke(false);
            _logService.AddLog("IPC", "Gateway Stopped", "MCP gateway stopped.");
        }

        public async ValueTask DisposeAsync() => await StopServerAsync().ConfigureAwait(false);

        private void OnBridgeStateChanged(int pid, bool connected)
        {
            bool before, after;
            lock (_gate)
            {
                before = _bridgePids.Count > 0;
                if (connected)
                {
                    _bridgePids.Add(pid);
                }
                else
                {
                    _bridgePids.Remove(pid);
                }

                after = _bridgePids.Count > 0;
            }

            _logService.AddLog("BRIDGE", connected ? "Bridge Connected" : "Bridge Disconnected", $"Revit pid {pid}");
            if (before != after)
            {
                BridgeConnectionChanged?.Invoke(after);
            }
        }
    }
}
