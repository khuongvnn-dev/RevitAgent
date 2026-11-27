namespace RevitAgent.Host.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using CommunityToolkit.Mvvm.ComponentModel;
    using CommunityToolkit.Mvvm.Input;
    using RevitAgent.Contracts.Protocol;
    using RevitAgent.Core;
    using RevitAgent.Core.Notify;
    using RevitAgent.Core.Policy;
    using RevitAgent.Core.RunStore;
    using RevitAgent.Core.Scenario;
    using RevitAgent.Core.Tools;
    using RevitAgent.Host.Config;
    using RevitAgent.Host.Services;

    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IEncryptedConfigService _configService;
        private readonly GatewayPolicy _policy;
        private readonly AgentPaths _paths;
        private readonly IAuditLogService _auditLogService;
        private readonly ILocalizationService _localizationService;
        private readonly IThemeService? _themeService;
        private readonly INotificationService? _notificationService;
        private readonly TelegramConfigStore _telegramConfigStore;
        private readonly TelegramClient _telegramClient;

        [ObservableProperty]
        private string _selectedLanguage = LocalizationService.CultureVietnamese;

        [ObservableProperty]
        private string _selectedThemeMode = ThemeService.ModeSystem;

        [ObservableProperty]
        private int _notificationDurationSeconds = 3;

        [ObservableProperty]
        private string _ipcPipeName = IpcConstants.DefaultMcpPipeName;

        /// <summary>Read, Interact, Write or Admin: the highest tool permission AI clients may use.</summary>
        [ObservableProperty]
        private string _selectedMaxPermission = nameof(ToolPermission.Admin);

        [ObservableProperty]
        private bool _enforceSandbox = true;

        /// <summary>Comma-separated process names (besides Revit) that ui_* input may reach. Empty = Revit only.</summary>
        [ObservableProperty]
        private string _allowedInputProcesses = string.Empty;

        [ObservableProperty]
        private string _runStoreRoot = string.Empty;

        [ObservableProperty]
        private string _scenarioLibraryDir = string.Empty;

        [ObservableProperty]
        private string _telegramBotToken = string.Empty;

        [ObservableProperty]
        private long _telegramChatId;

        [ObservableProperty]
        private int _telegramTimeoutMinutes = 30;

        [ObservableProperty]
        private bool _telegramEnabled;

        [ObservableProperty]
        private string _telegramTestStatus = string.Empty;

        [ObservableProperty]
        private bool _isTelegramTestSuccess = true;

        public IReadOnlyList<string> PermissionOptions => GatewayPolicySettings.PermissionNames;

        [ObservableProperty]
        private string _configStatusText = "Ready to load or save the configuration.";

        [ObservableProperty]
        private bool _isConfigStatusSuccess = true;

        public event Action<string>? StatusChanged;

        public SettingsViewModel(
            IEncryptedConfigService configService,
            GatewayPolicy policy,
            AgentPaths paths,
            IAuditLogService auditLogService,
            ILocalizationService? localizationService = null,
            IThemeService? themeService = null,
            INotificationService? notificationService = null,
            TelegramConfigStore? telegramConfigStore = null,
            TelegramClient? telegramClient = null)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _auditLogService = auditLogService ?? throw new ArgumentNullException(nameof(auditLogService));
            _localizationService = localizationService ?? new LocalizationService();
            _themeService = themeService;
            _notificationService = notificationService;
            _telegramConfigStore = telegramConfigStore ?? new TelegramConfigStore(paths);
            _telegramClient = telegramClient ?? new TelegramClient();

            LoadConfigFromService();
        }

        partial void OnSelectedThemeModeChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_themeService != null && _themeService.ThemeMode != value)
            {
                _themeService.SetThemeMode(value);
                SaveThemeToConfig(value);
            }
        }

        private void SaveThemeToConfig(string mode)
        {
            try
            {
                var cfg = _configService.CurrentConfig ?? _configService.LoadConfig();
                cfg.ThemeMode = mode;
                _configService.SaveConfig(cfg);
                _auditLogService.AddLog("CONFIG", "Theme Changed", $"Theme mode changed to {mode} and saved to DPAPI.");
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Save Theme Failed", ex.Message);
            }
        }

        partial void OnSelectedLanguageChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            string normalized = value.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? LocalizationService.CultureEnglish
                : LocalizationService.CultureVietnamese;

            if (_localizationService.CurrentLanguage != normalized)
            {
                _localizationService.SetLanguage(normalized);
                SaveLanguageToConfig(normalized);
            }
        }

        private void SaveLanguageToConfig(string lang)
        {
            try
            {
                var cfg = _configService.CurrentConfig ?? _configService.LoadConfig();
                cfg.Language = lang;
                _configService.SaveConfig(cfg);
                _auditLogService.AddLog("CONFIG", "Language Changed", $"Language changed to {lang} and saved to DPAPI.");
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Save Language Failed", ex.Message);
            }
        }

        public void LoadConfigFromService()
        {
            try
            {
                var cfg = _configService.LoadConfig();

                SelectedLanguage = string.IsNullOrWhiteSpace(cfg.Language)
                    ? LocalizationService.CultureVietnamese
                    : cfg.Language;

                SelectedThemeMode = string.IsNullOrWhiteSpace(cfg.ThemeMode)
                    ? ThemeService.ModeSystem
                    : cfg.ThemeMode;

                _localizationService.SetLanguage(SelectedLanguage);
                _themeService?.SetThemeMode(SelectedThemeMode);

                NotificationDurationSeconds = cfg.NotificationDurationSeconds;
                if (_notificationService != null)
                {
                    _notificationService.DefaultDurationSeconds = NotificationDurationSeconds;
                }

                IpcPipeName = string.IsNullOrWhiteSpace(cfg.IpcPipeName) ? IpcConstants.DefaultMcpPipeName : cfg.IpcPipeName;
                SelectedMaxPermission = GatewayPolicySettings.ParsePermission(cfg.GatewayMaxPermission).ToString();
                EnforceSandbox = cfg.GatewayEnforceSandbox;
                AllowedInputProcesses = string.Join(", ", GatewayPolicySettings.CleanProcessNames(cfg.GatewayAllowedInputProcesses));
                RunStoreRoot = RunStoreOptions.Load(_paths).GetEffectiveRoot(_paths);
                ScenarioLibraryDir = ScenarioLibraryOptions.GetEffectiveLibraryDir(_paths);
                GatewayPolicySettings.Apply(cfg, _policy);

                var tg = _telegramConfigStore.Load();
                TelegramBotToken = tg.BotToken ?? string.Empty;
                TelegramChatId = tg.ChatId;
                TelegramTimeoutMinutes = tg.AskTimeoutMinutes > 0 ? tg.AskTimeoutMinutes : 30;
                TelegramEnabled = tg.Enabled;
                TelegramTestStatus = string.Empty;

                if (_configService.ConfigFileExists)
                {
                    string fmt = _localizationService.GetString("Str_StatusConfigLoaded");
                    ConfigStatusText = string.Format(fmt, Path.GetFileName(_configService.ConfigFilePath));
                    IsConfigStatusSuccess = true;
                }
                else
                {
                    ConfigStatusText = _localizationService.GetString("Str_StatusConfigDefault");
                    IsConfigStatusSuccess = true;
                }
            }
            catch (Exception ex)
            {
                ConfigStatusText = $"Failed to decrypt the configuration: {ex.Message}";
                IsConfigStatusSuccess = false;
                _auditLogService.AddLog("ERROR", "DPAPI Decrypt Failed", ex.Message);
            }
        }

        [RelayCommand]
        public void SaveConfig()
        {
            try
            {
                // Checked first so nothing is saved when the run-data folder is not allowed (it is part of the sandbox).
                if (RunStoreOptions.Validate(RunStoreRoot) is { } invalidRoot)
                {
                    throw new ArgumentException("Run-data folder: " + invalidRoot);
                }

                var cfg = new AppConfig
                {
                    Language = SelectedLanguage,
                    ThemeMode = SelectedThemeMode,
                    NotificationDurationSeconds = NotificationDurationSeconds > 0 ? NotificationDurationSeconds : 3,
                    IpcPipeName = string.IsNullOrWhiteSpace(IpcPipeName) ? IpcConstants.DefaultMcpPipeName : IpcPipeName.Trim(),
                    GatewayMaxPermission = GatewayPolicySettings.ParsePermission(SelectedMaxPermission).ToString(),
                    GatewayEnforceSandbox = EnforceSandbox,
                    GatewayAllowedInputProcesses = GatewayPolicySettings.ParseProcessList(AllowedInputProcesses),
                };

                if (_notificationService != null)
                {
                    _notificationService.DefaultDurationSeconds = cfg.NotificationDurationSeconds;
                }

                _configService.SaveConfig(cfg);
                RunStoreOptions.Save(_paths, new RunStoreOptions { Root = RunStoreRoot });
                ScenarioLibraryOptions.Save(_paths, ScenarioLibraryDir);
                GatewayPolicySettings.Apply(cfg, _policy);

                var tg = new TelegramOptions
                {
                    BotToken = TelegramBotToken,
                    ChatId = TelegramChatId,
                    AskTimeoutMinutes = TelegramTimeoutMinutes > 0 ? TelegramTimeoutMinutes : 30,
                    Enabled = TelegramEnabled
                };
                _telegramConfigStore.Save(tg);

                ConfigStatusText = _localizationService.GetString("Str_StatusConfigSaved") + $" ({DateTime.Now:HH:mm:ss})";
                IsConfigStatusSuccess = true;

                _auditLogService.AddLog("CONFIG", "Save DPAPI", $"Configuration encrypted and saved at: {_configService.ConfigFilePath}");
                string savedMsg = _localizationService.GetString("Str_StatusConfigSaved");
                StatusChanged?.Invoke(savedMsg);
                _notificationService?.ShowSuccess(
                    _localizationService.GetString("Str_HeaderTitle"),
                    savedMsg);
            }
            catch (Exception ex)
            {
                ConfigStatusText = $"Failed to save the configuration: {ex.Message}";
                IsConfigStatusSuccess = false;
                _auditLogService.AddLog("ERROR", "Save Config Failed", ex.Message);
                _notificationService?.ShowError("Config Error", ex.Message);
            }
        }

        [RelayCommand]
        public void TestToast(string? level)
        {
            if (_notificationService == null) return;

            string lvl = (level ?? "INFO").ToUpperInvariant();
            switch (lvl)
            {
                case "SUCCESS":
                    _notificationService.ShowSuccess("Success", "Configuration saved.");
                    break;
                case "WARN":
                case "WARNING":
                    _notificationService.ShowWarning("Warning", "UAC elevation differs between Revit and the Host.");
                    break;
                case "ERROR":
                    _notificationService.ShowError("System error", "The Named Pipe connection was lost or the Revit process stopped unexpectedly.");
                    break;
                default:
                    _notificationService.ShowInfo("Info", "The RevitAgent Host is ready to accept commands.");
                    break;
            }
        }

        [RelayCommand]
        public void ReloadConfig()
        {
            LoadConfigFromService();
            _auditLogService.AddLog("CONFIG", "Reload DPAPI", "Configuration reloaded from disk.");
            StatusChanged?.Invoke(_localizationService.GetString("Str_StatusConfigReloaded"));
        }

        [RelayCommand]
        public void OpenConfigFolder()
        {
            try
            {
                _configService.OpenConfigFileFolder();
                _auditLogService.AddLog("CONFIG", "Open Folder", $"Opened the configuration folder: {_configService.ConfigFilePath}");
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Open Config Folder", ex.Message);
            }
        }

        [RelayCommand]
        public async Task TestTelegramAsync()
        {
            if (string.IsNullOrWhiteSpace(TelegramBotToken) || TelegramChatId == 0)
            {
                TelegramTestStatus = _localizationService.GetString("Str_MsgTgUnconfigured");
                IsTelegramTestSuccess = false;
                return;
            }

            try
            {
                TelegramTestStatus = "Sending...";
                var testText = $"🔔 Test from RevitAgent Host ({DateTime.Now:HH:mm:ss})";
                await _telegramClient.SendMessageAsync(TelegramBotToken, TelegramChatId, testText, replyMarkup: null, parseMode: "HTML");
                TelegramTestStatus = _localizationService.GetString("Str_MsgTgTestSuccess");
                IsTelegramTestSuccess = true;
                _auditLogService.AddLog("TELEGRAM", "Test Message", $"Sent a test message to chat_id {TelegramChatId}");
                _notificationService?.ShowSuccess("Telegram", TelegramTestStatus);
            }
            catch (Exception ex)
            {
                var safeError = TelegramClient.Redact(ex.Message, TelegramBotToken);
                TelegramTestStatus = string.Format(_localizationService.GetString("Str_MsgTgTestFailed"), safeError);
                IsTelegramTestSuccess = false;
                _auditLogService.AddLog("ERROR", "Telegram Test Failed", safeError);
                _notificationService?.ShowError("Telegram Error", safeError);
            }
        }

        [RelayCommand]
        public void BrowseScenarioLibraryDir()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = _localizationService.GetString("Str_SecScenarioLibrary"),
                    InitialDirectory = Directory.Exists(ScenarioLibraryDir) ? ScenarioLibraryDir : Path.Combine(_paths.Root, "scenarios"),
                };
                if (dialog.ShowDialog() == true)
                {
                    ScenarioLibraryDir = dialog.FolderName;
                }
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Browse Scenario Library Dir", ex.Message);
            }
        }

        [RelayCommand]
        public void BrowseRunStoreRoot()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = _localizationService.GetString("Str_SecRunStore"),
                    InitialDirectory = Directory.Exists(RunStoreRoot) ? RunStoreRoot : _paths.RunsDir,
                };
                if (dialog.ShowDialog() == true)
                {
                    RunStoreRoot = dialog.FolderName;
                }
            }
            catch (Exception ex)
            {
                _auditLogService.AddLog("ERROR", "Browse Run Store Root", ex.Message);
            }
        }
    }
}
