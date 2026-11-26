namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.ObjectModel;
    using System.Threading.Tasks;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Contracts.Protocol;
    using RevitAgent.Host.Automation;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;

    public partial class MainViewModel : ObservableObject
    {
        private readonly IHostIpcService _ipcService;
        private readonly IAuditLogService _auditLogService;
        private readonly ILocalizationService _localizationService;
        private readonly IThemeService? _themeService;

        public DashboardViewModel Dashboard { get; }
        public SettingsViewModel Settings { get; }
        public AuditLogViewModel AuditLogs { get; }
        public NotificationsViewModel Notifications { get; }
        public RunHistoryViewModel RunHistory { get; }
        public ScenarioRunnerViewModel? ScenarioRunner { get; }
        public INotificationService NotificationService { get; }

        public ObservableCollection<NotificationItem> ActiveNotifications => NotificationService.ActiveToasts;

        [ObservableProperty]
        private string _currentLanguage = LocalizationService.CultureVietnamese;

        [ObservableProperty]
        private string _currentThemeMode = ThemeService.ModeSystem;

        [ObservableProperty]
        private string _activeTheme = ThemeService.ThemeDark;

        [ObservableProperty]
        private string _themeToggleText = "💻 System";

        [ObservableProperty]
        private string _clock = "--:--:--";

        [ObservableProperty]
        private string _footerStatus = "Ready for test and diagnostic requests.";

        [ObservableProperty]
        private bool _isDesktopAvailable = true;

        [ObservableProperty]
        private string _desktopStatusText = "Desktop Interactive";

        [ObservableProperty]
        private bool _isIpcServerRunning = false;

        [ObservableProperty]
        private string _ipcPipeStatusText = "Pipe: Offline";

        [ObservableProperty]
        private bool _isBridgeConnected = false;

        [ObservableProperty]
        private string _bridgeStatusText = "Bridge: Disconnected";

        [ObservableProperty]
        private bool _isUacParityMatched = true;

        [ObservableProperty]
        private string _uacStatusText = "UAC: Standby";

        [ObservableProperty]
        private int _selectedTabIndex = 0;

        public MainViewModel(
            DashboardViewModel dashboard,
            SettingsViewModel settings,
            AuditLogViewModel auditLogs,
            IHostIpcService ipcService,
            IAuditLogService auditLogService,
            ILocalizationService? localizationService = null,
            IThemeService? themeService = null,
            NotificationsViewModel? notifications = null,
            INotificationService? notificationService = null,
            RunHistoryViewModel? runHistory = null,
            ScenarioRunnerViewModel? scenarioRunner = null)
        {
            Dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            AuditLogs = auditLogs ?? throw new ArgumentNullException(nameof(auditLogs));
            _ipcService = ipcService ?? throw new ArgumentNullException(nameof(ipcService));
            _auditLogService = auditLogService ?? throw new ArgumentNullException(nameof(auditLogService));
            _localizationService = localizationService ?? new LocalizationService();
            _themeService = themeService;
            NotificationService = notificationService ?? new NotificationService(Settings.NotificationDurationSeconds);
            Notifications = notifications ?? new NotificationsViewModel(NotificationService, _localizationService);
            RunHistory = runHistory ?? new RunHistoryViewModel(new Core.RunStore.RunStore(new Core.AgentPaths(), new Core.Policy.GatewayPolicy()), _auditLogService, _localizationService);
            ScenarioRunner = scenarioRunner;

            CurrentLanguage = _localizationService.CurrentLanguage;
            _localizationService.LanguageChanged += OnLanguageChanged;

            if (_themeService != null)
            {
                CurrentThemeMode = _themeService.ThemeMode;
                ActiveTheme = _themeService.ActiveTheme;
                _themeService.ThemeChanged += (t) =>
                {
                    ActiveTheme = t;
                    CurrentThemeMode = _themeService.ThemeMode;
                    UpdateThemeDisplayText();
                };
            }
            UpdateThemeDisplayText();

            Dashboard.StatusMessage += (msg) => FooterStatus = msg;
            Settings.StatusChanged += (msg) => FooterStatus = msg;

            _ipcService.ServerStateChanged += OnServerStateChanged;
            _ipcService.BridgeConnectionChanged += OnBridgeConnectionChanged;

            FooterStatus = _localizationService.GetString("Str_StatusReady");
            CheckDesktopStatus();
            UpdateUacStatus();
        }

        private void OnLanguageChanged(string lang)
        {
            CurrentLanguage = lang;
            CheckDesktopStatus();
            UpdateUacStatus();
            UpdatePipeStatus();
            UpdateBridgeStatus();
            UpdateThemeDisplayText();
        }

        public void UpdateThemeDisplayText()
        {
            string mode = _themeService?.ThemeMode ?? CurrentThemeMode;
            CurrentThemeMode = mode;
            if (_themeService != null)
            {
                ActiveTheme = _themeService.ActiveTheme;
            }

            bool isEnglish = _localizationService.CurrentLanguage == LocalizationService.CultureEnglish;

            // 1-Click Binary Toggle displays the action that will occur on click
            if (string.Equals(ActiveTheme, ThemeService.ThemeDark, StringComparison.OrdinalIgnoreCase))
            {
                ThemeToggleText = "☀️ " + (isEnglish ? "Light" : "Sáng");
            }
            else
            {
                ThemeToggleText = "🌙 " + (isEnglish ? "Dark" : "Tối");
            }
        }

        [RelayCommand]
        public void ToggleTheme()
        {
            if (_themeService != null)
            {
                string nextMode = _themeService.ToggleBinaryTheme();
                Settings.SelectedThemeMode = nextMode;
                UpdateThemeDisplayText();

                string themeName = nextMode == ThemeService.ModeLight ? "Light" : "Dark";
                NotificationService.ShowInfo(
                    _localizationService.GetString("Str_SecTheme"),
                    $"Switched to the {themeName} theme.");
            }
        }

        [RelayCommand]
        public void DismissNotification(NotificationItem? item)
        {
            if (item != null)
            {
                NotificationService.DismissToast(item);
            }
        }

        [RelayCommand]
        public void NavigateToNotifications()
        {
            SelectedTabIndex = 1; // Tab 1 is Notifications
        }

        [RelayCommand]
        public void NavigateToAuditLogs()
        {
            SelectedTabIndex = 2; // Tab 2 is Audit Logs
        }

        public async Task InitializeAsync()
        {
            _auditLogService.AddLog("SYSTEM", "Dashboard Started", "RevitAgent Host Dashboard (gateway v2) is ready.");
            OnTick();
            await Dashboard.InitializeAsync();
            UpdateUacStatus();

            // Auto-start server with configured pipe name
            string pipeName = Settings.IpcPipeName;
            if (string.IsNullOrWhiteSpace(pipeName)) pipeName = IpcConstants.DefaultMcpPipeName;

            try
            {
                await _ipcService.StartServerAsync(pipeName);
                FooterStatus = $"Host listening on \\\\.\\pipe\\{_ipcService.ActivePipeName}";
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "AutoStart IPC Failed", ex.Message);
            }
        }

        public void OnTick()
        {
            Clock = DateTime.Now.ToString("HH:mm:ss");
            CheckDesktopStatus();
            Dashboard.OnPollTick();
            UpdateUacStatus();
        }

        private void CheckDesktopStatus()
        {
            bool available = DesktopPreflight.IsDesktopAvailable();
            IsDesktopAvailable = available;
            DesktopStatusText = available
                ? _localizationService.GetString("Str_BadgeDesktopInteractive")
                : _localizationService.GetString("Str_BadgeDesktopLocked");
        }

        private void UpdateUacStatus()
        {
            bool? matched = Dashboard.SelectedUacMatched;
            if (matched is null)
            {
                IsUacParityMatched = true;
                UacStatusText = _localizationService.GetString("Str_BadgeUacStandby");
            }
            else
            {
                IsUacParityMatched = matched.Value;
                UacStatusText = matched.Value
                    ? _localizationService.GetString("Str_BadgeUacMatched")
                    : _localizationService.GetString("Str_BadgeUacMismatch");
            }
        }

        private void UpdatePipeStatus()
        {
            IpcPipeStatusText = IsIpcServerRunning
                ? $"Pipe: {_ipcService.ActivePipeName}"
                : _localizationService.GetString("Str_BadgePipeOffline");
        }

        private void UpdateBridgeStatus()
        {
            BridgeStatusText = IsBridgeConnected
                ? _localizationService.GetString("Str_BadgeBridgeConnected")
                : _localizationService.GetString("Str_BadgeBridgeDisconnected");
        }

        private void OnServerStateChanged(bool running)
        {
            IsIpcServerRunning = running;
            UpdatePipeStatus();
            if (running)
            {
                NotificationService.ShowSuccess(
                    "Named Pipe Server",
                    $"Host listening on \\\\.\\pipe\\{_ipcService.ActivePipeName}");
            }
            else
            {
                IsBridgeConnected = false;
                UpdateBridgeStatus();
                NotificationService.ShowInfo(
                    "Named Pipe Server",
                    "Named Pipe Server stopped.");
            }
        }

        private void OnBridgeConnectionChanged(bool connected)
        {
            IsBridgeConnected = connected;
            UpdateBridgeStatus();
            if (connected)
            {
                NotificationService.ShowSuccess(
                    "Ribbon Bridge",
                    "The Revit add-in connected to the Host.");
            }
            else
            {
                NotificationService.ShowWarning(
                    "Ribbon Bridge",
                    "The Revit add-in disconnected.");
            }
        }

        [RelayCommand]
        public void SwitchLanguage(string? targetLang)
        {
            if (string.IsNullOrWhiteSpace(targetLang))
            {
                targetLang = CurrentLanguage == LocalizationService.CultureVietnamese
                    ? LocalizationService.CultureEnglish
                    : LocalizationService.CultureVietnamese;
            }

            Settings.SelectedLanguage = targetLang;
        }

        #region Toolbar Forwarding Commands

        [RelayCommand]
        private Task ToggleServerAsync() => Dashboard.ToggleServerCommand.ExecuteAsync(null);

        [RelayCommand]
        private Task LaunchRevitAsync() => Dashboard.LaunchRevitCommand.ExecuteAsync(null);

        [RelayCommand]
        private Task CaptureScreenshotAsync() => Dashboard.CaptureScreenshotCommand.ExecuteAsync(null);

        [RelayCommand]
        private Task ListDialogsAsync() => Dashboard.ListDialogsCommand.ExecuteAsync(null);

        [RelayCommand]
        private Task InstallBridgeAsync() => Dashboard.InstallBridgeCommand.ExecuteAsync(null);

        [RelayCommand]
        private void CopyClaudeCode() => Dashboard.CopyClaudeCodeCommand.Execute(null);

        [RelayCommand]
        private void OpenRunsFolder() => Dashboard.OpenRunsFolderCommand.Execute(null);

        [RelayCommand]
        private void ClearLogs() => AuditLogs.ClearLogsCommand.Execute(null);

        #endregion
    }
}
