using System.IO;
using System.Net.Http;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class TelegramNotifierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentPaths _paths;
    private readonly AuditLog _auditLog;
    private readonly TelegramConfigStore _configStore;

    private const string BotToken = "123456:FAKE_TOKEN";
    private const long ChatId = 555666777;

    public TelegramNotifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RevitAgent_NotifierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new AgentPaths(_tempDir);
        _auditLog = new AuditLog(_paths);
        _configStore = new TelegramConfigStore(_paths, _auditLog);
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
    public async Task NotifyAsync_WhenDisabledOrUnconfigured_IsNoOp()
    {
        var fakeHandler = new FakeHttpMessageHandler();
        using var client = new TelegramClient(fakeHandler);
        var notifier = new TelegramNotifier(_configStore, client, _auditLog);

        // Case 1: unconfigured (no token, chat_id = 0, enabled = false)
        await notifier.NotifyAsync(NotificationKind.RunResult, "Test result");
        Assert.Empty(fakeHandler.Requests);

        // Case 2: configured but disabled
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ChatId,
            Enabled = false
        });

        await notifier.NotifyAsync(NotificationKind.RevitHang, "Revit is hanging");
        Assert.Empty(fakeHandler.Requests);
    }

    [Fact]
    public async Task NotifyAsync_WhenEnabled_SendsMessageWithHeaderAndKind()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ChatId,
            Enabled = true
        });

        var fakeHandler = new FakeHttpMessageHandler(req =>
            FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 1 } }));

        using var client = new TelegramClient(fakeHandler);
        var notifier = new TelegramNotifier(_configStore, client, _auditLog);

        await notifier.NotifyAsync(NotificationKind.RevitHang, "Process 1234 is unresponsive");

        Assert.Single(fakeHandler.Requests);
        var req = fakeHandler.Requests[0];
        Assert.Contains("sendMessage", req.RequestUri!.ToString());

        var json = await req.Content!.ReadAsStringAsync();
        Assert.Contains(ChatId.ToString(), json);
        Assert.Contains("RevitHang", json);
        Assert.Contains("Process 1234 is unresponsive", json);

        // Audit entry recorded
        var audits = _auditLog.Recent();
        Assert.Contains(audits, a => a.Tool == "telegram_notify" && a.Ok);
    }

    [Fact]
    public async Task NotifyAsync_WithImages_SendsPhotos()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ChatId,
            Enabled = true
        });

        var fakeHandler = new FakeHttpMessageHandler(req =>
            FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 2 } }));

        using var client = new TelegramClient(fakeHandler);
        var notifier = new TelegramNotifier(_configStore, client, _auditLog);

        var imagePath = Path.Combine(_tempDir, "sample.png");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });

        await notifier.NotifyAsync(NotificationKind.AddinBlocked, "Blocked add-in popup", new string[] { imagePath });

        Assert.Single(fakeHandler.Requests);
        var req = fakeHandler.Requests[0];
        Assert.Contains("sendPhoto", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task NotifyAsync_RateLimit_DropsExcessMessages()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ChatId,
            Enabled = true
        });

        var fakeHandler = new FakeHttpMessageHandler(req =>
            FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 1 } }));

        using var client = new TelegramClient(fakeHandler);
        var notifier = new TelegramNotifier(_configStore, client, _auditLog);

        // Send 25 messages rapidly
        for (int i = 0; i < 25; i++)
        {
            await notifier.NotifyAsync(NotificationKind.RunResult, $"Run message #{i}");
        }

        // Only 20 should have been sent over HTTP
        Assert.Equal(20, fakeHandler.Requests.Count);

        // 5 should be audited as rate limited
        var audits = _auditLog.Recent();
        var rateLimitedAudits = audits.Where(a => a.ErrorCode == "RATE_LIMITED").ToList();
        Assert.Equal(5, rateLimitedAudits.Count);
    }
}
