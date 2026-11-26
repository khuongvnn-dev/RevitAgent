namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Contracts.Protocol;
    using RevitAgent.Core;
    using RevitAgent.Core.Audit;
    using RevitAgent.Core.Install;
    using RevitAgent.Core.Policy;
    using RevitAgent.Core.Mcp;
    using RevitAgent.Core.Revit;
    using RevitAgent.Core.Tools;
    using RevitAgent.Host.Automation;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;

    /// <summary>
    /// Dashboard of the v2 gateway: running Revit instances, installed versions + Bridge, and how to connect an AI client.
    /// It only ever LISTS processes by itself; anything that touches a Revit (capture, close, launch, dialogs, Bridge)
    /// runs when the user presses a button, and goes through the gateway ToolRegistry (policy + audit, session "dashboard").
    /// </summary>
    public partial class DashboardViewModel : ObservableObject
    {
        public const string SessionId = "dashboard";
        public const string BridgeInstallTool = "bridge_install";
        public const string BridgeUninstallTool = "bridge_uninstall";

        private const int InstalledRefreshEveryNthTick = 30;

        private readonly ToolRegistry _tools;
        private readonly RevitProcessService _processes;
        private readonly GatewayPipeListener _listener;
        private readonly AgentPaths _paths;
        private readonly IEncryptedConfigService _configService;
        private readonly IHostIpcService _ipcService;
        private readonly IAuditLogService _auditLogService;
        private readonly ILocalizationService _localizationService;
        private readonly INotificationService? _notificationService;
        private readonly BridgeInstaller? _bridgeInstaller;
        private readonly AuditLog? _gatewayAudit;

        private readonly Func<IReadOnlyList<RevitInstance>> _listInstances;
        private readonly Func<int, bool> _uacParity;
        private readonly Func<IntPtr, byte[]?> _capture;
        private readonly Action<string> _setClipboard;
        private readonly string? _baseDirectory;

        private int _refreshing;
        private int _pollCount;

        /// <summary>Bridge manifest paths of the installs from the last revit_list_installed call, and which of them existed then.</summary>
        private volatile string[] _manifestPaths = Array.Empty<string>();
        private volatile string _manifestFingerprint = string.Empty;

        [ObservableProperty]
        private ObservableCollection<RevitInstanceItem> _revitInstances = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CaptureScreenshotCommand))]
        [NotifyCanExecuteChangedFor(nameof(ListDialogsCommand))]
        [NotifyCanExecuteChangedFor(nameof(CloseRevitCommand))]
        private RevitInstanceItem? _selectedRevitInstance;

        [ObservableProperty]
        private ObservableCollection<InstalledRevitItem> _installedVersions = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LaunchRevitCommand))]
        [NotifyCanExecuteChangedFor(nameof(InstallBridgeCommand))]
        [NotifyCanExecuteChangedFor(nameof(UninstallBridgeCommand))]
        private InstalledRevitItem? _selectedInstalled;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LaunchRevitCommand))]
        private bool _isLaunching;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InstallBridgeCommand))]
        [NotifyCanExecuteChangedFor(nameof(UninstallBridgeCommand))]
        private bool _isBridgeBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InstallBridgeCommand))]
        [NotifyCanExecuteChangedFor(nameof(UninstallBridgeCommand))]
        private bool _areBridgeToolsAvailable;

        [ObservableProperty]
        private string _bridgeInstallTooltip = string.Empty;

        [ObservableProperty]
        private string _bridgeUninstallTooltip = string.Empty;

        [ObservableProperty]
        private string _closeRevitTooltip = string.Empty;

        [ObservableProperty]
        private string _revitCountText = string.Empty;

        [ObservableProperty]
        private bool _hasDialogResults;

        [ObservableProperty]
        private ObservableCollection<string> _dialogResults = new();

        [ObservableProperty]
        private string _pipeText = string.Empty;

        [ObservableProperty]
        private int _activeSessions;

        [ObservableProperty]
        private string? _shimPath;

        [ObservableProperty]
        private string _shimText = string.Empty;

        [ObservableProperty]
        private bool _isShimAvailable;

        [ObservableProperty]
        private string _claudeCodeCommand = string.Empty;

        [ObservableProperty]
        private string _claudeDesktopJson = string.Empty;

        [ObservableProperty]
        private ImageSource? _screenshotPreview;

        [ObservableProperty]
        private bool _hasScreenshot;

        [ObservableProperty]
        private string _serverToggleText = "Start Server";

        [ObservableProperty]
        private bool _isServerRunning;

        /// <summary>"Let AI act on my Revit": time-limited action grants (Host memory only).</summary>
        public ActionGrantViewModel Grants { get; }

        /// <summary>Owner approval requests (agents asking to install / uninstall the Bridge), null when not wired.</summary>
        public OwnerApprovalViewModel? Approvals { get; }

        public event Action<string>? StatusMessage;

        public DashboardViewModel(
            ToolRegistry tools,
            RevitProcessService processes,
            GatewayPipeListener listener,
            AgentPaths paths,
            IEncryptedConfigService configService,
            IHostIpcService ipcService,
            IAuditLogService auditLogService,
            ILocalizationService? localizationService = null,
            INotificationService? notificationService = null,
            Func<IReadOnlyList<RevitInstance>>? listInstances = null,
            Func<int, bool>? uacParity = null,
            Func<IntPtr, byte[]?>? capture = null,
            Action<string>? setClipboard = null,
            string? baseDirectory = null,
            RevitAgent.Core.Policy.ActionGrantStore? grants = null,
            BridgeInstaller? bridgeInstaller = null,
            AuditLog? gatewayAudit = null,
            DashboardApprovalChannel? approvalChannel = null)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _processes = processes ?? throw new ArgumentNullException(nameof(processes));
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _ipcService = ipcService ?? throw new ArgumentNullException(nameof(ipcService));
            _auditLogService = auditLogService ?? throw new ArgumentNullException(nameof(auditLogService));
            _localizationService = localizationService ?? new LocalizationService();
            _notificationService = notificationService;
            _baseDirectory = baseDirectory;
            _bridgeInstaller = bridgeInstaller;
            _gatewayAudit = gatewayAudit;
            if (approvalChannel is not null)
            {
                Approvals = new OwnerApprovalViewModel(approvalChannel, _localizationService, notificationService);
            }

            Grants = new ActionGrantViewModel(grants ?? new RevitAgent.Core.Policy.ActionGrantStore(), _localizationService);

            _listInstances = listInstances ?? (() => _processes.ListInstances());
            _uacParity = uacParity ?? DefaultUacParity;
            _capture = capture ?? (hwnd => RevitAgent.Core.Ui.WindowCapture.CapturePng(hwnd));
            _setClipboard = setClipboard ?? DefaultSetClipboard;

            _ipcService.ServerStateChanged += OnServerStateChanged;
            _localizationService.LanguageChanged += _ => UpdateLocalizedTexts();

            IsServerRunning = _ipcService.IsRunning;
            AreBridgeToolsAvailable = _tools.TryGet(BridgeInstallTool, out _) && _tools.TryGet(BridgeUninstallTool, out _);
            ResolveShim();
            UpdateLocalizedTexts();
        }

        /// <summary>The UI-thread-safe snapshot used by the header UAC badge: null when no Revit is selected.</summary>
        public bool? SelectedUacMatched => SelectedRevitInstance?.UacMatched;

        public string PipeFullName => $@"\\.\pipe\{_listener.PipeName ?? _ipcService.ActivePipeName}";

        // ---------------------------------------------------------------- localisation

        private string L(string key) => _localizationService.GetString(key);

        private string L(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, _localizationService.GetString(key), args);

        private void UpdateLocalizedTexts()
        {
            ServerToggleText = IsServerRunning ? L("Str_BtnStopServer") : L("Str_BtnStartServer");
            BridgeInstallTooltip = AreBridgeToolsAvailable ? L("Str_Tooltip_BtnInstallBridge") : L("Str_Tooltip_BridgeUnavailable");
            BridgeUninstallTooltip = AreBridgeToolsAvailable ? L("Str_Tooltip_BtnUninstallBridge") : L("Str_Tooltip_BridgeUnavailable");
            CloseRevitTooltip = SelectedRevitInstance is { LaunchedByGateway: false }
                ? L("Str_Tooltip_BtnCloseRevitForeign")
                : L("Str_Tooltip_BtnCloseRevit");
            RevitCountText = L("Str_RevitCountFmt", RevitInstances.Count);
            ShimText = ShimPath ?? L("Str_ValShimNotFound");
        }

        private void OnServerStateChanged(bool running)
        {
            IsServerRunning = running;
            ServerToggleText = running ? L("Str_BtnStopServer") : L("Str_BtnStartServer");
            OnPropertyChanged(nameof(PipeFullName));
        }

        // ---------------------------------------------------------------- connect an AI client

        private void ResolveShim()
        {
            ShimPath = ShimLocator.Resolve(_baseDirectory);
            IsShimAvailable = ShimPath is not null;

            // Without a built Shim still give a usable template (the user adjusts the path).
            var shown = ShimPath ?? ShimLocator.DevLayoutCandidate(_baseDirectory ?? AppContext.BaseDirectory)
                ?? Path.Combine(_baseDirectory ?? AppContext.BaseDirectory, ShimLocator.ShimFileName);
            ClaudeCodeCommand = ShimLocator.ClaudeCodeCommand(shown);
            ClaudeDesktopJson = ShimLocator.McpServersJson(shown);
        }

        [RelayCommand]
        public void CopyClaudeCode() => CopyToClipboard(ClaudeCodeCommand);

        [RelayCommand]
        public void CopyClaudeJson() => CopyToClipboard(ClaudeDesktopJson);

        private void CopyToClipboard(string text)
        {
            try
            {
                _setClipboard(text);
                Notify(NotificationKind.Success, L("Str_TitleConnectAi"), IsShimAvailable ? L("Str_MsgCopied") : L("Str_MsgCopiedNoShim"));
                StatusMessage?.Invoke(L("Str_MsgCopied"));
            }
            catch (Exception ex)
            {
                Notify(NotificationKind.Error, L("Str_TitleConnectAi"), ex.Message);
            }
        }

        private static void DefaultSetClipboard(string text) => Clipboard.SetText(text);

        // ---------------------------------------------------------------- polling (never blocks the UI thread)

        /// <summary>
        /// Called by the shell timer (1 s tick): every 3rd tick re-lists Revit; every 30th checks the Bridge manifests of the known
        /// installs and re-reads the installed versions (an audited revit_list_installed call) only when one appeared or disappeared.
        /// </summary>
        public void OnPollTick()
        {
            _pollCount++;
            Grants.Tick();
            if (_pollCount % 3 == 0)
            {
                _ = RefreshAsync();
            }

            if (_pollCount % InstalledRefreshEveryNthTick == 0)
            {
                _ = RefreshInstalledIfChangedAsync();
            }
        }

        private async Task RefreshInstalledIfChangedAsync()
        {
            var paths = _manifestPaths;
            if (paths.Length == 0)
            {
                return; // nothing installed was found at start; a newly installed Revit shows after a dashboard restart
            }

            var now = await Task.Run(() => ManifestFingerprint(paths)).ConfigureAwait(true);
            if (!string.Equals(now, _manifestFingerprint, StringComparison.Ordinal))
            {
                await RefreshInstalledAsync().ConfigureAwait(true);
            }
        }

        private static string ManifestFingerprint(IEnumerable<string> manifests) =>
            string.Concat(manifests.Select(m => File.Exists(m) ? '1' : '0'));

        public async Task InitializeAsync()
        {
            await RefreshInstalledAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }

        /// <summary>Lists Revit processes on a background thread (read-only, no message is sent to Revit) and applies the result.</summary>
        public async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) == 1)
            {
                return;
            }

            try
            {
                var snapshot = await Task.Run(() =>
                {
                    var list = _listInstances();
                    return list.Select(i => RevitInstanceItem.From(i, SafeParity(i.Pid))).OrderBy(i => i.Pid).ToList();
                }).ConfigureAwait(true);

                ApplyInstances(snapshot);
                ActiveSessions = _listener.ActiveSessions;
                OnPropertyChanged(nameof(PipeFullName));
                UpdateLocalizedTexts();

            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("WARN", "Revit List Failed", ex.Message);
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }

        private bool SafeParity(int pid)
        {
            try
            {
                return _uacParity(pid);
            }
            catch
            {
                return true;
            }
        }

        private static bool DefaultUacParity(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return DesktopPreflight.CheckElevationParity(process);
            }
            catch
            {
                return true;
            }
        }

        private void ApplyInstances(IReadOnlyList<RevitInstanceItem> snapshot)
        {
            Grants.Update(snapshot);
            var current = string.Join(";", RevitInstances.Select(i => i.Signature));
            var next = string.Join(";", snapshot.Select(i => i.Signature));
            if (current == next)
            {
                return;
            }

            int? previousPid = SelectedRevitInstance?.Pid;
            RevitInstances = new ObservableCollection<RevitInstanceItem>(snapshot);
            SelectedRevitInstance = previousPid is int pid ? snapshot.FirstOrDefault(i => i.Pid == pid) : null;
            OnPropertyChanged(nameof(SelectedUacMatched));
        }

        partial void OnSelectedRevitInstanceChanged(RevitInstanceItem? value)
        {
            OnPropertyChanged(nameof(SelectedUacMatched));
            CloseRevitTooltip = value is { LaunchedByGateway: false } ? L("Str_Tooltip_BtnCloseRevitForeign") : L("Str_Tooltip_BtnCloseRevit");
        }

        partial void OnAreBridgeToolsAvailableChanged(bool value) => UpdateLocalizedTexts();

        // ---------------------------------------------------------------- installed versions + Bridge

        /// <summary>Reads installed Revit versions through the revit_list_installed tool (off the UI thread).</summary>
        public async Task RefreshInstalledAsync()
        {
            AreBridgeToolsAvailable = _tools.TryGet(BridgeInstallTool, out _) && _tools.TryGet(BridgeUninstallTool, out _);

            if (!_tools.TryGet("revit_list_installed", out _))
            {
                return;
            }

            try
            {
                var result = await Task.Run(() => CallToolAsync("revit_list_installed", new { })).ConfigureAwait(true);
                if (result.IsError || result.Structured is not { } json || !json.TryGetProperty("installs", out var installs))
                {
                    return;
                }

                var items = new List<InstalledRevitItem>();
                var manifests = new List<string>();
                var fingerprint = new System.Text.StringBuilder();
                foreach (var e in installs.EnumerateArray())
                {
                    if (e.TryGetProperty("user_addins_dir", out var dir) && dir.ValueKind == JsonValueKind.String && dir.GetString() is { Length: > 0 } addins)
                    {
                        // Same check as the tool's bridge_manifest_installed, so the poll compares like with like.
                        manifests.Add(Path.Combine(addins, RevitProcessService.BridgeManifestName));
                        fingerprint.Append(e.TryGetProperty("bridge_manifest_installed", out var mi) && mi.ValueKind == JsonValueKind.True ? '1' : '0');
                    }

                    if (e.TryGetProperty("year", out var y) && y.TryGetInt32(out var year))
                    {
                        items.Add(new InstalledRevitItem
                        {
                            Year = year,
                            ExePath = e.TryGetProperty("exe_path", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                            BridgeInstalled = e.TryGetProperty("bridge_manifest_installed", out var b) && b.ValueKind == JsonValueKind.True,
                        });
                    }
                }

                _manifestFingerprint = fingerprint.ToString();
                _manifestPaths = manifests.ToArray();

                items.Sort((a, b) => b.Year.CompareTo(a.Year));
                int? selectedYear = SelectedInstalled?.Year;
                InstalledVersions = new ObservableCollection<InstalledRevitItem>(items);
                SelectedInstalled = items.FirstOrDefault(i => i.Year == selectedYear) ?? items.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("WARN", "Revit List Installed Failed", ex.Message);
            }
        }

        private bool CanBridge() => AreBridgeToolsAvailable && !IsBridgeBusy && SelectedInstalled is not null;

        [RelayCommand(CanExecute = nameof(CanBridge))]
        public Task InstallBridgeAsync() => RunBridgeAsync(BridgeInstallTool, "Str_MsgBridgeInstalled");

        [RelayCommand(CanExecute = nameof(CanBridge))]
        public Task UninstallBridgeAsync() => RunBridgeAsync(BridgeUninstallTool, "Str_MsgBridgeRemoved");

        private async Task RunBridgeAsync(string tool, string successKey)
        {
            if (!_tools.TryGet(tool, out _))
            {
                Notify(NotificationKind.Warning, L("Str_TitleBridge"), L("Str_Tooltip_BridgeUnavailable"));
                return;
            }

            if (SelectedInstalled is not { } installed)
            {
                Notify(NotificationKind.Warning, L("Str_TitleBridge"), L("Str_MsgSelectYear"));
                return;
            }

            IsBridgeBusy = true;
            try
            {
                // The owner pressing the button acts directly: no agent session, no approval prompt. The installer is
                // called here, never through the tool registry (whose bridge tools ask the owner first).
                var (failure, result) = _bridgeInstaller is { } installer
                    ? (await Task.Run(() => RunInstallerDirect(installer, tool, installed.Year)).ConfigureAwait(true), (ToolResult?)null)
                    : (null, await CallToolAsync(tool, new { years = new[] { installed.Year } }).ConfigureAwait(true));
                failure ??= result is { IsError: true } r ? Describe(r) : null;
                if (failure is not null)
                {
                    Notify(NotificationKind.Error, L("Str_TitleBridge"), L("Str_MsgToolFailed", tool, failure));
                }
                else
                {
                    Notify(NotificationKind.Success, L("Str_TitleBridge"), L(successKey, installed.Year));
                    StatusMessage?.Invoke(L(successKey, installed.Year));
                }
            }
            finally
            {
                IsBridgeBusy = false;
            }

            await RefreshInstalledAsync().ConfigureAwait(true);
        }

        /// <summary>Runs the installer for one year on the owner's behalf; returns a failure text or null. Audited as session "dashboard".</summary>
        private string? RunInstallerDirect(BridgeInstaller installer, string tool, int year)
        {
            var sw = Stopwatch.StartNew();
            string? failure = null;
            try
            {
                var years = new[] { year };
                var results = tool == BridgeUninstallTool ? installer.Uninstall(years) : installer.Install(years);
                var bad = results.OfType<Dictionary<string, object?>>().FirstOrDefault(d => d.TryGetValue("ok", out var ok) && ok is not true);
                if (bad is not null)
                {
                    failure = bad.TryGetValue("message", out var m) && m is string text && text.Length > 0 ? text : "failed";
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            _gatewayAudit?.Record(new AuditEntry(
                DateTimeOffset.Now,
                Guid.NewGuid().ToString("N")[..12],
                SessionId,
                tool,
                ToolPermission.Admin.ToString(),
                "{\"years\":[" + year.ToString(CultureInfo.InvariantCulture) + "]}",
                failure is null,
                failure is null ? null : "DEPENDENCY_FAILED",
                (long)sw.Elapsed.TotalMilliseconds,
                Note: "owner action (dashboard button)"));
            return failure;
        }

        // ---------------------------------------------------------------- launch / close / dialogs / capture

        private bool CanLaunch() => !IsLaunching && SelectedInstalled is not null;

        /// <summary>Start a NEW Revit of the selected year (revit_launch). Long running: shows a busy state.</summary>
        [RelayCommand(CanExecute = nameof(CanLaunch))]
        public async Task LaunchRevitAsync()
        {
            if (SelectedInstalled is not { } installed)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgSelectYear"));
                return;
            }

            IsLaunching = true;
            StatusMessage?.Invoke(L("Str_MsgLaunching", installed.Year));
            Notify(NotificationKind.Info, L("Str_TitleRevit"), L("Str_MsgLaunching", installed.Year));
            try
            {
                var result = await CallToolAsync("revit_launch", new { year = installed.Year }).ConfigureAwait(true);
                if (result.IsError)
                {
                    Notify(NotificationKind.Error, L("Str_TitleRevit"), L("Str_MsgToolFailed", "revit_launch", Describe(result)));
                }
                else
                {
                    var pid = GetInt(result, "pid");
                    var ready = result.Structured is { } s && s.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
                    if (ready)
                    {
                        Notify(NotificationKind.Success, L("Str_TitleRevit"), L("Str_MsgLaunched", installed.Year, pid?.ToString(CultureInfo.InvariantCulture) ?? "?"));
                    }
                    else
                    {
                        Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgLaunchNotReady", Describe(result)));
                    }
                }
            }
            finally
            {
                IsLaunching = false;
            }

            await RefreshAsync().ConfigureAwait(true);
        }

        private bool CanActOnSelection() => SelectedRevitInstance is not null;

        private bool CanClose() => SelectedRevitInstance is { LaunchedByGateway: true };

        /// <summary>Screenshot of the selected Revit's main window into the preview + captures folder.</summary>
        [RelayCommand(CanExecute = nameof(CanActOnSelection))]
        public async Task CaptureScreenshotAsync()
        {
            if (SelectedRevitInstance is not { } item)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgSelectRevit"));
                return;
            }

            if (item.MainWindow == IntPtr.Zero)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgNoMainWindow", item.Pid));
                return;
            }

            try
            {
                var hwnd = item.MainWindow;
                var path = Path.Combine(_paths.CapturesDir, $"dashboard_revit{item.Pid}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
                var png = await Task.Run(() =>
                {
                    var bytes = _capture(hwnd);
                    if (bytes is not null)
                    {
                        File.WriteAllBytes(path, bytes);
                    }

                    return bytes;
                }).ConfigureAwait(true);

                if (png is null)
                {
                    Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgCaptureFailed"));
                    return;
                }

                ScreenshotPreview = LoadPreview(png);
                HasScreenshot = true;
                _auditLogService.AddLog("CAPTURE", "Screenshot", $"Revit pid {item.Pid} → {path}");
                StatusMessage?.Invoke(L("Str_MsgCaptureSaved", path));
                Notify(NotificationKind.Success, L("Str_TitleRevit"), L("Str_MsgCaptureSaved", path));
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Screenshot Failed", ex.Message);
                Notify(NotificationKind.Error, L("Str_TitleRevit"), ex.Message);
            }
        }

        private static BitmapImage LoadPreview(byte[] png)
        {
            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(png);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Lists the dialogs blocking the selected Revit (revit_list_dialogs is read-only: it answers nothing).</summary>
        [RelayCommand(CanExecute = nameof(CanActOnSelection))]
        public async Task ListDialogsAsync()
        {
            if (SelectedRevitInstance is not { } item)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgSelectRevit"));
                return;
            }

            var result = await CallToolAsync("revit_list_dialogs", new { pid = item.Pid }).ConfigureAwait(true);
            if (result.IsError)
            {
                Notify(NotificationKind.Error, L("Str_TitleDialogs"), L("Str_MsgToolFailed", "revit_list_dialogs", Describe(result)));
                return;
            }

            var lines = new List<string>();
            if (result.Structured is { } json && json.TryGetProperty("dialogs", out var dialogs) && dialogs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dialogs.EnumerateArray())
                {
                    var title = Str(d, "title");
                    var text = Str(d, "text");
                    var category = Str(d, "category");
                    var buttons = d.TryGetProperty("buttons", out var bs) && bs.ValueKind == JsonValueKind.Array
                        ? string.Join(" | ", bs.EnumerateArray().Select(b => Str(b, "text")).Where(t => t.Length > 0))
                        : string.Empty;
                    lines.Add($"[{category}] {title}: {Truncate(text, 140)}" + (buttons.Length > 0 ? $"  ({buttons})" : string.Empty));
                }
            }

            DialogResults = new ObservableCollection<string>(lines);
            HasDialogResults = lines.Count > 0;
            if (lines.Count == 0)
            {
                Notify(NotificationKind.Info, L("Str_TitleDialogs"), L("Str_MsgDialogsNone", item.Pid));
            }
            else
            {
                Notify(NotificationKind.Warning, L("Str_TitleDialogs"), L("Str_MsgDialogsCount", lines.Count, item.Pid) + "\n" + string.Join("\n", lines.Take(3)));
            }
        }

        /// <summary>Closes a Revit the gateway launched. Foreign (user-owned) Revit instances are never closed from here.</summary>
        [RelayCommand(CanExecute = nameof(CanClose))]
        public async Task CloseRevitAsync()
        {
            if (SelectedRevitInstance is not { } item)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgSelectRevit"));
                return;
            }

            if (!item.LaunchedByGateway)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_Tooltip_BtnCloseRevitForeign"));
                return;
            }

            var result = await CallToolAsync("revit_close", new { pid = item.Pid }).ConfigureAwait(true);
            if (result.IsError)
            {
                Notify(NotificationKind.Warning, L("Str_TitleRevit"), L("Str_MsgToolFailed", "revit_close", Describe(result)));
            }
            else
            {
                Notify(NotificationKind.Success, L("Str_TitleRevit"), L("Str_MsgClosed", item.Pid));
            }

            await RefreshAsync().ConfigureAwait(true);
        }

        // ---------------------------------------------------------------- gateway toggle + folders

        [RelayCommand]
        public async Task ToggleServerAsync()
        {
            if (IsServerRunning)
            {
                await _ipcService.StopServerAsync();
                StatusMessage?.Invoke(L("Str_MsgGatewayStopped"));
            }
            else
            {
                string pipeName = _configService.CurrentConfig.IpcPipeName ?? IpcConstants.DefaultMcpPipeName;
                await _ipcService.StartServerAsync(pipeName);
                StatusMessage?.Invoke(L("Str_MsgGatewayListening", $@"\\.\pipe\{_ipcService.ActivePipeName}"));
            }
        }

        [RelayCommand]
        public void OpenRunsFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{_paths.RunsDir}\"", UseShellExecute = true });
                _auditLogService.AddLog("SANDBOX", "Open Folder", _paths.RunsDir);
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Open Runs Folder Failed", ex.Message);
            }
        }

        // ---------------------------------------------------------------- helpers

        private enum NotificationKind { Info, Success, Warning, Error }

        private void Notify(NotificationKind kind, string title, string message)
        {
            switch (kind)
            {
                case NotificationKind.Success: _notificationService?.ShowSuccess(title, message); break;
                case NotificationKind.Warning: _notificationService?.ShowWarning(title, message); break;
                case NotificationKind.Error: _notificationService?.ShowError(title, message); break;
                default: _notificationService?.ShowInfo(title, message); break;
            }
        }

        private async Task<ToolResult> CallToolAsync(string tool, object args)
        {
            var json = JsonSerializer.SerializeToElement(args);
            return await _tools.InvokeAsync(tool, json, SessionId, CancellationToken.None).ConfigureAwait(false);
        }

        private static string Describe(ToolResult result)
        {
            if (result.Structured is { } s)
            {
                var message = Str(s, "message");
                var code = Str(s, "code");
                var next = Str(s, "next_step");
                var reason = Str(s, "reason");
                var text = string.Join(" ", new[] { code, message, reason, next }.Where(t => t.Length > 0));
                if (text.Length > 0)
                {
                    return text;
                }
            }

            return result.ErrorCode ?? "error";
        }

        private static int? GetInt(ToolResult result, string name) =>
            result.Structured is { } s && s.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
