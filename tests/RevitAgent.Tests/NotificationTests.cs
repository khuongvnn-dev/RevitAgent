namespace RevitAgent.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using RevitAgent.Host.Config;
    using RevitAgent.Host.Models;
    using RevitAgent.Host.Services;
    using RevitAgent.Host.ViewModels;
    using Xunit;

    public class NotificationTests
    {
        [Fact]
        public void NotificationService_ShowMethods_AddItemsWithCorrectProperties()
        {
            // Arrange
            using var service = new NotificationService(defaultDurationSeconds: 4);

            // Act
            var success = service.ShowSuccess("Success Title", "Success details");
            var info = service.ShowInfo("Info Title", "Info details");
            var warn = service.ShowWarning("Warn Title", "Warn details");
            var error = service.ShowError("Error Title", "Error details");

            // Assert
            Assert.Equal(4, service.ActiveToasts.Count);
            Assert.Equal(4, service.History.Count);
            Assert.Equal(4, service.UnreadCount);

            Assert.Equal(NotificationType.Success, success.Type);
            Assert.Equal("✓", success.IconGlyph);
            Assert.Equal("Success Title", success.Title);

            Assert.Equal(NotificationType.Info, info.Type);
            Assert.Equal("ℹ", info.IconGlyph);

            Assert.Equal(NotificationType.Warning, warn.Type);
            Assert.Equal("⚠", warn.IconGlyph);

            Assert.Equal(NotificationType.Error, error.Type);
            Assert.Equal("⛔", error.IconGlyph);
        }

        [Fact]
        public void NotificationService_ActiveToasts_CappedAtMaximumStack()
        {
            // Arrange
            using var service = new NotificationService(defaultDurationSeconds: 10);

            // Act - Push 6 notifications (max active toasts is 4)
            for (int i = 1; i <= 6; i++)
            {
                service.ShowInfo($"Title {i}", $"Message {i}");
            }

            // Assert: Active toasts must be capped at 4, but History retains all 6
            Assert.Equal(4, service.ActiveToasts.Count);
            Assert.Equal(6, service.History.Count);

            // The oldest active toast should be Title 3 (Titles 1 and 2 evicted from active)
            Assert.Equal("Title 3", service.ActiveToasts[0].Title);
            Assert.Equal("Title 6", service.ActiveToasts[3].Title);
        }

        [Fact]
        public void NotificationService_DismissToast_RemovesFromActiveOnly()
        {
            // Arrange
            using var service = new NotificationService();
            var item = service.ShowSuccess("Job Finished", "Export completed");

            Assert.Single(service.ActiveToasts);
            Assert.Single(service.History);

            // Act
            service.DismissToast(item.Id);

            // Assert: Active is empty, but History still preserves it
            Assert.Empty(service.ActiveToasts);
            Assert.Single(service.History);
        }

        [Fact]
        public async Task NotificationService_AutoDismiss_RemovesToastAfterDuration()
        {
            // Arrange
            using var service = new NotificationService();

            // Act: Show with 1 second duration
            var item = service.ShowInfo("Auto Disappearing", "Will close soon", durationSeconds: 1);
            Assert.Single(service.ActiveToasts);

            // Wait 1.3 seconds
            await Task.Delay(1300);

            // Assert: Toast auto-dismissed from active stack, but remains in history
            Assert.Empty(service.ActiveToasts);
            Assert.Single(service.History);
        }

        [Fact]
        public void NotificationService_MarkAllAsReadAndClearHistory_WorkProperly()
        {
            // Arrange
            using var service = new NotificationService();
            service.ShowInfo("1", "M1");
            service.ShowWarning("2", "M2");

            Assert.Equal(2, service.UnreadCount);

            // Act - Mark all as read
            service.MarkAllAsRead();
            Assert.Equal(0, service.UnreadCount);
            Assert.True(service.History.All(n => n.IsRead));

            // Act - Clear history
            service.ClearHistory();
            Assert.Empty(service.ActiveToasts);
            Assert.Empty(service.History);
            Assert.Equal(0, service.UnreadCount);
        }

        [Fact]
        public void NotificationService_ConcurrentAccess_IsThreadSafe()
        {
            // Arrange
            using var service = new NotificationService(defaultDurationSeconds: 5);
            const int threadCount = 20;

            // Act: Concurrent tasks pushing notifications simultaneously
            Parallel.For(0, threadCount, i =>
            {
                service.Show(
                    i % 2 == 0 ? NotificationType.Info : NotificationType.Success,
                    $"Concurrent {i}",
                    $"Details {i}");
            });

            // Assert: All 20 items in history, ActiveToasts capped safely
            Assert.Equal(threadCount, service.History.Count);
            Assert.True(service.ActiveToasts.Count <= 4);
        }

        [Fact]
        public void NotificationsViewModel_FilteringByLevel_FiltersProperly()
        {
            // Arrange
            using var service = new NotificationService();
            var vm = new NotificationsViewModel(service);

            service.ShowSuccess("Success 1", "Done");
            service.ShowInfo("Info 1", "Note");
            service.ShowWarning("Warn 1", "Caution");
            service.ShowError("Error 1", "Failure");

            Assert.Equal(4, service.History.Count);
            Assert.Equal("ALL", vm.CurrentLevelFilter);
            Assert.True(vm.HasNotifications);

            // Act & Assert - Filter SUCCESS
            vm.FilterLevelCommand.Execute("SUCCESS");
            Assert.Equal("SUCCESS", vm.CurrentLevelFilter);
            var successList = vm.NotificationsView.Cast<NotificationItem>().ToList();
            Assert.Single(successList);
            Assert.Equal(NotificationType.Success, successList[0].Type);

            // Act & Assert - Filter ERROR
            vm.FilterLevelCommand.Execute("ERROR");
            var errorList = vm.NotificationsView.Cast<NotificationItem>().ToList();
            Assert.Single(errorList);
            Assert.Equal(NotificationType.Error, errorList[0].Type);

            // Act & Assert - Filter ALL
            vm.FilterLevelCommand.Execute("ALL");
            var allList = vm.NotificationsView.Cast<NotificationItem>().ToList();
            Assert.Equal(4, allList.Count);

            // Act - Clear All
            vm.ClearAllCommand.Execute(null);
            Assert.Empty(service.History);
            Assert.False(vm.HasNotifications);
        }

        [Fact]
        public void SettingsViewModel_NotificationDuration_RoundtripsThroughConfig()
        {
            // Arrange
            using var rig = new Host.HostRig();
            using var notifService = new NotificationService();
            var configService = rig.Config;

            var vm = new SettingsViewModel(configService, new RevitAgent.Core.Policy.GatewayPolicy(), rig.Paths, rig.Log, notificationService: notifService);

            // Default is 3
            Assert.Equal(3, vm.NotificationDurationSeconds);
            Assert.Equal(3, notifService.DefaultDurationSeconds);

            // Act - Change duration to 7 seconds and save
            vm.NotificationDurationSeconds = 7;
            vm.SaveConfigCommand.Execute(null);

            // Assert: Service updated and SaveConfig pushed a success toast
            Assert.Equal(7, notifService.DefaultDurationSeconds);
            Assert.Single(notifService.ActiveToasts);

            // Act - Load from disk
            var loaded = configService.LoadConfig();
            Assert.Equal(7, loaded.NotificationDurationSeconds);

            // Act - Test toast command (adds 2nd toast)
            vm.TestToastCommand.Execute("SUCCESS");
            Assert.Equal(2, notifService.ActiveToasts.Count);
            Assert.Equal(NotificationType.Success, notifService.ActiveToasts[1].Type);
        }
    }
}
