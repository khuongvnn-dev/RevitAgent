using System.IO;
using System.Net.Http;
using System.Text.Json;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Tools;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class TelegramHumanEscalationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentPaths _paths;
    private readonly AuditLog _auditLog;
    private readonly TelegramConfigStore _configStore;

    private const string BotToken = "555123:ESC_TOKEN";
    private const long ConfiguredChatId = 777888999;

    public TelegramHumanEscalationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RevitAgent_EscalationTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task AskAsync_WhenUnconfiguredOrDisabled_ReturnsSafeOptionImmediately()
    {
        var fakeHandler = new FakeHttpMessageHandler();
        using var client = new TelegramClient(fakeHandler);
        var escalation = new TelegramHumanEscalation(_configStore, client, _auditLog);

        var question = new HumanQuestion(
            Title: "Save changes?",
            Text: "Do you want to save the document?",
            ScreenshotPath: null,
            Options: new[] { "Save", "Discard", "Cancel" },
            SafeOption: "Cancel");

        var answer = await escalation.AskAsync(question);

        Assert.Equal("Cancel", answer.Choice);
        Assert.True(answer.TimedOut);
        Assert.Equal("system", answer.AnsweredBy);
        Assert.Contains("disabled or unconfigured", answer.Reason);
        Assert.Empty(fakeHandler.Requests);
    }

    [Fact]
    public async Task AskAsync_AnswerFlow_ReceivesCallbackAnswersRemovesButtonsAndReturnsChoice()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ConfiguredChatId,
            Enabled = true
        });

        string? capturedQuestionId = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();

            if (uri.Contains("sendMessage") && !uri.Contains("Selected:"))
            {
                // Inspect inline keyboard to extract question id
                var json = req.Content!.ReadAsStringAsync().Result;
                using var doc = JsonDocument.Parse(json);
                var rm = doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
                var cbData = rm[0][0].GetProperty("callback_data").GetString()!; // e.g. "q:1a2b3c4d:0"
                capturedQuestionId = cbData.Split(':')[1];

                return FakeHttpMessageHandler.OkJson(new
                {
                    ok = true,
                    result = new { message_id = 101, chat = new { id = ConfiguredChatId } }
                });
            }

            if (uri.Contains("getUpdates"))
            {
                // Return a callback query with the chosen option index 1 ("Discard")
                return FakeHttpMessageHandler.OkJson(new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            update_id = 1,
                            callback_query = new
                            {
                                id = "cb_query_999",
                                from = new { id = ConfiguredChatId, username = "test_owner" },
                                message = new
                                {
                                    message_id = 101,
                                    chat = new { id = ConfiguredChatId }
                                },
                                data = $"q:{capturedQuestionId}:1" // index 1 = "Discard"
                            }
                        }
                    }
                });
            }

            if (uri.Contains("answerCallbackQuery"))
            {
                return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
            }

            if (uri.Contains("editMessageReplyMarkup"))
            {
                return FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 101 } });
            }

            // confirmation sendMessage
            return FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 102 } });
        });

        using var client = new TelegramClient(fakeHandler);
        var escalation = new TelegramHumanEscalation(_configStore, client, _auditLog);

        var question = new HumanQuestion(
            Title: "Security dialog",
            Text: "An unknown addin wants to load",
            ScreenshotPath: null,
            Options: new[] { "Load Once", "Discard", "Do Not Load" },
            SafeOption: "Do Not Load",
            Timeout: TimeSpan.FromSeconds(5));

        var answer = await escalation.AskAsync(question);

        Assert.Equal("Discard", answer.Choice);
        Assert.False(answer.TimedOut);
        Assert.Equal("test_owner", answer.AnsweredBy);

        // Verify that answerCallbackQuery, editMessageReplyMarkup, and confirmation were sent
        Assert.Contains(fakeHandler.Requests, r => r.RequestUri!.ToString().Contains("answerCallbackQuery"));
        Assert.Contains(fakeHandler.Requests, r => r.RequestUri!.ToString().Contains("editMessageReplyMarkup"));
        Assert.Contains(fakeHandler.Requests, r => r.RequestUri!.ToString().Contains("sendMessage") &&
                                                   r.Content!.ReadAsStringAsync().Result.Contains("Selected: Discard"));
    }

    [Fact]
    public async Task AskAsync_ChatIdFiltering_IgnoresUpdatesFromOtherChatIds()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ConfiguredChatId,
            Enabled = true
        });

        string? capturedQuestionId = null;
        int pollCount = 0;

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();

            if (uri.Contains("sendMessage") && !uri.Contains("Đã chọn"))
            {
                var json = req.Content!.ReadAsStringAsync().Result;
                using var doc = JsonDocument.Parse(json);
                var rm = doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
                var cbData = rm[0][0].GetProperty("callback_data").GetString()!;
                capturedQuestionId = cbData.Split(':')[1];

                return FakeHttpMessageHandler.OkJson(new
                {
                    ok = true,
                    result = new { message_id = 201, chat = new { id = ConfiguredChatId } }
                });
            }

            if (uri.Contains("getUpdates"))
            {
                pollCount++;
                if (pollCount == 1)
                {
                    // 1st poll: update from unauthorized hacker chat ID 999999
                    return FakeHttpMessageHandler.OkJson(new
                    {
                        ok = true,
                        result = new[]
                        {
                            new
                            {
                                update_id = 10,
                                callback_query = new
                                {
                                    id = "cb_unauthorized",
                                    from = new { id = 999999L, username = "hacker" },
                                    message = new
                                    {
                                        message_id = 201,
                                        chat = new { id = 999999L }
                                    },
                                    data = $"q:{capturedQuestionId}:0"
                                }
                            }
                        }
                    });
                }

                // 2nd poll: update from legitimate owner chat ID ConfiguredChatId
                return FakeHttpMessageHandler.OkJson(new
                {
                    ok = true,
                    result = new[]
                    {
                        new
                        {
                            update_id = 11,
                            callback_query = new
                            {
                                id = "cb_owner",
                                from = new { id = ConfiguredChatId, username = "legit_owner" },
                                message = new
                                {
                                    message_id = 201,
                                    chat = new { id = ConfiguredChatId }
                                },
                                data = $"q:{capturedQuestionId}:2" // index 2 = "Do Not Load"
                            }
                        }
                    }
                });
            }

            return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
        });

        using var client = new TelegramClient(fakeHandler);
        var escalation = new TelegramHumanEscalation(_configStore, client, _auditLog);

        var question = new HumanQuestion(
            Title: "Add-in warning",
            Text: "Allow this add-in?",
            ScreenshotPath: null,
            Options: new[] { "Load Once", "Always Load", "Do Not Load" },
            SafeOption: "Do Not Load",
            Timeout: TimeSpan.FromSeconds(5));

        var answer = await escalation.AskAsync(question);

        // Assert that the unauthorized answer was NOT accepted, and legit owner's answer was accepted
        Assert.Equal("Do Not Load", answer.Choice);
        Assert.Equal("legit_owner", answer.AnsweredBy);
        Assert.False(answer.TimedOut);

        // Check audit log for rejection entry
        var audits = _auditLog.Recent();
        Assert.Contains(audits, a => a.ErrorCode == "UNAUTHORIZED_CHAT");
    }

    [Fact]
    public async Task AskAsync_Timeout_ReturnsSafeOptionAndRemovesButtons()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = BotToken,
            ChatId = ConfiguredChatId,
            Enabled = true
        });

        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("sendMessage") || uri.Contains("editMessageReplyMarkup"))
            {
                return FakeHttpMessageHandler.OkJson(new
                {
                    ok = true,
                    result = new { message_id = 301, chat = new { id = ConfiguredChatId } }
                });
            }

            if (uri.Contains("getUpdates"))
            {
                // Return empty updates until timeout
                return FakeHttpMessageHandler.OkJson(new
                {
                    ok = true,
                    result = Array.Empty<object>()
                });
            }

            return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
        });

        using var client = new TelegramClient(fakeHandler);
        var escalation = new TelegramHumanEscalation(_configStore, client, _auditLog);

        var question = new HumanQuestion(
            Title: "Save model?",
            Text: "Work is pending save.",
            ScreenshotPath: null,
            Options: new[] { "Save", "Discard", "Postpone" },
            SafeOption: "Postpone",
            Timeout: TimeSpan.FromMilliseconds(150)); // Fast timeout for test

        var answer = await escalation.AskAsync(question);

        Assert.Equal("Postpone", answer.Choice);
        Assert.True(answer.TimedOut);
        Assert.Equal("system", answer.AnsweredBy);

        // Buttons removed via editMessageReplyMarkup
        Assert.Contains(fakeHandler.Requests, r => r.RequestUri!.ToString().Contains("editMessageReplyMarkup"));

        // Timeout message sent
        Assert.Contains(fakeHandler.Requests, r =>
            r.RequestUri!.ToString().Contains("sendMessage") &&
            r.Content!.ReadAsStringAsync().Result.Contains("Timed out - safe option chosen: Postpone"));
    }
}
