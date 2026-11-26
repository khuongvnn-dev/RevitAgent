using System.Net;
using System.Net.Http;
using System.Text.Json;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Tools;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class TelegramClientTests
{
    private const string SecretToken = "123456789:ABCdefGHIjklMNOpqrsTUVwxyz12345";
    private const long ChatId = 987654321;

    [Fact]
    public async Task SendPhotoAsync_BuildsMultipartWithChatIdCaptionAndKeyboard()
    {
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return FakeHttpMessageHandler.OkJson(new
            {
                ok = true,
                result = new { message_id = 42, date = 1234567890 }
            });
        });

        using var client = new TelegramClient(SecretToken, fakeHandler);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new InlineKeyboardButton("Option 1", "cb_1"),
            new InlineKeyboardButton("Option 2", "cb_2")
        });
        byte[] dummyPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var response = await client.SendPhotoAsync(
            ChatId,
            dummyPng,
            fileName: "screenshot.png",
            caption: "Look at this Revit dialog",
            replyMarkup: keyboard);

        Assert.True(response.Ok);
        Assert.Equal(42, response.Result!.MessageId);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Contains($"/bot{SecretToken}/sendPhoto", capturedRequest.RequestUri!.ToString());

        var multipart = Assert.IsAssignableFrom<MultipartFormDataContent>(capturedRequest.Content);
        var parts = new Dictionary<string, string>();
        foreach (var part in multipart)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (name != null)
            {
                parts[name] = await part.ReadAsStringAsync();
            }
        }

        Assert.Equal(ChatId.ToString(), parts["chat_id"]);
        Assert.Equal("Look at this Revit dialog", parts["caption"]);
        Assert.Equal("HTML", parts["parse_mode"]);

        // Validate inline keyboard JSON
        Assert.True(parts.ContainsKey("reply_markup"));
        var parsedKeyboard = JsonSerializer.Deserialize<InlineKeyboardMarkup>(parts["reply_markup"], GatewayJson.Options);
        Assert.NotNull(parsedKeyboard);
        Assert.Single(parsedKeyboard.InlineKeyboard);
        Assert.Equal(2, parsedKeyboard.InlineKeyboard[0].Count);
        Assert.Equal("Option 1", parsedKeyboard.InlineKeyboard[0][0].Text);
        Assert.Equal("cb_1", parsedKeyboard.InlineKeyboard[0][0].CallbackData);
    }

    [Fact]
    public async Task SendMessageAsync_BuildsJsonWithChatIdAndKeyboard()
    {
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return FakeHttpMessageHandler.OkJson(new
            {
                ok = true,
                result = new { message_id = 99, date = 1234567890 }
            });
        });

        using var client = new TelegramClient(SecretToken, fakeHandler);
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new InlineKeyboardButton("Agree", "q:yes"),
            new InlineKeyboardButton("Cancel", "q:no")
        });

        var response = await client.SendMessageAsync(
            ChatId,
            "Please confirm action",
            replyMarkup: keyboard);

        Assert.True(response.Ok);
        Assert.NotNull(capturedRequest);
        Assert.Contains($"/bot{SecretToken}/sendMessage", capturedRequest.RequestUri!.ToString());

        var json = await capturedRequest.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(ChatId, root.GetProperty("chat_id").GetInt64());
        Assert.Equal("Please confirm action", root.GetProperty("text").GetString());
        Assert.True(root.TryGetProperty("reply_markup", out var rm));
        Assert.True(rm.TryGetProperty("inline_keyboard", out var ik));
        Assert.Equal(1, ik.GetArrayLength());
        Assert.Equal(2, ik[0].GetArrayLength());
    }

    [Fact]
    public void TokenRedaction_RedactsTokenFromExceptionsAndUrls()
    {
        string textWithToken = $"Error connecting to https://api.telegram.org/bot{SecretToken}/sendMessage: 401";
        string redacted = TelegramClient.Redact(textWithToken, SecretToken);

        Assert.DoesNotContain(SecretToken, redacted);
        Assert.Contains("[REDACTED]", redacted);

        // Also test general token pattern redaction
        string arbitraryToken = "https://api.telegram.org/bot998877:XYZ-abc_123/getUpdates";
        string generalRedacted = TelegramClient.Redact(arbitraryToken);
        Assert.DoesNotContain("998877:XYZ-abc_123", generalRedacted);
        Assert.Contains("bot[REDACTED]", generalRedacted);
    }

    [Fact]
    public async Task TokenRedaction_WhenApiFails_ExceptionDoesNotContainToken()
    {
        var fakeHandler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, $"Unauthorized for bot{SecretToken}"));

        using var client = new TelegramClient(SecretToken, fakeHandler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendMessageAsync(ChatId, "Test message"));

        Assert.DoesNotContain(SecretToken, ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    [Fact]
    public async Task EditMessageReplyMarkup_RemovesButtons()
    {
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return FakeHttpMessageHandler.OkJson(new
            {
                ok = true,
                result = new { message_id = 42 }
            });
        });

        using var client = new TelegramClient(SecretToken, fakeHandler);
        var ok = await client.EditMessageReplyMarkupAsync(ChatId, 42, replyMarkup: null);

        Assert.True(ok);
        Assert.NotNull(capturedRequest);
        Assert.Contains("editMessageReplyMarkup", capturedRequest.RequestUri!.ToString());

        var json = await capturedRequest.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("message_id").GetInt32());
        Assert.Equal(ChatId, doc.RootElement.GetProperty("chat_id").GetInt64());
        var rm = doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
        Assert.Equal(0, rm.GetArrayLength());
    }

    [Fact]
    public async Task AnswerCallbackQuery_SendsCallbackQueryId()
    {
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
        });

        using var client = new TelegramClient(SecretToken, fakeHandler);
        var ok = await client.AnswerCallbackQueryAsync("cb_query_123", "Recorded");

        Assert.True(ok);
        Assert.NotNull(capturedRequest);
        Assert.Contains("answerCallbackQuery", capturedRequest.RequestUri!.ToString());

        var json = await capturedRequest.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("cb_query_123", doc.RootElement.GetProperty("callback_query_id").GetString());
        Assert.Equal("Recorded", doc.RootElement.GetProperty("text").GetString());
    }
}
