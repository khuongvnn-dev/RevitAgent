namespace RevitAgent.Contracts.Bridge.Wire
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.IO.Pipes;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using RevitAgent.Contracts.Protocol;

    /// <summary>Where a Bridge tool runs.</summary>
    internal enum BridgeToolKind
    {
        /// <summary>Not a Bridge tool.</summary>
        Unknown = 0,

        /// <summary>No Revit API access: runs on the pipe thread, never BUSY (ping, dialog events, enum lists).</summary>
        Immediate = 1,

        /// <summary>Needs the Revit API: marshalled to the Revit UI thread, one at a time (BUSY otherwise).</summary>
        Revit = 2,
    }

    /// <summary>Expected tool failure with a gateway error code (<see cref="GatewayErrorCodes"/>).</summary>
    internal sealed class BridgeToolException : Exception
    {
        public BridgeToolException(string code, string message, object? details = null)
            : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? GatewayErrorCodes.Internal : code;
            Details = details;
        }

        public string Code { get; }

        /// <summary>Optional JSON-serializable details (<see cref="WireJson"/> model).</summary>
        public object? Details { get; }
    }

    /// <summary>Work scheduled onto the Revit UI thread.</summary>
    internal interface IBridgeWork
    {
        /// <summary>Completes with the result, the tool's exception, or as cancelled (never started).</summary>
        Task<object?> Completion { get; }

        /// <summary>Atomically cancels the work if Revit has not started it yet. True = it will never run.</summary>
        bool TryCancelIfNotStarted();
    }

    /// <summary>Executes Bridge tools (implemented by the add-in; faked in tests).</summary>
    internal interface IBridgeExecutor
    {
        BridgeToolKind GetKind(string tool);

        IReadOnlyCollection<string> ToolNames { get; }

        object? RunImmediate(string tool, WireObject args, BridgeServerCore server);

        IBridgeWork Schedule(string tool, WireObject args);

        /// <summary>Maps an unexpected exception (e.g. a Revit API exception) to an error. Must not leak stack traces.</summary>
        BridgeToolException MapException(string tool, Exception exception);
    }

    internal interface IBridgeLog
    {
        void Info(string message);

        void Error(string message, Exception? exception = null);
    }

    /// <summary>
    /// Bridge pipe server: accepts Host connections on <c>\\.\pipe\revitagent-bridge-&lt;pid&gt;</c>
    /// (several connections, several sequential requests per connection), answers immediate tools on the
    /// pipe thread, and runs Revit tools one at a time through <see cref="IBridgeExecutor.Schedule"/>.
    /// A Revit request that arrives while another is in flight gets BUSY immediately (no silent queueing).
    /// Never blocks the Revit UI thread: waiting happens here, on thread-pool threads.
    /// </summary>
    internal sealed class BridgeServerCore : IDisposable
    {
        private const int LogArgsChars = 512;

        private readonly Func<NamedPipeServerStream> _pipeFactory;
        private readonly IBridgeExecutor _executor;
        private readonly IBridgeLog _log;
        private readonly object _gate = new object();
        private readonly HashSet<Stream> _connections = new HashSet<Stream>();
        private CancellationTokenSource? _cts;
        private NamedPipeServerStream? _pending;
        private readonly object _busyGate = new object();
        private Task? _acceptLoop;
        private long _busyGeneration;
        private long _generationCounter;
        private string? _busyTool;
        private DateTime _busySinceUtc;
        private long _requestCount;

        public BridgeServerCore(string pipeName, Func<NamedPipeServerStream> pipeFactory, IBridgeExecutor executor, IBridgeLog log)
        {
            PipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public string PipeName { get; }

        /// <summary>Idle connections are closed after this long without a request.</summary>
        public TimeSpan IdleConnectionTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public long RequestCount => Interlocked.Read(ref _requestCount);

        /// <summary>Name of the Revit tool currently in flight, or null.</summary>
        public string? BusyTool
        {
            get
            {
                lock (_busyGate)
                {
                    return _busyGeneration != 0 ? _busyTool : null;
                }
            }
        }

        /// <summary>Seconds the current Revit tool has been running (0 when idle).</summary>
        public double BusySeconds
        {
            get
            {
                lock (_busyGate)
                {
                    return _busyGeneration != 0 ? (DateTime.UtcNow - _busySinceUtc).TotalSeconds : 0;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_cts != null)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(token));
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            NamedPipeServerStream? pending;
            Stream[] open;
            lock (_gate)
            {
                cts = _cts;
                _cts = null;
                pending = _pending;
                _pending = null;
                open = new Stream[_connections.Count];
                _connections.CopyTo(open);
                _connections.Clear();
            }

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // Disposing aborts pending waits/reads (cancellation tokens alone do not on .NET Framework pipes).
            SafeDispose(pending);
            foreach (var s in open)
            {
                SafeDispose(s);
            }

            try
            {
                _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            cts.Dispose();
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            var failures = 0;
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = _pipeFactory();
                    lock (_gate)
                    {
                        if (token.IsCancellationRequested)
                        {
                            pipe.Dispose();
                            return;
                        }

                        _pending = pipe;
                    }

                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _pending = null;
                        if (token.IsCancellationRequested)
                        {
                            pipe.Dispose();
                            return;
                        }

                        _connections.Add(pipe);
                    }

                    failures = 0;
                    var connection = pipe;
                    pipe = null;
                    _ = Task.Run(() => ServeConnectionAsync(connection, token));
                }
                catch (Exception ex) when (token.IsCancellationRequested && (ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException))
                {
                    SafeDispose(pipe);
                    return;
                }
                catch (Exception ex)
                {
                    SafeDispose(pipe);
                    lock (_gate)
                    {
                        _pending = null;
                    }

                    failures++;
                    if (failures <= 3 || failures % 100 == 0)
                    {
                        _log.Error("Pipe accept failed (" + failures + ")", ex);
                    }

                    try
                    {
                        await Task.Delay(failures < 10 ? 200 : 2000, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && pipe.IsConnected)
                {
                    string? request;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        idle.CancelAfter(IdleConnectionTimeout);
                        using (idle.Token.Register(() => SafeDispose(pipe)))
                        {
                            request = await WireFraming.ReadFrameAsync(pipe, idle.Token).ConfigureAwait(false);
                        }
                    }

                    if (request == null)
                    {
                        break;
                    }

                    var response = await HandleRequestAsync(request).ConfigureAwait(false);
                    await WireFraming.WriteFrameAsync(pipe, response, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is EndOfStreamException)
            {
                // Client went away, idle timeout or shutdown: normal.
            }
            catch (Exception ex)
            {
                _log.Error("Connection failed", ex);
            }
            finally
            {
                lock (_gate)
                {
                    _connections.Remove(pipe);
                }

                SafeDispose(pipe);
            }
        }

        /// <summary>Handles one request JSON and returns the response JSON. Never throws.</summary>
        public async Task<string> HandleRequestAsync(string requestJson)
        {
            Interlocked.Increment(ref _requestCount);
            var sw = Stopwatch.StartNew();
            var id = string.Empty;
            var tool = string.Empty;
            string response;
            try
            {
                WireObject request;
                try
                {
                    request = WireJson.ParseObject(requestJson);
                }
                catch (FormatException ex)
                {
                    throw new BridgeToolException(GatewayErrorCodes.ValidationFailed, "Bridge request is not valid JSON: " + ex.Message);
                }

                id = WireValues.AsString(request["id"]) ?? string.Empty;
                tool = WireValues.AsString(request["tool"]) ?? string.Empty;
                if (tool.Length == 0)
                {
                    throw new BridgeToolException(GatewayErrorCodes.ValidationFailed, "Bridge request has no 'tool'.");
                }

                var rawArgs = request["args"];
                var args = rawArgs as WireObject;
                if (args == null)
                {
                    if (rawArgs != null)
                    {
                        throw new BridgeToolException(GatewayErrorCodes.ValidationFailed, "Bridge request 'args' must be a JSON object.");
                    }

                    args = new WireObject();
                }

                long timeoutSec;
                try
                {
                    timeoutSec = WireValues.AsLong(request["timeout_sec"]) ?? 0;
                }
                catch (FormatException)
                {
                    throw new BridgeToolException(GatewayErrorCodes.ValidationFailed, "Bridge request 'timeout_sec' must be an integer.");
                }

                if (timeoutSec <= 0)
                {
                    timeoutSec = BridgeWire.DefaultTimeoutSec;
                }

                timeoutSec = Math.Min(timeoutSec, BridgeWire.MaxTimeoutSec);

                _log.Info("-> " + tool + " id=" + id + " args=" + Clip(WireJson.Serialize(args), LogArgsChars));
                var result = await ExecuteAsync(tool, args, TimeSpan.FromSeconds(timeoutSec)).ConfigureAwait(false);
                response = WireJson.Serialize(new WireObject { { "id", id }, { "ok", true }, { "result", result } });
                _log.Info("<- " + tool + " id=" + id + " ok " + sw.ElapsedMilliseconds + " ms, " + response.Length + " chars");
            }
            catch (Exception ex)
            {
                var error = ex as BridgeToolException ?? SafeMap(tool, ex);
                var body = new WireObject { { "code", error.Code }, { "message", error.Message } };
                if (error.Details != null)
                {
                    body["details"] = error.Details;
                }

                try
                {
                    response = WireJson.Serialize(new WireObject { { "id", id }, { "ok", false }, { "error", body } });
                }
                catch (Exception serializeEx)
                {
                    _log.Error("Could not serialize error details for " + tool, serializeEx);
                    response = WireJson.Serialize(new WireObject
                    {
                        { "id", id },
                        { "ok", false },
                        { "error", new WireObject { { "code", error.Code }, { "message", error.Message } } },
                    });
                }

                _log.Info("<- " + tool + " id=" + id + " " + error.Code + " " + sw.ElapsedMilliseconds + " ms: " + Clip(error.Message, LogArgsChars));
            }

            var responseBytes = Encoding.UTF8.GetByteCount(response);
            if (responseBytes > BridgeWire.MaxPayloadBytes)
            {
                // WireFraming would throw and the connection would close; answer with a clear error instead.
                var mb = (responseBytes + 1024 * 1024 - 1) / (1024 * 1024);
                var limitMb = BridgeWire.MaxPayloadBytes / (1024 * 1024);
                var message = "result too large (" + mb + " MB > " + limitMb + " MB); lower limit or narrow the query";
                _log.Info("<- " + tool + " id=" + id + " " + GatewayErrorCodes.ValidationFailed + ": " + message);
                response = WireJson.Serialize(new WireObject
                {
                    { "id", id },
                    { "ok", false },
                    { "error", new WireObject { { "code", GatewayErrorCodes.ValidationFailed }, { "message", message } } },
                });
            }

            return response;
        }

        private async Task<object?> ExecuteAsync(string tool, WireObject args, TimeSpan timeout)
        {
            switch (_executor.GetKind(tool))
            {
                case BridgeToolKind.Immediate:
                    return _executor.RunImmediate(tool, args, this);
                case BridgeToolKind.Revit:
                    return await RunOnRevitAsync(tool, args, timeout).ConfigureAwait(false);
                default:
                    throw new BridgeToolException(
                        GatewayErrorCodes.NotFound,
                        "Unknown Bridge tool '" + tool + "'. The Bridge loaded in this Revit may be older than the gateway; reinstall it with bridge_install and restart Revit.",
                        new WireObject { { "supported_tools", new List<string>(_executor.ToolNames) } });
            }
        }

        private async Task<object?> RunOnRevitAsync(string tool, WireObject args, TimeSpan timeout)
        {
            long generation;
            string running;
            double seconds;
            lock (_busyGate)
            {
                generation = _busyGeneration == 0 ? ++_generationCounter : 0;
                if (generation != 0)
                {
                    _busyGeneration = generation;
                    _busyTool = tool;
                    _busySinceUtc = DateTime.UtcNow;
                }

                running = _busyTool ?? "?";
                seconds = Math.Round((DateTime.UtcNow - _busySinceUtc).TotalSeconds, 1);
            }

            if (generation == 0)
            {
                throw new BridgeToolException(
                    GatewayErrorCodes.Busy,
                    "Revit is still running '" + running + "' (" + seconds.ToString(CultureInfo.InvariantCulture) + " s). The Revit API runs one call at a time; retry when it finishes. " +
                    "If it seems stuck, a modal dialog may be open: check dialog_list_events / revit_list_dialogs.",
                    new WireObject { { "running_tool", running }, { "running_sec", seconds } });
            }

            IBridgeWork work;
            try
            {
                work = _executor.Schedule(tool, args);
            }
            catch
            {
                ReleaseBusy(generation);
                throw;
            }

            // Released when the work really ends (it may outlive a TIMEOUT answer), and released eagerly
            // below before answering, so the Host's next call never sees a stale BUSY.
            _ = work.Completion.ContinueWith(_ => ReleaseBusy(generation), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            using (var delayCts = new CancellationTokenSource())
            {
                var delay = Task.Delay(timeout, delayCts.Token);
                var finished = await Task.WhenAny(work.Completion, delay).ConfigureAwait(false);
                if (finished == work.Completion)
                {
                    delayCts.Cancel();
                    ReleaseBusy(generation);
                    try
                    {
                        return await work.Completion.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new BridgeToolException(GatewayErrorCodes.Timeout, "Bridge call '" + tool + "' was cancelled before Revit ran it.");
                    }
                    catch (AggregateException agg) when (agg.InnerException != null)
                    {
                        throw agg.InnerException;
                    }
                }
            }

            var secs = ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            if (work.TryCancelIfNotStarted())
            {
                ReleaseBusy(generation);
                throw new BridgeToolException(
                    GatewayErrorCodes.Timeout,
                    "Revit did not start '" + tool + "' within " + secs + " s, so the call was cancelled and did NOT run. Revit is probably showing a modal dialog or running a command: " +
                    "check dialog_list_events / revit_list_dialogs, resolve it, then retry.",
                    new WireObject { { "started", false } });
            }

            throw new BridgeToolException(
                GatewayErrorCodes.Timeout,
                "'" + tool + "' started in Revit but did not finish within " + secs + " s. It may still be running in Revit; further Revit calls return BUSY until it finishes. " +
                "Check state (bridge_status, dialog_list_events) before retrying.",
                new WireObject { { "started", true } });
        }

        private void ReleaseBusy(long generation)
        {
            lock (_busyGate)
            {
                if (_busyGeneration == generation)
                {
                    _busyGeneration = 0;
                    _busyTool = null;
                }
            }
        }

        private BridgeToolException SafeMap(string tool, Exception ex)
        {
            while (ex is AggregateException agg && agg.InnerException != null)
            {
                ex = agg.InnerException;
            }

            if (ex is BridgeToolException bte)
            {
                return bte;
            }

            try
            {
                var mapped = _executor.MapException(tool, ex);
                _log.Error("Tool " + tool + " failed: " + mapped.Code, ex);
                return mapped;
            }
            catch (Exception mapEx)
            {
                _log.Error("Exception mapping failed for " + tool, mapEx);
                return new BridgeToolException(GatewayErrorCodes.Internal, "Bridge tool '" + tool + "' failed unexpectedly (" + ex.GetType().Name + "). See the bridge log.");
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...(" + s.Length + " chars)";

        private static void SafeDispose(IDisposable? d)
        {
            try
            {
                d?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
