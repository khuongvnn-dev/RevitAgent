using System.IO;
using System.Collections.Concurrent;
using System.IO.Pipes;
using RevitAgent.Contracts.Bridge.Wire;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Bridge;

namespace RevitAgent.Tests.Bridge;

/// <summary>Process probe that reports the given pids as Revit (no real process is looked at).</summary>
internal sealed class FakeRevitProbe : IRevitProcessProbe
{
    private readonly Dictionary<int, RevitProcessInfo> _processes = new();

    public static readonly DateTime DefaultStart = new(2026, 11, 25, 8, 0, 0, DateTimeKind.Local);

    public FakeRevitProbe Add(int pid, string name = "Revit", int? year = 2024, DateTime? start = null, string? exePath = null)
    {
        _processes[pid] = new RevitProcessInfo(pid, name, exePath ?? $@"C:\Fake\Revit {year}\{name}.exe", year, start ?? DefaultStart);
        return this;
    }

    /// <summary>The process with <paramref name="pid"/> exits and another process reuses the pid (new start time).</summary>
    public FakeRevitProbe Reuse(int pid, string name = "Revit") => Add(pid, name, start: DefaultStart.AddHours(1));

    public FakeRevitProbe Remove(int pid)
    {
        _processes.Remove(pid);
        return this;
    }

    public RevitProcessInfo? Get(int pid) => _processes.TryGetValue(pid, out var p) ? p : null;

    public IReadOnlyList<RevitProcessInfo> ListRevit() => _processes.Values.Where(p => p.IsRevit).ToArray();
}

/// <summary>Simulates Revit's UI thread: one thread running queued work; can be paused (a modal dialog is up).</summary>
internal sealed class FakeRevitThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ManualResetEventSlim _running = new(true);
    private readonly Thread _thread;

    public FakeRevitThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "fake-revit-ui" };
        _thread.Start();
    }

    public void Pause() => _running.Reset();

    public void Resume() => _running.Set();

    public void Post(Action work) => _queue.Add(work);

    public void Dispose()
    {
        _running.Set();
        _queue.CompleteAdding();
    }

    private void Loop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                _running.Wait();
                work();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>Same state machine as the add-in's RevitWorkItem (Pending → Running → Done, or Cancelled).</summary>
internal sealed class FakeWork : IBridgeWork
{
    private readonly Func<object?> _work;
    private readonly TaskCompletionSource<object?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    public FakeWork(Func<object?> work) => _work = work;

    public Task<object?> Completion => _tcs.Task;

    public bool Started => Volatile.Read(ref _state) is 1 or 2;

    public bool TryCancelIfNotStarted()
    {
        if (Interlocked.CompareExchange(ref _state, 3, 0) != 0)
        {
            return false;
        }

        _tcs.TrySetCanceled();
        return true;
    }

    public void Run()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _tcs.TrySetResult(_work());
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
        finally
        {
            Volatile.Write(ref _state, 2);
        }
    }
}

/// <summary>Executor with a few canned tools: echo, slow (waits for Release), fail (NOT_FOUND), boom (unexpected).</summary>
internal sealed class FakeExecutor : IBridgeExecutor
{
    private readonly FakeRevitThread _ui;

    public FakeExecutor(FakeRevitThread ui) => _ui = ui;

    public ManualResetEventSlim SlowGate { get; } = new(false);

    public ConcurrentQueue<FakeWork> Scheduled { get; } = new();

    public IReadOnlyCollection<string> ToolNames { get; } = new[] { "ping", "events", "echo", "slow", "fail", "boom", "big" };

    public BridgeToolKind GetKind(string tool) => tool switch
    {
        "ping" or "events" => BridgeToolKind.Immediate,
        "echo" or "slow" or "fail" or "boom" or "big" => BridgeToolKind.Revit,
        _ => BridgeToolKind.Unknown,
    };

    public object? RunImmediate(string tool, WireObject args, BridgeServerCore server) =>
        new WireObject { { "pong", true }, { "busy_tool", server.BusyTool } };

    public IBridgeWork Schedule(string tool, WireObject args)
    {
        var work = new FakeWork(() => tool switch
        {
            "echo" => new WireObject { { "echo", args }, { "thread", Thread.CurrentThread.Name } },
            "slow" => Slow(),
            "big" => new WireObject { { "blob", new string('x', 33 * 1024 * 1024) } },
            "fail" => throw new BridgeToolException(GatewayErrorCodes.NotFound, "No such element 42.", new WireObject { { "id", 42L } }),
            _ => throw new InvalidOperationException("secret internal detail at C:\\stack\\trace.cs:line 1"),
        });
        Scheduled.Enqueue(work);
        _ui.Post(work.Run);
        return work;
    }

    public BridgeToolException MapException(string tool, Exception exception) =>
        new(GatewayErrorCodes.Internal, $"Bridge tool '{tool}' failed unexpectedly ({exception.GetType().Name}).");

    private object? Slow()
    {
        SlowGate.Wait(TimeSpan.FromSeconds(30));
        return new WireObject { { "slow", "done" } };
    }
}

internal sealed class NullBridgeLog : IBridgeLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Info(string message) => Lines.Enqueue(message);

    public void Error(string message, Exception? exception = null) => Lines.Enqueue("ERROR " + message + " " + exception?.GetType().Name);
}

/// <summary>A real BridgeServerCore on a unique test pipe + a BridgeClient pointed at it.</summary>
internal sealed class BridgeHarness : IDisposable
{
    public const int Pid = 424242;

    public BridgeHarness(TimeSpan? margin = null, TimeSpan? queueWait = null)
    {
        PipeName = "revitagent-test-bridge-" + Guid.NewGuid().ToString("N")[..10];
        Ui = new FakeRevitThread();
        Executor = new FakeExecutor(Ui);
        var name = PipeName;
        Server = new BridgeServerCore(
            name,
            () => new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly),
            Executor,
            Log);
        Server.Start();
        Events = new GatewayEvents();
        Events.BridgeStateChanged += (pid, up) => StateChanges.Enqueue((pid, up));
        Client = new BridgeClient(Events, new FakeRevitProbe().Add(Pid), new BridgeClientOptions
        {
            PipeNameForPid = _ => name,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            BridgeTimeoutMargin = margin ?? TimeSpan.FromSeconds(1),
            QueueWait = queueWait ?? BridgeClient.DefaultQueueWait,
        });
        WaitForPipe();
    }

    public string PipeName { get; }

    public FakeRevitThread Ui { get; }

    public FakeExecutor Executor { get; }

    public NullBridgeLog Log { get; } = new();

    public BridgeServerCore Server { get; }

    public GatewayEvents Events { get; }

    public ConcurrentQueue<(int Pid, bool Up)> StateChanges { get; } = new();

    public BridgeClient Client { get; }

    public void Dispose()
    {
        Executor.SlowGate.Set();
        Server.Stop();
        Ui.Dispose();
    }

    private void WaitForPipe()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!Client.IsPipePresent(Pid) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }
}
