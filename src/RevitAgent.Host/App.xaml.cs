namespace RevitAgent.Host
{
    using System;
    using System.Windows;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using RevitAgent.Core;
    using RevitAgent.Core.Composition;
    using RevitAgent.Core.Policy;
    using RevitAgent.Host.Config;
    using RevitAgent.Host.Services;
    using RevitAgent.Host.ViewModels;

    /// <summary>
    /// Interaction logic for App.xaml
    /// Configures Microsoft.Extensions.Hosting Generic Host and Dependency Injection Container.
    /// </summary>
    public partial class App : Application
    {
        private readonly IHost _host;

        public static IServiceProvider Services { get; private set; } = null!;

        public App()
        {
            RegisterGlobalExceptionHandlers();

            _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    ConfigureAppServices(services);
                })
                .Build();

            Services = _host.Services;
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += (_, args) =>
            {
                LogUnhandled("Dispatcher", args.Exception);
                // Keep the dashboard alive for ordinary errors; only out-of-memory style failures are fatal.
                args.Handled = args.Exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                LogUnhandled("AppDomain" + (args.IsTerminating ? " (terminating)" : string.Empty), args.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogUnhandled("Task", args.Exception);
                args.SetObserved();
            };
        }

        private void LogUnhandled(string source, Exception? ex)
        {
            try
            {
                Services?.GetService<RevitAgent.Core.Audit.AuditLog>()?.Diagnostic($"Unhandled exception ({source})", ex);
                Services?.GetService<IAuditLogService>()?.AddLog("Error", "UnhandledException", $"{source}: {ex?.GetType().Name}: {ex?.Message}");
            }
            catch (Exception)
            {
                // Logging must never throw from an exception handler.
            }
        }

        public static void ConfigureAppServices(IServiceCollection services, AgentPaths? paths = null)
        {
            // Localization & Theme Services
            services.AddSingleton<ILocalizationService, LocalizationService>();
            services.AddSingleton<IThemeService, ThemeService>();

            // RevitAgent gateway (tools, MCP server, audit, policy) from RevitAgent.Core
            services.AddRevitAgentGateway(paths);

            // Core Infrastructure & Application Services
            services.AddSingleton<IEncryptedConfigService, EncryptedConfigService>();
            services.AddSingleton<IAuditLogService, AuditLogService>();
            services.AddSingleton<IHostIpcService, HostIpcService>();
            services.AddSingleton<INotificationService, NotificationService>();

            // ViewModels
            services.AddSingleton<AuditLogViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<NotificationsViewModel>();
            services.AddSingleton<AddinAllowlistViewModel>();
            services.AddSingleton<RunHistoryViewModel>();
            services.AddSingleton<ScenarioRunnerViewModel>();
            services.AddSingleton<MainViewModel>();

            // Views
            services.AddSingleton<MainWindow>();
            services.AddSingleton<Views.RunHistoryView>();
            services.AddSingleton<Views.ScenarioRunnerView>();
        }

        private static System.Threading.Mutex? _singleInstance;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // One dashboard per user session: a second start (e.g. by the Shim) just exits.
            _singleInstance = new System.Threading.Mutex(true, @"Local\RevitAgent.Host.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            await _host.StartAsync();

            // Initialize Localization and Theme from DPAPI encrypted config
            var configService = _host.Services.GetRequiredService<IEncryptedConfigService>();
            var localizationService = _host.Services.GetRequiredService<ILocalizationService>();
            var themeService = _host.Services.GetRequiredService<IThemeService>();
            AppConfig config;
            try
            {
                config = configService.LoadConfig();
            }
            catch (Exception ex)
            {
                // A damaged file, or one written by another user/machine (DPAPI), must not stop the app from starting:
                // keep it aside for inspection and continue with defaults.
                var moved = new EncryptedConfigManager(configService.ConfigFilePath).QuarantineConfigFile();
                _host.Services.GetService<RevitAgent.Core.Audit.AuditLog>()?.Diagnostic(
                    $"Configuration file '{configService.ConfigFilePath}' could not be read; " +
                    (moved is null ? "continuing with defaults." : $"moved to '{moved}' and continuing with defaults."), ex);
                _host.Services.GetService<IAuditLogService>()?.AddLog("Config", "ConfigReset",
                    moved is null ? "Configuration unreadable; defaults used." : $"Configuration unreadable; saved aside as {System.IO.Path.GetFileName(moved)}; defaults used.");
                config = new AppConfig();
            }

            localizationService.SetLanguage(config.Language);
            themeService.SetThemeMode(config.ThemeMode);

            // Gateway policy chosen in Settings (max permission, sandbox enforcement) governs every MCP client from the first call.
            GatewayPolicySettings.Apply(config, _host.Services.GetRequiredService<GatewayPolicy>());

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            if (Array.Exists(e.Args, a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                // Started on demand by RevitAgent.Shim for an AI client: stay out of the user's way.
                mainWindow.WindowState = WindowState.Minimized;
            }

            mainWindow.Show();

            // Start the nightly schedule service if configured
            var scheduleService = _host.Services.GetService<RevitAgent.Core.Schedule.ScheduleService>();
            scheduleService?.Start();

            // The MCP gateway must be up whenever the dashboard runs ("just turn the tool on").
            var ipc = _host.Services.GetRequiredService<IHostIpcService>();
            if (!ipc.IsRunning)
            {
                await ipc.StartServerAsync(config.IpcPipeName);
            }

            // Start Revit watchdog for gateway-launched Revit processes
            _host.Services.GetService<RevitAgent.Core.Revit.RevitWatchdog>()?.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _host.Services.GetService<RevitAgent.Core.Revit.RevitWatchdog>()?.Stop();
            }
            catch (Exception)
            {
            }

            try
            {
                _host.Services.GetService<RevitAgent.Core.Schedule.ScheduleService>()?.Stop();
            }
            catch (Exception)
            {
            }

            // Must be synchronous: an 'async void' OnExit never resumes once the dispatcher has shut down,
            // and the Generic Host's console lifetime then blocks process exit forever (seen 2026-11-20).
            // Several singletons (gateway pipe listener, IPC service) are IAsyncDisposable only, so dispose async.
            try
            {
                Task.Run(async () =>
                {
                    await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    if (_host is IAsyncDisposable asyncHost)
                    {
                        await asyncHost.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _host.Dispose();
                    }
                }).Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // Exiting anyway; nothing useful to report to a closing UI.
            }

            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
