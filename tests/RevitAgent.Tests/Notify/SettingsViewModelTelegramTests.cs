using RevitAgent.Core.Notify;
using RevitAgent.Host.ViewModels;
using RevitAgent.Tests.Host;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class SettingsViewModelTelegramTests
{
    [Fact]
    public void LoadConfig_PopulatesTelegramPropertiesFromConfigStore()
    {
        using var rig = new HostRig();
        var store = new TelegramConfigStore(rig.Paths, rig.Provider.GetService(typeof(RevitAgent.Core.Audit.AuditLog)) as RevitAgent.Core.Audit.AuditLog);
        store.Save(new TelegramOptions
        {
            BotToken = "123:saved_token",
            ChatId = 778899,
            AskTimeoutMinutes = 40,
            Enabled = true
        });

        var vm = new SettingsViewModel(
            rig.Config,
            rig.Policy,
            rig.Paths,
            rig.Log,
            new RevitAgent.Host.Services.LocalizationService(),
            null,
            rig.Notifications,
            store);

        Assert.Equal("123:saved_token", vm.TelegramBotToken);
        Assert.Equal(778899, vm.TelegramChatId);
        Assert.Equal(40, vm.TelegramTimeoutMinutes);
        Assert.True(vm.TelegramEnabled);
    }

    [Fact]
    public void SaveConfig_PersistsTelegramPropertiesToConfigStore()
    {
        using var rig = new HostRig();
        var store = new TelegramConfigStore(rig.Paths);
        var vm = new SettingsViewModel(
            rig.Config,
            rig.Policy,
            rig.Paths,
            rig.Log,
            new RevitAgent.Host.Services.LocalizationService(),
            null,
            rig.Notifications,
            store);

        vm.TelegramBotToken = "555:new_token";
        vm.TelegramChatId = 998877;
        vm.TelegramTimeoutMinutes = 25;
        vm.TelegramEnabled = true;

        vm.SaveConfigCommand.Execute(null);

        // Verify that store on disk reflects the updated settings
        var freshStore = new TelegramConfigStore(rig.Paths);
        var loaded = freshStore.Load();

        Assert.Equal("555:new_token", loaded.BotToken);
        Assert.Equal(998877, loaded.ChatId);
        Assert.Equal(25, loaded.AskTimeoutMinutes);
        Assert.True(loaded.Enabled);
    }

    [Fact]
    public async Task TestTelegramCommand_WhenUnconfigured_SetsErrorStatus()
    {
        using var rig = new HostRig();
        var vm = rig.NewSettings();

        vm.TelegramBotToken = "";
        vm.TelegramChatId = 0;

        await vm.TestTelegramCommand.ExecuteAsync(null);

        Assert.False(vm.IsTelegramTestSuccess);
        Assert.NotEmpty(vm.TelegramTestStatus);
    }

    [Fact]
    public async Task TestTelegramCommand_WhenConfigured_SendsMessageAndSetsSuccess()
    {
        using var rig = new HostRig();
        var fakeHandler = new FakeHttpMessageHandler(req =>
            FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 999 } }));

        var client = new TelegramClient(fakeHandler);
        var store = new TelegramConfigStore(rig.Paths);

        var vm = new SettingsViewModel(
            rig.Config,
            rig.Policy,
            rig.Paths,
            rig.Log,
            new RevitAgent.Host.Services.LocalizationService(),
            null,
            rig.Notifications,
            store,
            client);

        vm.TelegramBotToken = "111:valid_token";
        vm.TelegramChatId = 123456;

        await vm.TestTelegramCommand.ExecuteAsync(null);

        Assert.True(vm.IsTelegramTestSuccess);
        Assert.NotEmpty(vm.TelegramTestStatus);
        Assert.Single(fakeHandler.Requests);
        Assert.Contains("sendMessage", fakeHandler.Requests[0].RequestUri!.ToString());
    }
}
