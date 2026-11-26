namespace RevitAgent.Core.Notify;

/// <summary>
/// Configuration options for the Telegram bot notification and human escalation channel.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Bot token issued by @BotFather. Secret; never logged or saved in plain text.</summary>
    public string? BotToken { get; set; }

    /// <summary>Owner's Telegram chat id (only this chat may interact with the bot).</summary>
    public long ChatId { get; set; }

    /// <summary>Timeout in minutes before falling back to the safe option (default 30 min).</summary>
    public int AskTimeoutMinutes { get; set; } = 30;

    /// <summary>Whether Telegram notifications and human escalation are enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>True when bot token and chat id are both set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && ChatId != 0;
}
