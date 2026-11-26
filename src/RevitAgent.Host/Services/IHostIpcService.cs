namespace RevitAgent.Host.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IHostIpcService : IAsyncDisposable
    {
        bool IsRunning { get; }
        string ActivePipeName { get; }
        bool IsBridgeConnected { get; }

        event Action<bool>? BridgeConnectionChanged;
        event Action<bool>? ServerStateChanged;

        Task StartServerAsync(string pipeName, CancellationToken ct = default);
        Task StopServerAsync();
    }
}
