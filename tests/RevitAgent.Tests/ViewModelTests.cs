namespace RevitAgent.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Media;
    using Microsoft.Extensions.DependencyInjection;
    using RevitAgent.Host;
    using RevitAgent.Host.Converters;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;
    using RevitAgent.Host.ViewModels;
    using Xunit;

    public class ViewModelTests
    {
        [Fact]
        public async Task DependencyInjection_ResolvesAllServicesAndViewModels_Successfully()
        {
            // Arrange: isolated gateway home so nothing touches the user's real %LOCALAPPDATA%\RevitAgent
            var services = new ServiceCollection();
            App.ConfigureAppServices(services, new RevitAgent.Core.AgentPaths(TestGateway.NewTempDir("revitagent_di_")));

            await using var provider = services.BuildServiceProvider();

            // Act & Assert - Services
            Assert.NotNull(provider.GetRequiredService<IEncryptedConfigService>());
            Assert.NotNull(provider.GetRequiredService<IAuditLogService>());
            Assert.NotNull(provider.GetRequiredService<IHostIpcService>());
            Assert.NotNull(provider.GetRequiredService<IThemeService>());
            Assert.NotNull(provider.GetRequiredService<INotificationService>());
            Assert.NotNull(provider.GetRequiredService<RevitAgent.Core.Revit.RevitProcessService>());
            Assert.NotNull(provider.GetRequiredService<RevitAgent.Core.Tools.ToolRegistry>());
            Assert.NotNull(provider.GetRequiredService<RevitAgent.Core.Notify.INotifier>());
            Assert.NotNull(provider.GetRequiredService<RevitAgent.Core.Notify.IHumanEscalation>());
            Assert.NotNull(provider.GetRequiredService<RevitAgent.Core.Notify.TelegramConfigStore>());

            // Act & Assert - ViewModels
            Assert.NotNull(provider.GetRequiredService<AuditLogViewModel>());
            Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
            Assert.NotNull(provider.GetRequiredService<DashboardViewModel>());
            Assert.NotNull(provider.GetRequiredService<NotificationsViewModel>());
            Assert.NotNull(provider.GetRequiredService<MainViewModel>());
        }

        [Fact]
        public void AuditLogViewModel_FilteringAndSearching_WorksCorrectly()
        {
            // Arrange
            var logService = new AuditLogService();
            var vm = new AuditLogViewModel(logService);

            var notifiedProperties = new List<string>();
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null) notifiedProperties.Add(e.PropertyName);
            };

            // Act - Add logs
            logService.AddLog("INFO", "Action1", "Details one");
            logService.AddLog("WARN", "Action2", "Details two with Warning");
            logService.AddLog("ERROR", "Action3", "Details three with Error");
            logService.AddLog("IPC", "Action4", "Details four IPC message");

            // Assert initial state
            Assert.Equal(4, vm.EventLogs.Count);
            Assert.Equal("ALL", vm.CurrentLogLevelFilter);

            // Act - Filter WARN
            vm.FilterLogLevelCommand.Execute("WARN");

            // Assert
            Assert.Equal("WARN", vm.CurrentLogLevelFilter);
            Assert.Contains(nameof(vm.CurrentLogLevelFilter), notifiedProperties);

            // Act - Search text
            vm.SearchText = "Warning";
            Assert.Contains(nameof(vm.SearchText), notifiedProperties);

            // Act - Clear logs
            vm.ClearLogsCommand.Execute(null);
            Assert.Empty(vm.EventLogs);
        }

        [Fact]
        public void SettingsViewModel_SaveConfig_PersistsPipeNameAndReportsSuccess()
        {
            using var rig = new Host.HostRig();
            var vm = rig.NewSettings();

            vm.IpcPipeName = "test-pipe-custom";
            vm.SaveConfigCommand.Execute(null);

            Assert.True(vm.IsConfigStatusSuccess);
            Assert.Equal("test-pipe-custom", rig.Config.LoadConfig().IpcPipeName);
        }

        [Fact]
        public async Task DashboardViewModel_ToggleServer_StartsAndStopsTheGateway()
        {
            using var rig = new Host.HostRig();
            var vm = rig.Dashboard;

            Assert.False(vm.IsServerRunning);
            Assert.True(vm.ServerToggleText.Contains("Khởi động") || vm.ServerToggleText.Contains("Start"));

            await vm.ToggleServerCommand.ExecuteAsync(null);
            Assert.True(vm.IsServerRunning);
            Assert.True(vm.ServerToggleText.Contains("Dừng") || vm.ServerToggleText.Contains("Stop"));

            await vm.ToggleServerCommand.ExecuteAsync(null);
            Assert.False(vm.IsServerRunning);
            Assert.True(vm.ServerToggleText.Contains("Khởi động") || vm.ServerToggleText.Contains("Start"));
        }

        [Fact]
        public async Task MainViewModel_TickAndToolbarForwarding_NotifiesPropertyChanges()
        {
            using var rig = new Host.HostRig();
            rig.Instances.Add(Host.HostRig.Instance(10));
            var mainVm = rig.NewMain();

            var changedProps = new List<string>();
            mainVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null) changedProps.Add(e.PropertyName);
            };

            mainVm.OnTick();

            Assert.NotEqual("--:--:--", mainVm.Clock);
            Assert.Contains(nameof(mainVm.Clock), changedProps);
            Assert.NotNull(mainVm.DesktopStatusText);

            // No Revit selected: UAC badge is on standby
            Assert.True(mainVm.IsUacParityMatched);

            await rig.Dashboard.RefreshAsync();
            rig.Dashboard.SelectedRevitInstance = rig.Dashboard.RevitInstances[0];
            mainVm.OnTick();
            Assert.NotNull(mainVm.UacStatusText);

            // Toolbar forwarding: copy the Claude Code command
            mainVm.CopyClaudeCodeCommand.Execute(null);
            Assert.Single(rig.Clipboard);
        }

        [Fact]
        public void MainViewModel_ClearLogsToolbarCommand_ClearsTheAuditList()
        {
            using var rig = new Host.HostRig();
            var mainVm = rig.NewMain();

            rig.Log.ClearLogs();
            rig.Log.AddLog("INFO", "Test", "Msg");
            Assert.Single(mainVm.AuditLogs.EventLogs);
            mainVm.ClearLogsCommand.Execute(null);
            Assert.Empty(mainVm.AuditLogs.EventLogs);
        }

        [Fact]
        public void ToolbarForwardingCommands_ExistAndRemovedV1CommandsAreGone()
        {
            var main = typeof(MainViewModel);
            foreach (var name in new[] { "LaunchRevitCommand", "CaptureScreenshotCommand", "ListDialogsCommand", "InstallBridgeCommand", "CopyClaudeCodeCommand", "OpenRunsFolderCommand", "ToggleServerCommand", "ClearLogsCommand" })
            {
                Assert.NotNull(main.GetProperty(name));
            }

            foreach (var name in new[] { "DismissDialogCommand", "EmergencyEscapeCommand", "CheckPreflightCommand", "OpenSandboxCommand" })
            {
                Assert.Null(main.GetProperty(name));
                Assert.Null(typeof(DashboardViewModel).GetProperty(name));
            }
        }

        [Fact]
        public void ValueConverters_EvaluateCorrectly()
        {
            // BoolToBrushConverter
            var boolBrushConv = new BoolToBrushConverter
            {
                TrueBrush = Brushes.Green,
                FalseBrush = Brushes.Red
            };
            Assert.Equal(Brushes.Green, boolBrushConv.Convert(true, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Brushes.Red, boolBrushConv.Convert(false, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture));

            // BoolToVisibilityConverter
            var boolVisConv = new BoolToVisibilityConverter();
            Assert.Equal(System.Windows.Visibility.Visible, boolVisConv.Convert(true, typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(System.Windows.Visibility.Collapsed, boolVisConv.Convert(false, typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));

            var invertedBoolVisConv = new BoolToVisibilityConverter { Inverted = true };
            Assert.Equal(System.Windows.Visibility.Collapsed, invertedBoolVisConv.Convert(true, typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(System.Windows.Visibility.Visible, invertedBoolVisConv.Convert(false, typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));

            // NullToVisibilityConverter
            var nullVisConv = new NullToVisibilityConverter();
            Assert.Equal(System.Windows.Visibility.Collapsed, nullVisConv.Convert(null, typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(System.Windows.Visibility.Collapsed, nullVisConv.Convert("", typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(System.Windows.Visibility.Visible, nullVisConv.Convert("Some text", typeof(System.Windows.Visibility), null, System.Globalization.CultureInfo.InvariantCulture));

            // EqualityToBrushConverter
            var eqConv = new EqualityToBrushConverter
            {
                ActiveBrush = Brushes.Blue,
                InactiveBrush = Brushes.Gray
            };
            Assert.Equal(Brushes.Blue, eqConv.Convert("ALL", typeof(Brush), "ALL", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(Brushes.Gray, eqConv.Convert("INFO", typeof(Brush), "ALL", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
