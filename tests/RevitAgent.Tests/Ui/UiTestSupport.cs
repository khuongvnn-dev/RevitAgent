using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Tests.Ui;

/// <summary>
/// A fact that needs an unlocked interactive desktop (it drives a real window). Skipped — not failed — when the
/// session is locked, non-interactive (service/CI agent), or REVITAGENT_SKIP_UI_TESTS=1.
/// </summary>
public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        var reason = UiTestEnvironment.SkipReason();
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}

/// <summary>Theory counterpart of <see cref="DesktopFactAttribute"/> (real windows / dialogs on the desktop).</summary>
public sealed class DesktopTheoryAttribute : TheoryAttribute
{
    public DesktopTheoryAttribute()
    {
        var reason = UiTestEnvironment.SkipReason();
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}

public static class UiTestEnvironment
{
    public static string? SkipReason()
    {
        if (Environment.GetEnvironmentVariable("REVITAGENT_SKIP_UI_TESTS") == "1")
        {
            return "REVITAGENT_SKIP_UI_TESTS=1";
        }

        if (!Environment.UserInteractive)
        {
            return "No interactive desktop (service / non-interactive session): UI automation tests skipped.";
        }

        var reason = UiEnvironment.DesktopUnavailableReason();
        return reason is null ? null : "UI automation tests skipped: " + reason;
    }
}

/// <summary>UI tests share one desktop (focus, mouse): never run them in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiDesktopCollection
{
    public const string Name = "ui-desktop";
}

/// <summary>
/// Launches tests/RevitAgent.UiTestApp (built with the solution) and always kills it on dispose. The tests only ever
/// target this process id.
/// </summary>
public sealed class UiTestApp : IDisposable
{
    public const string MainTitle = "RevitAgent UI Test App";
    private static readonly object BuildGate = new();

    private UiTestApp(Process process) => Process = process;

    public Process Process { get; }

    public int Pid => Process.Id;

    public static UiTestApp Start(string? title = null)
    {
        var exe = LocateExe();
        var psi = new ProcessStartInfo(exe, title is null ? "--lifetime 300" : $"--lifetime 300 --title \"{title}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the UI test app.");
        var app = new UiTestApp(p);
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (p.HasExited)
                {
                    throw new InvalidOperationException($"UI test app exited early (code {p.ExitCode}).");
                }

                if (TopLevelWindows.List(p.Id).Any(w => w.Title == (title ?? MainTitle)))
                {
                    Thread.Sleep(300); // let WPF finish the first layout pass
                    return app;
                }

                Thread.Sleep(100);
            }

            throw new TimeoutException("UI test app main window did not appear within 30 s.");
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }

        Process.Dispose();
    }

    /// <summary>Finds the built exe next to the test output (same configuration first); builds it once if missing.</summary>
    public static string LocateExe()
    {
        var repo = FindRepoRoot();
        var baseDir = AppContext.BaseDirectory;
        var configs = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };
        string Candidate(string cfg) => Path.Combine(repo, "tests", "RevitAgent.UiTestApp", "bin", cfg, "net10.0-windows", "RevitAgent.UiTestApp.exe");
        foreach (var cfg in configs)
        {
            if (File.Exists(Candidate(cfg)))
            {
                return Candidate(cfg);
            }
        }

        lock (BuildGate)
        {
            if (!File.Exists(Candidate(configs[0])))
            {
                var csproj = Path.Combine(repo, "tests", "RevitAgent.UiTestApp", "RevitAgent.UiTestApp.csproj");
                using var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{csproj}\" -c {configs[0]} --nologo -v q")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                build.StandardOutput.ReadToEnd();
                build.WaitForExit(180_000);
            }
        }

        return File.Exists(Candidate(configs[0]))
            ? Candidate(configs[0])
            : throw new FileNotFoundException("RevitAgent.UiTestApp.exe not found; build RevitAgent.slnx first.", Candidate(configs[0]));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RevitAgent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (RevitAgent.slnx) not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Gateway (temp REVITAGENT home) + test app; calls tools through the real <see cref="ToolRegistry"/>.</summary>
public sealed class UiHarness : IDisposable
{
    private readonly ServiceProvider _sp;

    private UiHarness(ServiceProvider sp, AgentPaths paths, UiTestApp app)
    {
        _sp = sp;
        Paths = paths;
        App = app;
        Registry = sp.GetRequiredService<ToolRegistry>();
    }

    public AgentPaths Paths { get; }

    public UiTestApp App { get; }

    public ToolRegistry Registry { get; }

    public string Session { get; } = "ui-test-" + Guid.NewGuid().ToString("N")[..8];

    public int Pid => App.Pid;

    public static UiHarness Start(string? appTitle = null)
    {
        var sp = TestGateway.BuildProvider(out var paths);
        try
        {
            return new UiHarness(sp, paths, UiTestApp.Start(appTitle));
        }
        catch
        {
            sp.Dispose();
            throw;
        }
    }

    public async Task<ToolResult> CallRawAsync(string tool, object args)
    {
        var json = JsonSerializer.SerializeToElement(args);
        return await Registry.InvokeAsync(new ToolCall(tool, json, Session), CancellationToken.None);
    }

    /// <summary>Calls a tool and asserts success; returns the structured JSON result.</summary>
    public async Task<JsonElement> CallAsync(string tool, object args)
    {
        var r = await CallRawAsync(tool, args);
        Assert.False(r.IsError, $"{tool} failed: {Text(r)}");
        return r.Structured!.Value;
    }

    /// <summary>Calls a tool and asserts it failed; returns (code, message).</summary>
    public async Task<(string Code, string Message)> CallErrorAsync(string tool, object args)
    {
        var r = await CallRawAsync(tool, args);
        Assert.True(r.IsError, $"{tool} unexpectedly succeeded: {Text(r)}");
        var s = r.Structured!.Value;
        return (s.GetProperty("code").GetString()!, s.GetProperty("message").GetString()!);
    }

    public static string Text(ToolResult r) => string.Join("\n", r.Content.OfType<TextContent>().Select(t => t.Text));

    /// <summary>Snapshot text of the main window (or of a named window).</summary>
    public async Task<string> SnapshotTextAsync(string? window = null)
    {
        var r = await CallRawAsync("ui_snapshot", window is null ? new { pid = Pid } : new { pid = Pid, window });
        Assert.False(r.IsError, Text(r));
        return ((TextContent)r.Content[1]).Text;
    }

    /// <summary>Ref of the first snapshot line containing <paramref name="fragment"/>.</summary>
    public static string RefOf(string snapshot, string fragment)
    {
        var line = snapshot.Split('\n').FirstOrDefault(l => l.Contains(fragment, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"'{fragment}' not in snapshot:\n{snapshot}");
        var m = System.Text.RegularExpressions.Regex.Match(line, "\\[ref=(e\\d+)\\]");
        Assert.True(m.Success, line);
        return m.Groups[1].Value;
    }

    public async Task ExpectAsync(object args)
    {
        var r = await CallAsync("ui_expect", args);
        Assert.True(r.GetProperty("pass").GetBoolean(), r.GetProperty("message").GetString());
    }

    public void Dispose()
    {
        App.Dispose();
        _sp.Dispose();
    }
}
