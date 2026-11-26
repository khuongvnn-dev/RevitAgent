namespace RevitAgent.Bridge.Runtime
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Autodesk.Revit.UI;
    using RevitAgent.Bridge.Tools;
    using RevitAgent.Contracts.Bridge.Wire;
    using RevitAgent.Contracts.Protocol;

    /// <summary>
    /// Marshals Bridge tools onto the Revit UI thread through one <see cref="ExternalEvent"/>.
    /// The pipe thread enqueues a <see cref="RevitWorkItem"/> and raises the event; Revit runs the
    /// handler when it is idle (never while a modal dialog or command is running). The pipe thread waits
    /// on the work item's task, never the UI thread.
    /// </summary>
    internal sealed class RevitExecutor : IExternalEventHandler, IBridgeExecutor
    {
        private readonly ConcurrentQueue<RevitWorkItem> _queue = new ConcurrentQueue<RevitWorkItem>();
        private readonly BridgeToolRegistry _tools;
        private readonly IBridgeLog _log;
        private ExternalEvent? _event;

        public RevitExecutor(BridgeToolRegistry tools, IBridgeLog log)
        {
            _tools = tools;
            _log = log;
        }

        public IReadOnlyCollection<string> ToolNames => _tools.Names;

        /// <summary>Creates the ExternalEvent. Must be called from a valid Revit API context (OnStartup).</summary>
        public void CreateEvent() => _event = ExternalEvent.Create(this);

        public void DisposeEvent()
        {
            var ev = _event;
            _event = null;
            while (_queue.TryDequeue(out var item))
            {
                item.TryCancelIfNotStarted();
            }

            ev?.Dispose();
        }

        public BridgeToolKind GetKind(string tool) => _tools.GetKind(tool);

        public object? RunImmediate(string tool, WireObject args, BridgeServerCore server) =>
            _tools.RunImmediate(tool, new ToolArgs(args), server);

        public IBridgeWork Schedule(string tool, WireObject args)
        {
            var ev = _event ?? throw new BridgeToolException(GatewayErrorCodes.DependencyFailed, "The Bridge is shutting down or failed to initialize; restart Revit.");
            var handler = _tools.GetRevitHandler(tool);
            var toolArgs = new ToolArgs(args);
            var item = new RevitWorkItem(app => handler(new ToolContext(app, toolArgs, _tools.Runtime, _tools.Dialogs)));
            _queue.Enqueue(item);
            var raised = ev.Raise();
            if (raised != ExternalEventRequest.Accepted && raised != ExternalEventRequest.Pending)
            {
                _log.Info("ExternalEvent.Raise returned " + raised + " for " + tool + "; the call waits for the next Revit idle cycle.");
            }

            return item;
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Run(app);
            }
        }

        public string GetName() => "RevitAgent Bridge";

        public BridgeToolException MapException(string tool, Exception exception) => RevitErrors.Map(tool, exception, _tools.Runtime.LogFile);
    }

    /// <summary>One Bridge call scheduled onto the Revit UI thread.</summary>
    internal sealed class RevitWorkItem : IBridgeWork
    {
        private const int Pending = 0;
        private const int Running = 1;
        private const int Done = 2;
        private const int Cancelled = 3;

        private readonly Func<UIApplication, object?> _work;
        private readonly TaskCompletionSource<object?> _tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        public RevitWorkItem(Func<UIApplication, object?> work) => _work = work;

        public Task<object?> Completion => _tcs.Task;

        public bool TryCancelIfNotStarted()
        {
            if (Interlocked.CompareExchange(ref _state, Cancelled, Pending) != Pending)
            {
                return false;
            }

            _tcs.TrySetCanceled();
            return true;
        }

        /// <summary>Runs on the Revit UI thread (inside ExternalEvent.Execute).</summary>
        public void Run(UIApplication app)
        {
            if (Interlocked.CompareExchange(ref _state, Running, Pending) != Pending)
            {
                return; // cancelled after a timeout before Revit got to it
            }

            var previous = SynchronizationContext.Current;
            try
            {
                // No UI SynchronizationContext while tools run: protects against sync-over-async deadlocks.
                SynchronizationContext.SetSynchronizationContext(null);
                var result = _work(app);

                // Serialize here, on the Revit thread: results may hold lazy sequences over Revit objects
                // that must not be enumerated from the pipe thread.
                _tcs.TrySetResult(new WireRawJson(WireJson.Serialize(result)));
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                Volatile.Write(ref _state, Done);
            }
        }
    }

    /// <summary>Maps Revit/.NET exceptions to gateway error codes without leaking stack traces.</summary>
    internal static class RevitErrors
    {
        public static BridgeToolException Map(string tool, Exception ex, string logFile)
        {
            if (ex is BridgeToolException bte)
            {
                return bte;
            }

            var type = ex.GetType();
            var name = type.Name;
            var message = (ex.Message ?? string.Empty).Trim();
            if ((type.Namespace ?? string.Empty).StartsWith("Autodesk.Revit.Exceptions", StringComparison.Ordinal))
            {
                if (name.IndexOf("Argument", StringComparison.Ordinal) >= 0)
                {
                    return new BridgeToolException(GatewayErrorCodes.ValidationFailed, "Revit rejected the arguments of '" + tool + "': " + message);
                }

                if (name == "InvalidObjectException")
                {
                    return new BridgeToolException(GatewayErrorCodes.NotFound, "The Revit object no longer exists (" + message + ").");
                }

                if (name == "FileNotFoundException" || name == "DirectoryNotFoundException")
                {
                    return new BridgeToolException(GatewayErrorCodes.NotFound, message);
                }

                if (name == "OperationCanceledException")
                {
                    return new BridgeToolException(GatewayErrorCodes.Conflict, "Revit cancelled '" + tool + "' (" + message + "). A dialog may have been cancelled; check revit_list_dialogs.");
                }

                if (name.IndexOf("InvalidOperation", StringComparison.Ordinal) >= 0 || name.IndexOf("Modification", StringComparison.Ordinal) >= 0 ||
                    name.IndexOf("FileAccess", StringComparison.Ordinal) >= 0)
                {
                    return new BridgeToolException(GatewayErrorCodes.Conflict, "Revit refused '" + tool + "' in its current state: " + message);
                }

                return new BridgeToolException(GatewayErrorCodes.Internal, "Revit API error in '" + tool + "' (" + name + "): " + message);
            }

            if (ex is ArgumentException || ex is FormatException)
            {
                return new BridgeToolException(GatewayErrorCodes.ValidationFailed, message);
            }

            if (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return new BridgeToolException(GatewayErrorCodes.NotFound, message);
            }

            return new BridgeToolException(
                GatewayErrorCodes.Internal,
                "Bridge tool '" + tool + "' failed unexpectedly (" + name + "). Details are in the Bridge log " + Path.GetFileName(logFile) + ".");
        }
    }
}
