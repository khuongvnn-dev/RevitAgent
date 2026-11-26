using System.Text.Json.Serialization;

namespace RevitAgent.Core.Notify;

public sealed class InlineKeyboardButton
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("callback_data")]
    public string? CallbackData { get; set; }

    public InlineKeyboardButton() { }

    public InlineKeyboardButton(string text, string callbackData)
    {
        Text = text;
        CallbackData = callbackData;
    }
}

public sealed class InlineKeyboardMarkup
{
    [JsonPropertyName("inline_keyboard")]
    public List<List<InlineKeyboardButton>> InlineKeyboard { get; set; } = new();

    public InlineKeyboardMarkup() { }

    public InlineKeyboardMarkup(IEnumerable<InlineKeyboardButton> singleRow)
    {
        InlineKeyboard.Add(singleRow.ToList());
    }

    public InlineKeyboardMarkup(IEnumerable<IEnumerable<InlineKeyboardButton>> rows)
    {
        foreach (var r in rows)
        {
            InlineKeyboard.Add(r.ToList());
        }
    }
}

public sealed class TelegramResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("error_code")]
    public int? ErrorCode { get; set; }
}

public sealed class TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("is_bot")]
    public bool IsBot { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public sealed class TelegramMessage
{
    [JsonPropertyName("message_id")]
    public int MessageId { get; set; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; set; }

    [JsonPropertyName("chat")]
    public TelegramChat? Chat { get; set; }

    [JsonPropertyName("date")]
    public long Date { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

public sealed class TelegramCallbackQuery
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public TelegramUser? From { get; set; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; set; }

    [JsonPropertyName("callback_query")]
    public TelegramCallbackQuery? CallbackQuery { get; set; }
}
