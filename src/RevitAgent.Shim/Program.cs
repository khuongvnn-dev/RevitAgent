namespace RevitAgent.Shim
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.IO.Pipes;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using RevitAgent.Core.Composition;
    using RevitAgent.Core.Mcp;

    /// <summary>
    /// MCP stdio entry point for AI clients (Claude Code / Desktop, Cursor, Antigravity...).
    ///
    /// Default (relay): connect to the RevitAgent Host dashboard on \\.\pipe\revitagent-mcp and copy bytes
    /// both ways — the Host runs the official MCP SDK, so the Shim never parses the protocol. If the Host is
    /// not running it is started (minimized) and awaited. If it still cannot be reached, the Shim falls back to
    /// running the gateway in-process (headless) so the agent always gets a working server.
    ///
    /// Flags: --headless (always in-process), --pipe &lt;name&gt;, --no-autostart, --host-exe &lt;path&gt;,
    /// --profile core|full (tools/list profile; default core; env REVITAGENT_PROFILE, the flag wins). In relay mode the
    /// Shim writes one notification line with the profile (<see cref="ToolProfiles.NotificationLine"/>) before relaying
    /// the client's bytes unchanged; headless mode passes it to the in-process gateway.
    /// Diagnostics go to stderr only (stdout belongs to the MCP protocol).
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string pipeName = GatewayPipeListener.DefaultPipeName;
            bool headless = false, autostart = true;
            string? hostExe = Environment.GetEnvironmentVariable("REVITAGENT_HOST_EXE");
            string? profileArg = Environment.GetEnvironmentVariable(ToolProfiles.EnvironmentVariable);
            bool checkMachine = false;
            int checkYear = 2024;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--check-machine":
                        checkMachine = true;
                        break;
                    case "--profile" when i + 1 < args.Length:
                        profileArg = args[++i];
                        break;
                    case "--year" or "-y" when i + 1 < args.Length && int.TryParse(args[i + 1], out var y):
                        checkYear = y;
                        i++;
                        break;
                    case "--pipe" or "-p" when i + 1 < args.Length:
                        pipeName = args[++i];
                        break;
                    case "--headless":
                        headless = true;
                        break;
                    case "--no-autostart":
                        autostart = false;
                        break;
                    case "--host-exe" when i + 1 < args.Length:
                        hostExe = args[++i];
                        break;
                }
            }

            if (checkMachine)
            {
                return RunCheckMachine(checkYear);
            }

            // null = not configured: relay unchanged, the server defaults to core.
            string? profile = null;
            if (!string.IsNullOrWhiteSpace(profileArg))
            {
                profile = ToolProfiles.Parse(profileArg);
                if (profile is null)
                {
                    Log($"unknown profile '{profileArg}' (use core or full); using {ToolProfiles.Core}.");
                }
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            if (!headless)
            {
                var pipe = await ConnectAsync(pipeName, TimeSpan.FromMilliseconds(800), cts.Token).ConfigureAwait(false);
                if (pipe is null && autostart && TryStartHost(hostExe))
                {
                    pipe = await ConnectAsync(pipeName, TimeSpan.FromSeconds(25), cts.Token).ConfigureAwait(false);
                }

                if (pipe is not null)
                {
                    await using (pipe)
                    {
                        Log($"relaying stdio <-> \\\\.\\pipe\\{pipeName}{(profile is null ? string.Empty : $" (profile {profile})")}");
                        await RelayAsync(pipe, profile, cts.Token).ConfigureAwait(false);
                    }

                    return 0;
                }

                Log("RevitAgent Host not reachable; running the gateway headless in this process.");
            }

            await using var services = GatewayServices.BuildGateway();
            services.GetService<RevitAgent.Core.Revit.RevitWatchdog>()?.Start();
            await services.GetRequiredService<McpGatewayServer>().RunStdioAsync(cts.Token, profile).ConfigureAwait(false);
            return 0;
        }

        private static async Task<NamedPipeClientStream?> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    await client.ConnectAsync((int)Math.Clamp(left.TotalMilliseconds, 50, 1000), ct).ConfigureAwait(false);
                    return client;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    if (DateTime.UtcNow >= deadline)
                    {
                        return null;
                    }

                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }
        }

        private static async Task RelayAsync(Stream pipe, string? profile, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (profile is not null)
            {
                // One notification line before the client's first byte; everything after it is relayed unchanged.
                try
                {
                    await pipe.WriteAsync(ToolProfiles.NotificationLine(profile), ct).ConfigureAwait(false);
                    await pipe.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    return; // the Host closed the pipe: the session is over
                }
            }

            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var tracker = new PendingRequestTracker();
            var up = PumpAsync(stdin, pipe, linked.Token, tracker.ObserveUpstream);
            var down = PumpAsync(pipe, stdout, linked.Token, tracker.ObserveDownstream);
            var first = await Task.WhenAny(up, down).ConfigureAwait(false);
            if (first == up)
            {
                // The client closed stdin (a one-shot "type requests | Shim"): keep delivering the responses still
                // in flight. Wait until none is pending, the Host side ends, or the longest tool timeout (600 s) passed.
                var deadline = DateTime.UtcNow + DrainCap;
                while (!down.IsCompleted && tracker.PendingCount > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    await Task.WhenAny(down, Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
                }
            }

            linked.Cancel();
        }

        private static readonly TimeSpan DrainCap = TimeSpan.FromMinutes(11);

        private delegate void ObserveBytes(ReadOnlySpan<byte> bytes);

        private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct, ObserveBytes observe)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    observe(buffer.AsSpan(0, n));
                    await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    await to.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // One side closed: the session is over.
            }
        }

        private static bool TryStartHost(string? hostExe)
        {
            var exe = ResolveHostExe(hostExe);
            if (exe is null)
            {
                Log("RevitAgent.Host.exe not found (set REVITAGENT_HOST_EXE or --host-exe).");
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe, "--minimized") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Log($"started {exe}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"could not start {exe}: {ex.Message}");
                return false;
            }
        }

        private static string? ResolveHostExe(string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }

            var baseDir = AppContext.BaseDirectory;
            var sameDir = Path.Combine(baseDir, "RevitAgent.Host.exe");
            if (File.Exists(sameDir))
            {
                return sameDir;
            }

            // Developer layout: src\RevitAgent.Shim\bin\<cfg>\<tfm>\ → src\RevitAgent.Host\bin\<cfg>\<tfm>\
            var tfm = new DirectoryInfo(baseDir.TrimEnd('\\', '/'));
            var cfg = tfm.Parent;
            var srcDir = cfg?.Parent?.Parent?.Parent;
            if (cfg is not null && srcDir is not null)
            {
                var dev = Path.Combine(srcDir.FullName, "RevitAgent.Host", "bin", cfg.Name, tfm.Name, "RevitAgent.Host.exe");
                if (File.Exists(dev))
                {
                    return dev;
                }
            }

            return null;
        }

        private static void Log(string message) => Console.Error.WriteLine($"[revitagent-shim] {message}");

        private static int RunCheckMachine(int year)
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                var readiness = new RevitAgent.Core.Machine.MachineReadiness();
                var report = readiness.Check(new RevitAgent.Core.Machine.MachineCheckOptions { Year = year });
                var json = RevitAgent.Core.Tools.GatewayJson.Serialize(report);
                Console.WriteLine(json);
                return report.ReadyForUnattended ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[revitagent-shim] Error running machine readiness check: {ex.Message}");
                return 2;
            }
        }
    }
}
