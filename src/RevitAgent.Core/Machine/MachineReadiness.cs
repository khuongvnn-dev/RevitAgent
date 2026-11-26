using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Install;
using RevitAgent.Core.Mcp;
using RevitAgent.Core.Revit;

namespace RevitAgent.Core.Machine;

/// <summary>
/// Probes machine readiness for running Revit tests unattended overnight.
/// One C# implementation backing the MCP tool, the Shim CLI flag and the PowerShell script.
/// </summary>
public sealed class MachineReadiness
{
    private readonly AgentPaths _paths;
    private readonly BridgeInstaller? _bridgeInstaller;

    public MachineReadiness(AgentPaths? paths = null, BridgeInstaller? bridgeInstaller = null)
    {
        _paths = paths ?? new AgentPaths();
        _bridgeInstaller = bridgeInstaller;
    }

    /// <summary>Injectable probe for workstation lock status (for unit tests).</summary>
    public Func<bool>? IsLockedProbe { get; set; }

    /// <summary>Injectable probe for RDP session status (for unit tests).</summary>
    public Func<(bool IsRemote, int State, int Protocol)>? RdpProbe { get; set; }

    /// <summary>Injectable runner for powercfg (for unit tests).</summary>
    public Func<string>? PowerCfgRunner { get; set; }

    /// <summary>Injectable provider for installed Revit versions (for unit tests).</summary>
    public Func<IReadOnlyList<RevitInstallation>>? InstallationsProvider { get; set; }

    /// <summary>Injectable provider for Bridge installer list (for unit tests).</summary>
    public Func<IReadOnlyList<object>>? BridgeListProvider { get; set; }

    public MachineReadinessReport Check(MachineCheckOptions? options = null)
    {
        return CheckAsync(options, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<MachineReadinessReport> CheckAsync(MachineCheckOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new MachineCheckOptions();
        var checkedAt = DateTimeOffset.Now;
        var items = new List<MachineCheckItem>
        {
            RunIsolated("session", "Interactive desktop session", () => CheckSession()),
            RunIsolated("locked", "Workstation lock state", () => CheckLocked()),
            RunIsolated("rdp", "Console vs RDP session", () => CheckRdp()),
            RunIsolated("screensaver_lock", "Screen saver & inactivity lock", () => CheckScreensaverLock()),
            RunIsolated("sleep", "Sleep & hibernate on AC power", () => CheckSleep()),
            RunIsolated("pending_reboot", "Pending system reboot", () => CheckPendingReboot()),
            RunIsolated("elevation", "Process integrity elevation", () => CheckElevation()),
            RunIsolated("revit", $"Revit {options.Year} installation", () => CheckRevit(options.Year)),
            RunIsolated("bridge", $"Bridge add-in for Revit {options.Year}", () => CheckBridge(options.Year)),
            RunIsolated("addin_allowlist", "Add-in allowlist configuration", () => CheckAddinAllowlist(options)),
            RunIsolated("disk", "Run data storage space and permissions", () => CheckDisk(options)),
            RunIsolated("telegram", "Telegram notification configuration", () => CheckTelegram(options)),
            await RunIsolatedAsync("host", "RevitAgent Host pipe availability", () => CheckHostAsync(options, cancellationToken)).ConfigureAwait(false)
        };

        bool ready = items.All(i => !string.Equals(i.Status, MachineCheckStatus.Fail, StringComparison.OrdinalIgnoreCase));

        return new MachineReadinessReport
        {
            ReadyForUnattended = ready,
            CheckedAt = checkedAt,
            Year = options.Year,
            Items = items
        };
    }

    private static MachineCheckItem RunIsolated(string id, string defaultTitle, Func<MachineCheckItem> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            return new MachineCheckItem
            {
                Id = id,
                Title = defaultTitle,
                Status = MachineCheckStatus.Warn,
                Detail = $"Check aborted by an exception ({ex.GetType().Name}): {ex.Message}",
                Fix = "Re-check the system configuration or the process permissions."
            };
        }
    }

    private static async Task<MachineCheckItem> RunIsolatedAsync(string id, string defaultTitle, Func<Task<MachineCheckItem>> check)
    {
        try
        {
            return await check().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new MachineCheckItem
            {
                Id = id,
                Title = defaultTitle,
                Status = MachineCheckStatus.Warn,
                Detail = $"Check aborted by an exception ({ex.GetType().Name}): {ex.Message}",
                Fix = "Re-check the system configuration or the process permissions."
            };
        }
    }

    // [session] interactive desktop session (not session 0 / service)
    private static MachineCheckItem CheckSession()
    {
        bool interactive = Environment.UserInteractive;
        int sessionId = Process.GetCurrentProcess().SessionId;

        if (!interactive || sessionId == 0)
        {
            return new MachineCheckItem
            {
                Id = "session",
                Title = "Interactive desktop session",
                Status = MachineCheckStatus.Fail,
                Detail = $"The process runs in a non-interactive session (UserInteractive={interactive}, SessionId={sessionId}).",
                Fix = "Run RevitAgent in an interactive logon session (not as a Windows service or in Session 0)."
            };
        }

        return new MachineCheckItem
        {
            Id = "session",
            Title = "Interactive desktop session",
            Status = MachineCheckStatus.Pass,
            Detail = $"Interactive session is valid (SessionId={sessionId}).",
            Fix = null
        };
    }

    // [locked] workstation not locked
    private MachineCheckItem CheckLocked()
    {
        bool isLocked = IsLockedProbe?.Invoke() ?? ProbeLockedNative();

        if (isLocked)
        {
            return new MachineCheckItem
            {
                Id = "locked",
                Title = "Workstation lock state",
                Status = MachineCheckStatus.Fail,
                Detail = "The workstation is locked. UI automation cannot interact with applications.",
                Fix = "Unlock the Windows session so UI automation can interact."
            };
        }

        return new MachineCheckItem
        {
            Id = "locked",
            Title = "Workstation lock state",
            Status = MachineCheckStatus.Pass,
            Detail = "The workstation is unlocked and ready for UI interaction.",
            Fix = null
        };
    }

    private static bool ProbeLockedNative()
    {
        var hDesk = MachineNative.OpenInputDesktop(0, false, MachineNative.DESKTOP_SWITCHDESKTOP);
        if (hDesk == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            return !MachineNative.SwitchDesktop(hDesk);
        }
        finally
        {
            MachineNative.CloseDesktop(hDesk);
        }
    }

    // [rdp] console session vs RDP; a disconnected RDP session = fail
    private MachineCheckItem CheckRdp()
    {
        var (isRemote, state, proto) = RdpProbe?.Invoke() ?? ProbeRdpNative();

        if (state == MachineNative.WTSDisconnected)
        {
            return new MachineCheckItem
            {
                Id = "rdp",
                Title = "Console vs RDP session",
                Status = MachineCheckStatus.Fail,
                Detail = "The Remote Desktop (RDP) session is disconnected (state=Disconnected). The screen is locked for UI automation.",
                Fix = @"Use scripts\disconnect-keep-console.bat to disconnect RDP while keeping the console session alive."
            };
        }

        if (isRemote || proto == 2)
        {
            return new MachineCheckItem
            {
                Id = "rdp",
                Title = "Console vs RDP session",
                Status = MachineCheckStatus.Warn,
                Detail = "Running through a Remote Desktop (RDP) session. Closing the RDP window with the X button ends the interactive desktop session.",
                Fix = @"Before leaving RDP, run scripts\disconnect-keep-console.bat from a Command Prompt to hand the session back to the console."
            };
        }

        return new MachineCheckItem
        {
            Id = "rdp",
            Title = "Console vs RDP session",
            Status = MachineCheckStatus.Pass,
            Detail = "Running on the local console session.",
            Fix = null
        };
    }

    private static (bool IsRemote, int State, int Protocol) ProbeRdpNative()
    {
        bool isRemote = MachineNative.GetSystemMetrics(MachineNative.SM_REMOTESESSION) != 0;

        int state = -1;
        if (MachineNative.WTSQuerySessionInformation(MachineNative.WTS_CURRENT_SERVER_HANDLE, MachineNative.WTS_CURRENT_SESSION, MachineNative.WTSConnectState, out var pState, out _))
        {
            try { state = Marshal.ReadInt32(pState); }
            finally { MachineNative.WTSFreeMemory(pState); }
        }

        int proto = -1;
        if (MachineNative.WTSQuerySessionInformation(MachineNative.WTS_CURRENT_SERVER_HANDLE, MachineNative.WTS_CURRENT_SESSION, MachineNative.WTSClientProtocolType, out var pProto, out _))
        {
            try { proto = Marshal.ReadInt16(pProto); }
            finally { MachineNative.WTSFreeMemory(pProto); }
        }

        return (isRemote, state, proto);
    }

    // [screensaver_lock] ScreenSaveActive/ScreenSaverIsSecure/ScreenSaveTimeOut and InactivityTimeoutSecs
    private static MachineCheckItem CheckScreensaverLock()
    {
        // 1. Check machine policy InactivityTimeoutSecs
        int inactivityTimeout = 0;
        using (var policyKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
        {
            if (policyKey?.GetValue("InactivityTimeoutSecs") is object obj)
            {
                inactivityTimeout = ParseInt(obj, 0);
            }
        }

        if (inactivityTimeout > 0)
        {
            return new MachineCheckItem
            {
                Id = "screensaver_lock",
                Title = "Screen saver & inactivity lock",
                Status = MachineCheckStatus.Fail,
                Detail = $"A system policy (InactivityTimeoutSecs) locks the machine after {inactivityTimeout} seconds of inactivity.",
                Fix = "Turn off the inactivity auto-lock policy (InactivityTimeoutSecs) in Group Policy / Registry."
            };
        }

        // 2. Check HKCU Control Panel\Desktop
        bool screenSaveActive = false;
        bool screenSaveIsSecure = false;
        int screenSaveTimeout = 600;

        using (var policyDesk = Registry.CurrentUser.OpenSubKey(@"Software\Policies\Microsoft\Windows\Control Panel\Desktop"))
        {
            if (policyDesk is not null)
            {
                if (policyDesk.GetValue("ScreenSaveActive") is object a) screenSaveActive = IsTrue(a);
                if (policyDesk.GetValue("ScreenSaverIsSecure") is object s) screenSaveIsSecure = IsTrue(s);
                if (policyDesk.GetValue("ScreenSaveTimeOut") is object t) screenSaveTimeout = ParseInt(t, 600);
            }
        }

        using (var userDesk = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            if (userDesk is not null)
            {
                if (userDesk.GetValue("ScreenSaveActive") is object a && !screenSaveActive) screenSaveActive = IsTrue(a);
                if (userDesk.GetValue("ScreenSaverIsSecure") is object s && !screenSaveIsSecure) screenSaveIsSecure = IsTrue(s);
                if (userDesk.GetValue("ScreenSaveTimeOut") is object t && screenSaveTimeout == 600) screenSaveTimeout = ParseInt(t, 600);
            }
        }

        if (screenSaveActive && screenSaveIsSecure)
        {
            return new MachineCheckItem
            {
                Id = "screensaver_lock",
                Title = "Screen saver & inactivity lock",
                Status = MachineCheckStatus.Fail,
                Detail = $"The screen saver is enabled and locks the machine after {screenSaveTimeout} seconds (ScreenSaverIsSecure=1).",
                Fix = "Turn off the lock: Settings -> Personalization -> Lock screen -> Screen saver: None (or clear 'On resume, display logon screen')."
            };
        }

        if (screenSaveActive)
        {
            return new MachineCheckItem
            {
                Id = "screensaver_lock",
                Title = "Screen saver & inactivity lock",
                Status = MachineCheckStatus.Warn,
                Detail = $"The screen saver starts after {screenSaveTimeout} seconds but does not require a password.",
                Fix = "Recommended: turn the screen saver off: Settings -> Personalization -> Lock screen -> Screen saver: None."
            };
        }

        return new MachineCheckItem
        {
            Id = "screensaver_lock",
            Title = "Screen saver & inactivity lock",
            Status = MachineCheckStatus.Pass,
            Detail = "The screen does not auto-lock (screen saver and InactivityTimeout are both off).",
            Fix = null
        };
    }

    // [sleep] AC standby/hibernate timeouts
    private MachineCheckItem CheckSleep()
    {
        string output;
        try
        {
            output = PowerCfgRunner?.Invoke() ?? RunPowerCfg();
        }
        catch (Exception ex)
        {
            return new MachineCheckItem
            {
                Id = "sleep",
                Title = "Sleep & hibernate on AC power",
                Status = MachineCheckStatus.Warn,
                Detail = $"Could not read the power configuration from powercfg ({ex.GetType().Name}): {ex.Message}",
                Fix = "Check that the powercfg command can be executed on this machine."
            };
        }

        var (sleepSec, hibSec) = PowerCfgParser.ParseSleepTimeouts(output);
        if (sleepSec > 0 || hibSec > 0)
        {
            return new MachineCheckItem
            {
                Id = "sleep",
                Title = "Sleep & hibernate on AC power",
                Status = MachineCheckStatus.Fail,
                Detail = $"The computer is configured to sleep on AC power (Sleep: {sleepSec}s, Hibernate: {hibSec}s).",
                Fix = "Turn off sleep while plugged in: Settings -> System -> Power & sleep -> When plugged in, PC goes to sleep after: Never (or run: powercfg /change standby-timeout-ac 0 && powercfg /change hibernate-timeout-ac 0)."
            };
        }

        return new MachineCheckItem
        {
            Id = "sleep",
            Title = "Sleep & hibernate on AC power",
            Status = MachineCheckStatus.Pass,
            Detail = "Sleep on AC power is off (Sleep: Never, Hibernate: Never).",
            Fix = null
        };
    }

    private static string RunPowerCfg()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = "/query SCHEME_CURRENT SUB_SLEEP",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch powercfg.");
        var text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);
        return text;
    }

    // [pending_reboot] registry RebootRequired / PendingFileRenameOperations
    private static MachineCheckItem CheckPendingReboot()
    {
        string? indicator = null;

        using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            if (cbs is not null) indicator = "Component Based Servicing RebootPending";
        }

        if (indicator is null)
        {
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null) indicator = "WindowsUpdate RebootRequired";
        }

        if (indicator is null)
        {
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            var pfro = sm?.GetValue("PendingFileRenameOperations");
            if (pfro is string[] arr && arr.Length > 0 && arr.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                indicator = "PendingFileRenameOperations";
            }
            else if (pfro is string str && !string.IsNullOrWhiteSpace(str))
            {
                indicator = "PendingFileRenameOperations";
            }
        }

        if (indicator is null)
        {
            using var pkg = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending");
            if (pkg is not null) indicator = "PackagesPending";
        }

        if (indicator is not null)
        {
            return new MachineCheckItem
            {
                Id = "pending_reboot",
                Title = "Pending system reboot",
                Status = MachineCheckStatus.Warn,
                Detail = $"The system is waiting for a restart to finish updates ({indicator}).",
                Fix = "Restart the computer before starting an overnight session."
            };
        }

        return new MachineCheckItem
        {
            Id = "pending_reboot",
            Title = "Pending system reboot",
            Status = MachineCheckStatus.Pass,
            Detail = "No operating-system update is waiting for a restart.",
            Fix = null
        };
    }

    // [elevation] gateway process elevated? (warn: Revit must run at the same integrity level)
    private static MachineCheckItem CheckElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        bool isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

        if (isElevated)
        {
            return new MachineCheckItem
            {
                Id = "elevation",
                Title = "Process integrity elevation",
                Status = MachineCheckStatus.Warn,
                Detail = "The gateway process runs as Administrator (elevated). Revit must run at the same integrity level.",
                Fix = "Start RevitAgent and Revit at the same level (recommended: a normal user, not Run as Administrator)."
            };
        }

        return new MachineCheckItem
        {
            Id = "elevation",
            Title = "Process integrity elevation",
            Status = MachineCheckStatus.Pass,
            Detail = "The gateway process runs as a standard user.",
            Fix = null
        };
    }

    // [revit] Revit <year> installed
    private MachineCheckItem CheckRevit(int year)
    {
        var installations = InstallationsProvider?.Invoke() ?? RevitInstallations.Discover();
        var match = installations.FirstOrDefault(i => i.Year == year);

        if (match is null)
        {
            return new MachineCheckItem
            {
                Id = "revit",
                Title = $"Revit {year} installation",
                Status = MachineCheckStatus.Fail,
                Detail = $"Autodesk Revit {year} is not installed on this machine.",
                Fix = $"Install Autodesk Revit {year}."
            };
        }

        return new MachineCheckItem
        {
            Id = "revit",
            Title = $"Revit {year} installation",
            Status = MachineCheckStatus.Pass,
            Detail = $"Autodesk Revit {year} is installed ({match.InstallDir}).",
            Fix = null
        };
    }

    // [bridge] Bridge installed + trusted for <year>
    private MachineCheckItem CheckBridge(int year)
    {
        IReadOnlyList<object> list;
        if (BridgeListProvider is not null)
        {
            list = BridgeListProvider();
        }
        else
        {
            var installer = _bridgeInstaller ?? new BridgeInstaller(BridgeInstallEnvironment.ForCurrentUser(_paths, new SystemRevitProcessProbe()));
            list = installer.List();
        }

        Dictionary<string, object?>? targetDict = null;
        foreach (var item in list)
        {
            if (item is Dictionary<string, object?> d && d.TryGetValue("year", out var yObj) && Convert.ToInt32(yObj) == year)
            {
                targetDict = d;
                break;
            }
        }

        if (targetDict is null)
        {
            return new MachineCheckItem
            {
                Id = "bridge",
                Title = $"Bridge add-in for Revit {year}",
                Status = MachineCheckStatus.Fail,
                Detail = $"No Bridge install information for Revit {year} (Revit is not installed or was not detected).",
                Fix = $"Install Autodesk Revit {year}, then install the Bridge add-in with the bridge_install tool."
            };
        }

        bool installed = targetDict.TryGetValue("installed", out var instObj) && instObj is true;
        bool trusted = targetDict.TryGetValue("trusted", out var trustObj) && trustObj is true;

        if (!installed)
        {
            return new MachineCheckItem
            {
                Id = "bridge",
                Title = $"Bridge add-in for Revit {year}",
                Status = MachineCheckStatus.Fail,
                Detail = $"The Bridge add-in for Revit {year} is not installed.",
                Fix = $"Install the Bridge for Revit {year} with the bridge_install tool."
            };
        }

        if (!trusted)
        {
            return new MachineCheckItem
            {
                Id = "bridge",
                Title = $"Bridge add-in for Revit {year}",
                Status = MachineCheckStatus.Fail,
                Detail = $"The Bridge add-in for Revit {year} is installed but not trusted (Revit CodeSigning registry).",
                Fix = $"Register trust for the Bridge add-in with the bridge_install tool."
            };
        }

        return new MachineCheckItem
        {
            Id = "bridge",
            Title = $"Bridge add-in for Revit {year}",
            Status = MachineCheckStatus.Pass,
            Detail = $"The Bridge add-in for Revit {year} is installed and trusted.",
            Fix = null
        };
    }

    // [addin_allowlist] %REVITAGENT_HOME%\config\addin-allowlist.json exists with >= 1 enabled entry
    private MachineCheckItem CheckAddinAllowlist(MachineCheckOptions options)
    {
        var configDir = options.ConfigDir ?? _paths.ConfigDir;
        var filePath = Path.Combine(configDir, "addin-allowlist.json");

        if (!File.Exists(filePath))
        {
            return new MachineCheckItem
            {
                Id = "addin_allowlist",
                Title = "Add-in allowlist configuration",
                Status = MachineCheckStatus.Warn,
                Detail = $"There is no add-in allowlist file yet ({filePath}).",
                Fix = "Add and enable at least one add-in in the allowlist on the Host dashboard ('Allowed add-ins' tab)."
            };
        }

        int enabledCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("enabled", out var en))
                    {
                        if (en.ValueKind == JsonValueKind.True)
                        {
                            enabledCount++;
                        }
                        else if (en.ValueKind == JsonValueKind.String && bool.TryParse(en.GetString(), out var b) && b)
                        {
                            enabledCount++;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new MachineCheckItem
            {
                Id = "addin_allowlist",
                Title = "Add-in allowlist configuration",
                Status = MachineCheckStatus.Warn,
                Detail = $"The add-in allowlist file ({filePath}) is not valid JSON: {ex.Message}",
                Fix = "Check or recreate the allowlist file on the Host dashboard."
            };
        }

        if (enabledCount >= 1)
        {
            return new MachineCheckItem
            {
                Id = "addin_allowlist",
                Title = "Add-in allowlist configuration",
                Status = MachineCheckStatus.Pass,
                Detail = $"The add-in allowlist has {enabledCount} enabled entries ({filePath}).",
                Fix = null
            };
        }

        return new MachineCheckItem
        {
            Id = "addin_allowlist",
            Title = "Add-in allowlist configuration",
            Status = MachineCheckStatus.Warn,
            Detail = $"The add-in allowlist file ({filePath}) has no enabled entry (enabled: true).",
            Fix = "Enable at least one add-in in the allowlist on the Host dashboard."
        };
    }

    // [disk] run-data root writable and free space: warn < 20 GB, fail < 5 GB
    private MachineCheckItem CheckDisk(MachineCheckOptions options)
    {
        var runsDir = options.RunsDir ?? _paths.RunsDir;
        try
        {
            Directory.CreateDirectory(runsDir);
            var testFile = Path.Combine(runsDir, $".probe_write_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "probe");
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            return new MachineCheckItem
            {
                Id = "disk",
                Title = "Run data storage space and permissions",
                Status = MachineCheckStatus.Fail,
                Detail = $"The run-data folder ({runsDir}) is not writable: {ex.Message}",
                Fix = $"Grant the user write permission on {runsDir}."
            };
        }

        string root = Path.GetPathRoot(Path.GetFullPath(runsDir))!;
        var drive = new DriveInfo(root);
        double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);

        if (freeGb < 5.0)
        {
            return new MachineCheckItem
            {
                Id = "disk",
                Title = "Run data storage space and permissions",
                Status = MachineCheckStatus.Fail,
                Detail = $"Free disk space at {runsDir} is too low: {freeGb:F1} GB (minimum 5 GB).",
                Fix = $"Free up space on drive {drive.Name} to have at least 5 GB (recommended > 20 GB)."
            };
        }

        if (freeGb < 20.0)
        {
            return new MachineCheckItem
            {
                Id = "disk",
                Title = "Run data storage space and permissions",
                Status = MachineCheckStatus.Warn,
                Detail = $"Free disk space at {runsDir} is limited: {freeGb:F1} GB (< 20 GB recommended).",
                Fix = $"Free up space on drive {drive.Name} to have at least 20 GB for run data."
            };
        }

        return new MachineCheckItem
        {
            Id = "disk",
            Title = "Run data storage space and permissions",
            Status = MachineCheckStatus.Pass,
            Detail = $"The run-data folder {runsDir} is writable; free space: {freeGb:F1} GB.",
            Fix = null
        };
    }

    // [telegram] %REVITAGENT_HOME%\config\telegram.json present
    private MachineCheckItem CheckTelegram(MachineCheckOptions options)
    {
        var configDir = options.ConfigDir ?? _paths.ConfigDir;
        var filePath = Path.Combine(configDir, "telegram.json");

        if (File.Exists(filePath))
        {
            return new MachineCheckItem
            {
                Id = "telegram",
                Title = "Telegram notification configuration",
                Status = MachineCheckStatus.Pass,
                Detail = $"Telegram notifications are configured ({filePath}).",
                Fix = null
            };
        }

        return new MachineCheckItem
        {
            Id = "telegram",
            Title = "Telegram notification configuration",
            Status = MachineCheckStatus.Warn,
            Detail = $"There is no Telegram configuration file yet ({filePath}). Incident notifications will not be sent through Telegram.",
            Fix = "Set the Telegram bot token and chat ID on the Host dashboard to receive overnight notifications."
        };
    }

    // [host] Host pipe \\.\pipe\revitagent-mcp reachable
    private static async Task<MachineCheckItem> CheckHostAsync(MachineCheckOptions options, CancellationToken cancellationToken)
    {
        var pipeName = options.PipeName ?? GatewayPipeListener.DefaultPipeName;
        bool reachable = false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using (client.ConfigureAwait(false))
            {
                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                reachable = true;
            }
        }
        catch
        {
            reachable = false;
        }

        if (reachable)
        {
            return new MachineCheckItem
            {
                Id = "host",
                Title = "RevitAgent Host pipe availability",
                Status = MachineCheckStatus.Pass,
                Detail = $@"The RevitAgent Host pipe (\\.\pipe\{pipeName}) is accepting connections.",
                Fix = null
            };
        }

        return new MachineCheckItem
        {
            Id = "host",
            Title = "RevitAgent Host pipe availability",
            Status = MachineCheckStatus.Warn,
            Detail = $@"The RevitAgent Host pipe (\\.\pipe\{pipeName}) is not responding (the Host is not running).",
            Fix = "Start the RevitAgent Host (dashboard) first if you want to watch through the visual interface."
        };
    }

    private static bool IsTrue(object? val) =>
        val switch
        {
            int i => i == 1,
            string s => s.Trim() == "1" || string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static int ParseInt(object? val, int defaultVal) =>
        val switch
        {
            int i => i,
            string s when int.TryParse(s.Trim(), out var parsed) => parsed,
            _ => defaultVal
        };
}
