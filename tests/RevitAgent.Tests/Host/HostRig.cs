using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;
using RevitAgent.Host.Config;
using RevitAgent.Host.Services;
using RevitAgent.Host.ViewModels;

namespace RevitAgent.Tests.Host;

/// <summary>A scripted gateway tool: records every call (name, arguments, session id) and answers with <see cref="Handler"/>.</summary>
internal sealed class FakeTool : IGatewayTool
{
    private readonly List<ToolCall> _calls = new();

    public FakeTool(string name, ToolPermission permission = ToolPermission.Read, Func<ToolCall, ToolResult>? handler = null)
    {
        Name = name;
        Permission = permission;
        Handler = handler ?? (_ => ToolResult.Json(new { ok = true }));
    }

    public string Name { get; }

    public string Description => "fake " + Name;

    public ToolPermission Permission { get; }

    public JsonElement InputSchema { get; } = Schema.Object().Build();

    public Func<ToolCall, ToolResult> Handler { get; set; }

    public IReadOnlyList<ToolCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }

        return Task.FromResult(Handler(call));
    }
}

/// <summary>
/// Dashboard + settings view models wired to an isolated gateway (temp home, temp DPAPI config, unique pipe) and a
/// registry of FAKE tools, so no test can touch a real Revit process. Listing is injected, capture and clipboard are faked.
/// </summary>
internal sealed class HostRig : IDisposable
{
    public ServiceProvider Provider { get; }
    public AgentPaths Paths { get; }
    public GatewayPolicy Policy { get; } = new();
    public EncryptedConfigService Config { get; }
    public AuditLogService Log { get; } = new();
    public NotificationService Notifications { get; } = new();
    public Dictionary<string, FakeTool> Tools { get; } = new(StringComparer.Ordinal);
    public List<RevitInstance> Instances { get; } = new();
    public List<string> Clipboard { get; } = new();
    public List<IntPtr> Captured { get; } = new();
    public ToolRegistry Registry { get; private set; } = null!;
    public DashboardViewModel Dashboard { get; private set; } = null!;
    public string BaseDirectory { get; }
    public HostIpcService Ipc { get; }
    public byte[]? CaptureBytes { get; set; } = TinyPng();

    public HostRig(IEnumerable<string>? toolNames = null, string? baseDirectory = null)
    {
        Provider = TestGateway.BuildProvider(out var paths);
        Paths = paths;
        Config = TestGateway.CreateTempConfigService();
        Ipc = new HostIpcService(
            Provider.GetRequiredService<GatewayPipeListener>(),
            Provider.GetRequiredService<GatewayEvents>(),
            Provider.GetRequiredService<AuditLog>(),
            Log);
        BaseDirectory = baseDirectory ?? TestGateway.NewTempDir("revitagent_hostbase_");

        foreach (var name in toolNames ?? new[] { "revit_list_installed", "revit_launch", "revit_close", "revit_list_dialogs" })
        {
            AddTool(new FakeTool(name, name is "revit_launch" or "revit_close" or "bridge_install" or "bridge_uninstall" ? ToolPermission.Admin : ToolPermission.Read));
        }

        Rebuild();
    }

    public FakeTool AddTool(FakeTool tool)
    {
        Tools[tool.Name] = tool;
        return tool;
    }

    /// <summary>(Re)creates the registry and dashboard from the current <see cref="Tools"/>.</summary>
    public void Rebuild()
    {
        Registry = new ToolRegistry(Tools.Values, Policy, Provider.GetRequiredService<RevitAgent.Core.Policy.ActionGuard>(), Provider.GetRequiredService<AuditLog>(), Array.Empty<IToolCallObserver>());
        Dashboard = new DashboardViewModel(
            Registry,
            Provider.GetRequiredService<RevitProcessService>(),
            Provider.GetRequiredService<GatewayPipeListener>(),
            Paths,
            Config,
            Ipc,
            Log,
            new LocalizationService(),
            Notifications,
            listInstances: () => Instances.ToArray(),
            uacParity: _ => true,
            capture: hwnd =>
            {
                Captured.Add(hwnd);
                return CaptureBytes;
            },
            setClipboard: text => Clipboard.Add(text),
            baseDirectory: BaseDirectory);
    }

    public SettingsViewModel NewSettings(GatewayPolicy? policy = null) =>
        new(Config, policy ?? Policy, Paths, Log, new LocalizationService(), null, Notifications);

    public MainViewModel NewMain(DashboardViewModel? dashboard = null, SettingsViewModel? settings = null) =>
        new(dashboard ?? Dashboard, settings ?? NewSettings(), new AuditLogViewModel(Log), Ipc, Log, new LocalizationService());

    public static RevitInstance Instance(int pid, int year = 2024, string state = "ready", bool launchedByGateway = false, bool bridge = false, string? hwnd = "0x1A2B", string? document = "Project1") =>
        new()
        {
            Pid = pid,
            Year = year,
            State = state,
            LaunchedByGateway = launchedByGateway,
            BridgePipeAvailable = bridge,
            MainWindowHwnd = hwnd,
            MainWindowTitle = "Autodesk Revit " + year,
            ActiveDocumentGuess = document,
        };

    public static byte[] TinyPng()
    {
        var pixels = new byte[4 * 4 * 4];
        var source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        Notifications.Dispose();
        Provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
