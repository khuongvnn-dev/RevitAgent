using System.IO;
using System.Security.Cryptography;
using System.Text;
using RevitAgent.Host.Config;
using Xunit;

namespace RevitAgent.Tests;

[Trait("Category", "Config")]
public sealed class EncryptedConfigTests : IDisposable
{
    private readonly string _tempDirectory;

    public EncryptedConfigTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "RevitAgent_ConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public void Constructor_DefaultPath_UsesLocalAppData()
    {
        var manager = new EncryptedConfigManager();
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitAgent",
            "appsettings.enc");

        Assert.Equal(expected, manager.ConfigFilePath);
    }

    [Fact]
    public void LoadConfig_FileDoesNotExist_ReturnsDefaultConfig()
    {
        string nonExistentPath = Path.Combine(_tempDirectory, "nonexistent.enc");
        var manager = new EncryptedConfigManager(nonExistentPath);

        Assert.Equal(nonExistentPath, manager.ConfigFilePath);
        Assert.False(manager.ConfigFileExists);

        var config = manager.LoadConfig();

        Assert.NotNull(config);
        Assert.Equal("Admin", config.GatewayMaxPermission);
        Assert.True(config.GatewayEnforceSandbox);
        Assert.Equal("revitagent-mcp", config.IpcPipeName);
    }

    [Fact]
    public void SaveConfig_AndLoadConfig_PreservesAllFields()
    {
        string filePath = Path.Combine(_tempDirectory, "appsettings.enc");
        var manager = new EncryptedConfigManager(filePath);

        var original = new AppConfig
        {
            Language = "en-US",
            ThemeMode = "Dark",
            NotificationDurationSeconds = 9,
            GatewayMaxPermission = "Write",
            GatewayEnforceSandbox = false,
            IpcPipeName = "custom-pipe-99"
        };

        manager.SaveConfig(original);

        Assert.True(manager.ConfigFileExists);
        Assert.True(File.Exists(filePath));

        // Verify that raw bytes on disk are protected and do NOT contain plain-text secrets
        string rawContent = File.ReadAllText(filePath);
        Assert.DoesNotContain("custom-pipe-99", rawContent);

        // Load and assert all fields are accurately preserved
        var loaded = manager.LoadConfig();
        Assert.NotNull(loaded);
        Assert.Equal(original.Language, loaded.Language);
        Assert.Equal(original.ThemeMode, loaded.ThemeMode);
        Assert.Equal(original.NotificationDurationSeconds, loaded.NotificationDurationSeconds);
        Assert.Equal(original.GatewayMaxPermission, loaded.GatewayMaxPermission);
        Assert.Equal(original.GatewayEnforceSandbox, loaded.GatewayEnforceSandbox);
        Assert.Equal(original.IpcPipeName, loaded.IpcPipeName);
    }

    [Theory]
    [InlineData("PlainText")]
    [InlineData("JsonPlain")]
    [InlineData("CorruptedBytes")]
    public void LoadConfig_WhenFileTampered_ThrowsCryptographicException(string scenario)
    {
        string filePath = Path.Combine(_tempDirectory, $"tampered_{Guid.NewGuid():N}.enc");
        var manager = new EncryptedConfigManager(filePath);

        if (scenario == "CorruptedBytes")
        {
            byte[] corruptBytes = new byte[64];
            Random.Shared.NextBytes(corruptBytes);
            File.WriteAllBytes(filePath, corruptBytes);
        }
        else if (scenario == "JsonPlain")
        {
            File.WriteAllText(filePath, "{\"RevitExecutablePath\":\"test\"}");
        }
        else
        {
            File.WriteAllText(filePath, "PlainTextNotEncryptedAtAll");
        }

        Assert.Throws<CryptographicException>(() => manager.LoadConfig());
    }

    [Fact]
    public void LoadConfig_WhenEncryptedWithDifferentEntropy_ThrowsCryptographicException()
    {
        string filePath = Path.Combine(_tempDirectory, "wrong_entropy.enc");
        var manager = new EncryptedConfigManager(filePath);

        byte[] wrongEntropy = Encoding.UTF8.GetBytes("WrongEntropySalt");
        byte[] plainBytes = Encoding.UTF8.GetBytes("{\"IpcPipeName\":\"test\"}");
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, wrongEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(filePath, encryptedBytes);

        Assert.Throws<CryptographicException>(() => manager.LoadConfig());
    }

    [Fact]
    public void LoadConfig_WhenEncryptedJsonIsCorrupt_ThrowsCryptographicException()
    {
        string filePath = Path.Combine(_tempDirectory, "corrupt_json.enc");
        var manager = new EncryptedConfigManager(filePath);

        byte[] correctEntropy = Encoding.UTF8.GetBytes("RevitAgent::DPAPI::EntropySalt::v1");
        byte[] invalidJsonBytes = Encoding.UTF8.GetBytes("{ Invalid JSON: not-a-json-payload ]]]");
        byte[] encryptedBytes = ProtectedData.Protect(invalidJsonBytes, correctEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(filePath, encryptedBytes);

        Assert.Throws<CryptographicException>(() => manager.LoadConfig());
    }

    [Fact]
    public void LoadConfig_V1ConfigWithRemovedFields_StillLoadsAndKeepsTheKnownOnes()
    {
        // A config written by the v1 Host: Telegram, Revit path, sandbox and attach settings no longer exist in AppConfig.
        string filePath = Path.Combine(_tempDirectory, "v1_appsettings.enc");
        string v1Json = """
            {
              "Language": "en-US",
              "ThemeMode": "Light",
              "NotificationDurationSeconds": 5,
              "RevitExecutablePath": "C:\\Program Files\\Autodesk\\Revit 2024\\Revit.exe",
              "TestModelsDirectory": "%LOCALAPPDATA%\\RevitAgent\\models",
              "SandboxBaseDirectory": "%LOCALAPPDATA%\\RevitAgent\\runs",
              "MaxQuarantinedModels": 7,
              "TelegramBotToken": "123:secret",
              "TelegramAdminChatId": 42,
              "AutoAttach": false,
              "IpcPipeName": "legacy-pipe"
            }
            """;
        byte[] entropy = Encoding.UTF8.GetBytes("RevitAgent::DPAPI::EntropySalt::v1");
        File.WriteAllBytes(filePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(v1Json), entropy, DataProtectionScope.CurrentUser));

        var config = new EncryptedConfigManager(filePath).LoadConfig();

        Assert.Equal("en-US", config.Language);
        Assert.Equal("Light", config.ThemeMode);
        Assert.Equal(5, config.NotificationDurationSeconds);
        Assert.Equal("legacy-pipe", config.IpcPipeName);
        // v2 policy fields fall back to their defaults
        Assert.Equal("Admin", config.GatewayMaxPermission);
        Assert.True(config.GatewayEnforceSandbox);

        // Saving again — even from a brand-new AppConfig, as the Settings page does — keeps every field this version
        // does not know: nothing in the user's configuration is dropped (live finding 2026-11-25: v1 token lost).
        var manager = new EncryptedConfigManager(filePath);
        manager.SaveConfig(new AppConfig { Language = "vi-VN", ThemeMode = "Dark" });
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(filePath), entropy, DataProtectionScope.CurrentUser);
        string rewritten = Encoding.UTF8.GetString(plain);
        Assert.Contains("\"TelegramBotToken\": \"123:secret\"", rewritten);
        Assert.Contains("\"TelegramAdminChatId\": 42", rewritten);
        Assert.Contains("\"AutoAttach\": false", rewritten);
        Assert.Contains("\"ThemeMode\": \"Dark\"", rewritten);   // known fields take the new values
        Assert.Contains("\"Language\": \"vi-VN\"", rewritten);
        var reloaded = manager.LoadConfig();
        Assert.Equal("Dark", reloaded.ThemeMode);
    }

    [Fact]
    public void LoadConfig_ViewModelOverV1Config_LoadsWithoutErrors()
    {
        string filePath = Path.Combine(_tempDirectory, "v1_vm.enc");
        byte[] entropy = Encoding.UTF8.GetBytes("RevitAgent::DPAPI::EntropySalt::v1");
        string v1Json = "{\"Language\":\"en-US\",\"TelegramBotToken\":\"x:y\",\"RevitExecutablePath\":\"C:\\\\Old\\\\Revit.exe\",\"AutoAttach\":true}";
        File.WriteAllBytes(filePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(v1Json), entropy, DataProtectionScope.CurrentUser));
        var service = new RevitAgent.Host.Services.EncryptedConfigService(new EncryptedConfigManager(filePath));
        using var rig = new RevitAgent.Tests.Host.HostRig();

        var vm = new RevitAgent.Host.ViewModels.SettingsViewModel(service, new RevitAgent.Core.Policy.GatewayPolicy(), rig.Paths, rig.Log, new RevitAgent.Host.Services.LocalizationService());

        Assert.True(vm.IsConfigStatusSuccess);
        Assert.Equal("Admin", vm.SelectedMaxPermission);
    }
}
