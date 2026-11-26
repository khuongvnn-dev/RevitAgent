using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Lightweight Telegram Bot API client over <see cref="HttpClient"/>.
/// Guarantees that bot tokens are never logged or exposed in exceptions or message URLs.
/// </summary>
public sealed class TelegramClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private string? _defaultBotToken;

    private static readonly Regex TokenPattern = new(@"bot\d+:[A-Za-z0-9_-]+", RegexOptions.Compiled);

    public TelegramClient(HttpMessageHandler? handler = null)
    {
        if (handler != null)
        {
            _httpClient = new HttpClient(handler, disposeHandler: false);
            _ownsClient = true;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsClient = true;
        }
    }

    public TelegramClient(string botToken, HttpMessageHandler? handler = null)
        : this(handler)
    {
        _defaultBotToken = botToken;
    }

    public TelegramClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = false;
    }

    public string? DefaultBotToken
    {
        get => _defaultBotToken;
        set => _defaultBotToken = value;
    }

    /// <summary>
    /// Redacts bot tokens from URLs, messages and stack traces.
    /// </summary>
    public static string Redact(string? text, string? token = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        if (!string.IsNullOrEmpty(token))
        {
            result = result.Replace(token, "[REDACTED]");
        }

        result = TokenPattern.Replace(result, "bot[REDACTED]");
        return result;
    }

    private string ResolveToken(string? token)
    {
        var resolved = !string.IsNullOrWhiteSpace(token) ? token : _defaultBotToken;
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException("Telegram bot token is not configured.");
        }
        return resolved;
    }

    private string BuildApiUrl(string token, string method) =>
        $"https://api.telegram.org/bot{token}/{method}";

    public Task<TelegramResponse<TelegramMessage>> SendMessageAsync(
        long chatId,
        string text,
        InlineKeyboardMarkup? replyMarkup = null,
        string? parseMode = "HTML",
        CancellationToken ct = default) =>
        SendMessageAsync(ResolveToken(null), chatId, text, replyMarkup, parseMode, ct);

    public async Task<TelegramResponse<TelegramMessage>> SendMessageAsync(
        string botToken,
        long chatId,
        string text,
        InlineKeyboardMarkup? replyMarkup = null,
        string? parseMode = "HTML",
        CancellationToken ct = default)
    {
        var token = ResolveToken(botToken);
        var url = BuildApiUrl(token, "sendMessage");

        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = text
        };

        if (!string.IsNullOrEmpty(parseMode))
        {
            payload["parse_mode"] = parseMode;
        }

        if (replyMarkup != null)
        {
            payload["reply_markup"] = replyMarkup;
        }

        return await PostJsonAsync<TelegramMessage>(url, payload, token, ct);
    }

    public Task<TelegramResponse<TelegramMessage>> SendPhotoAsync(
        long chatId,
        byte[] photoBytes,
        string fileName = "photo.png",
        string? caption = null,
        InlineKeyboardMarkup? replyMarkup = null,
        string? parseMode = "HTML",
        CancellationToken ct = default) =>
        SendPhotoAsync(ResolveToken(null), chatId, photoBytes, fileName, caption, replyMarkup, parseMode, ct);

    public async Task<TelegramResponse<TelegramMessage>> SendPhotoAsync(
        string botToken,
        long chatId,
        byte[] photoBytes,
        string fileName = "photo.png",
        string? caption = null,
        InlineKeyboardMarkup? replyMarkup = null,
        string? parseMode = "HTML",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photoBytes);
        var token = ResolveToken(botToken);
        var url = BuildApiUrl(token, "sendPhoto");

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString()), "chat_id");

        var fileContent = new ByteArrayContent(photoBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "photo", string.IsNullOrWhiteSpace(fileName) ? "photo.png" : fileName);

        if (!string.IsNullOrEmpty(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }

        if (!string.IsNullOrEmpty(parseMode))
        {
            form.Add(new StringContent(parseMode), "parse_mode");
        }

        if (replyMarkup != null)
        {
            var keyboardJson = JsonSerializer.Serialize(replyMarkup, GatewayJson.Options);
            form.Add(new StringContent(keyboardJson), "reply_markup");
        }

        try
        {
            using var response = await _httpClient.PostAsync(url, form, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TelegramResponse<TelegramMessage>>(responseContent, GatewayJson.Options)
                         ?? throw new InvalidOperationException("Empty response received from Telegram.");

            if (!result.Ok)
            {
                throw new InvalidOperationException($"Telegram API error: {Redact(result.Description, token)}");
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Redact(ex.Message, token), ex is HttpRequestException ? null : ex);
        }
    }

    public Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long? offset,
        int timeoutSec = 0,
        CancellationToken ct = default) =>
        GetUpdatesAsync(ResolveToken(null), offset, timeoutSec, ct);

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        string botToken,
        long? offset,
        int timeoutSec = 0,
        CancellationToken ct = default)
    {
        var token = ResolveToken(botToken);
        var url = BuildApiUrl(token, "getUpdates");

        var payload = new Dictionary<string, object?>
        {
            ["timeout"] = timeoutSec
        };

        if (offset.HasValue)
        {
            payload["offset"] = offset.Value;
        }

        var response = await PostJsonAsync<List<TelegramUpdate>>(url, payload, token, ct);
        return (IReadOnlyList<TelegramUpdate>?)response.Result ?? Array.Empty<TelegramUpdate>();
    }

    public async Task<bool> AnswerCallbackQueryAsync(
        string callbackQueryId,
        string? text = null,
        string? botToken = null,
        CancellationToken ct = default)
    {
        var token = ResolveToken(botToken);
        var url = BuildApiUrl(token, "answerCallbackQuery");

        var payload = new Dictionary<string, object?>
        {
            ["callback_query_id"] = callbackQueryId
        };

        if (!string.IsNullOrEmpty(text))
        {
            payload["text"] = text;
        }

        var response = await PostJsonAsync<bool>(url, payload, token, ct);
        return response.Ok;
    }

    public Task<bool> EditMessageReplyMarkupAsync(
        long chatId,
        int messageId,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default) =>
        EditMessageReplyMarkupAsync(ResolveToken(null), chatId, messageId, replyMarkup, ct);

    public async Task<bool> EditMessageReplyMarkupAsync(
        string botToken,
        long chatId,
        int messageId,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        var token = ResolveToken(botToken);
        var url = BuildApiUrl(token, "editMessageReplyMarkup");

        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["reply_markup"] = replyMarkup ?? new InlineKeyboardMarkup()
        };

        var response = await PostJsonAsync<TelegramMessage>(url, payload, token, ct);
        return response.Ok;
    }

    private async Task<TelegramResponse<T>> PostJsonAsync<T>(
        string url,
        object payload,
        string token,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, GatewayJson.Options);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.PostAsync(url, content, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TelegramResponse<T>>(responseContent, GatewayJson.Options)
                         ?? throw new InvalidOperationException("Empty response received from Telegram.");

            if (!result.Ok)
            {
                throw new InvalidOperationException($"Telegram API error: {Redact(result.Description, token)}");
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Redact(ex.Message, token), ex is HttpRequestException ? null : ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
