using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify.Tools;

/// <summary>
/// MCP tool reporting whether Telegram notifications and human escalation are configured and enabled.
/// Never reveals the bot token.
/// </summary>
public sealed class NotifyStatusTool : GatewayTool
{
    private readonly TelegramConfigStore _configStore;

    public NotifyStatusTool(TelegramConfigStore configStore)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
    }

    public override string Name => "notify_status";

    public override string Description =>
        "Check whether Telegram notification and human escalation channel is configured and enabled. Never reveals the bot token.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var options = _configStore.Load();
        return Task.FromResult(ToolResult.Json(new
        {
            enabled = options.Enabled,
            configured = options.IsConfigured,
            chat_id = options.ChatId,
            ask_timeout_minutes = options.AskTimeoutMinutes
        }));
    }
}
