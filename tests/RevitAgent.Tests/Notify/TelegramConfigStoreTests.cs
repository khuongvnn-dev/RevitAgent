using System.IO;
using RevitAgent.Core;
using RevitAgent.Core.Notify;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class TelegramConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentPaths _paths;

    public TelegramConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RevitAgent_TelegramConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new AgentPaths(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }
    }

    [Fact]
    public void Load_WhenFilesDoNotExist_ReturnsDefaults()
    {
        var store = new TelegramConfigStore(_paths);
        var options = store.Load();

        Assert.NotNull(options);
        Assert.Null(options.BotToken);
        Assert.Equal(0, options.ChatId);
        Assert.Equal(30, options.AskTimeoutMinutes);
        Assert.False(options.Enabled);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsPlainConfigAndDpapiToken()
    {
        var store = new TelegramConfigStore(_paths);
        var original = new TelegramOptions
        {
            BotToken = "987654:TEST_SECRET_TOKEN_XYZ",
            ChatId = 1234567890,
            AskTimeoutMinutes = 45,
            Enabled = true
        };

        store.Save(original);

        // 1. Verify plain file exists and DOES NOT contain the secret token
        Assert.True(File.Exists(store.JsonPath));
        var plainContent = File.ReadAllText(store.JsonPath);
        Assert.Contains("1234567890", plainContent);
        Assert.Contains("45", plainContent);
        Assert.DoesNotContain("TEST_SECRET_TOKEN_XYZ", plainContent);
        Assert.DoesNotContain("token", plainContent, StringComparison.OrdinalIgnoreCase);

        // 2. Verify encrypted token file exists
        Assert.True(File.Exists(store.TokenPath));
        var encryptedBytes = File.ReadAllBytes(store.TokenPath);
        var rawEncrypted = System.Text.Encoding.UTF8.GetString(encryptedBytes);
        Assert.DoesNotContain("TEST_SECRET_TOKEN_XYZ", rawEncrypted);

        // 3. Reload from fresh store and verify all fields preserved
        var freshStore = new TelegramConfigStore(_paths);
        var loaded = freshStore.Load();

        Assert.Equal(original.BotToken, loaded.BotToken);
        Assert.Equal(original.ChatId, loaded.ChatId);
        Assert.Equal(original.AskTimeoutMinutes, loaded.AskTimeoutMinutes);
        Assert.True(loaded.Enabled);
        Assert.True(loaded.IsConfigured);
    }

    [Fact]
    public void Save_WhenTokenEmpty_RemovesDatFile()
    {
        var store = new TelegramConfigStore(_paths);

        // First save with token
        store.Save(new TelegramOptions
        {
            BotToken = "some_token",
            ChatId = 112233,
            Enabled = true
        });
        Assert.True(File.Exists(store.TokenPath));

        // Now save with null/empty token
        store.Save(new TelegramOptions
        {
            BotToken = null,
            ChatId = 112233,
            Enabled = true
        });
        Assert.False(File.Exists(store.TokenPath));

        var loaded = store.Load();
        Assert.Null(loaded.BotToken);
        Assert.Equal(112233, loaded.ChatId);
    }
}
