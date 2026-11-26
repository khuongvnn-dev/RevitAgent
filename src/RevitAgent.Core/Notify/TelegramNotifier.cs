using System.Diagnostics;
using System.Text.Encodings.Web;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Sends gateway notifications to the configured Telegram chat with rate limiting and audit logging.
/// </summary>
public sealed class TelegramNotifier : INotifier
{
    private readonly TelegramConfigStore _configStore;
    private readonly TelegramClient _client;
    private readonly AuditLog _auditLog;

    private readonly object _rateGate = new();
    private readonly Queue<DateTimeOffset> _sendTimestamps = new();
    private const int MaxPerMinute = 20;

    public TelegramNotifier(TelegramConfigStore configStore, TelegramClient client, AuditLog auditLog)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    public bool TryAcquireRateLimit()
    {
        lock (_rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddMinutes(-1);
            while (_sendTimestamps.Count > 0 && _sendTimestamps.Peek() < cutoff)
            {
                _sendTimestamps.Dequeue();
            }

            if (_sendTimestamps.Count >= MaxPerMinute)
            {
                return false;
            }

            _sendTimestamps.Enqueue(now);
            return true;
        }
    }

    public async Task NotifyAsync(
        NotificationKind kind,
        string text,
        IReadOnlyList<string>? imagePaths = null,
        CancellationToken ct = default)
    {
        var options = _configStore.Load();
        if (!options.Enabled || !options.IsConfigured)
        {
            // Disabled or unconfigured: silently no-op
            return;
        }

        if (!TryAcquireRateLimit())
        {
            _auditLog.Diagnostic($"Telegram notification dropped: rate limit of {MaxPerMinute} msgs/min exceeded.");
            _auditLog.Record(new AuditEntry(
                At: DateTimeOffset.UtcNow,
                CallId: Guid.NewGuid().ToString("N")[..12],
                SessionId: "telegram",
                Tool: "telegram_notify",
                Permission: ToolPermission.Interact.ToString(),
                Args: AuditLog.SummarizeArgs(GatewayJson.ToElement(new { kind = kind.ToString(), text })),
                Ok: false,
                ErrorCode: "RATE_LIMITED",
                DurationMs: 0,
                Note: "Rate limit exceeded (max 20 msgs / min)"
            ));
            return;
        }

        var sw = Stopwatch.StartNew();
        var header = FormatKindHeader(kind);
        var messageBody = $"{header}\n\n{HtmlEncoder.Default.Encode(text ?? string.Empty)}";
        bool success = false;
        string? error = null;

        try
        {
            if (imagePaths != null && imagePaths.Count > 0)
            {
                var validImages = imagePaths.Where(File.Exists).ToList();
                if (validImages.Count > 0)
                {
                    for (int i = 0; i < validImages.Count; i++)
                    {
                        var bytes = await File.ReadAllBytesAsync(validImages[i], ct);
                        var caption = i == 0 ? messageBody : null;
                        await _client.SendPhotoAsync(
                            options.BotToken!,
                            options.ChatId,
                            bytes,
                            Path.GetFileName(validImages[i]),
                            caption: caption,
                            parseMode: "HTML",
                            ct: ct);
                    }
                }
                else
                {
                    await _client.SendMessageAsync(
                        options.BotToken!,
                        options.ChatId,
                        messageBody,
                        parseMode: "HTML",
                        ct: ct);
                }
            }
            else
            {
                await _client.SendMessageAsync(
                    options.BotToken!,
                    options.ChatId,
                    messageBody,
                    parseMode: "HTML",
                    ct: ct);
            }

            success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = TelegramClient.Redact(ex.Message, options.BotToken);
            _auditLog.Diagnostic($"Telegram notification failed: {error}", ex);
            throw;
        }
        finally
        {
            sw.Stop();
            _auditLog.Record(new AuditEntry(
                At: DateTimeOffset.UtcNow,
                CallId: Guid.NewGuid().ToString("N")[..12],
                SessionId: "telegram",
                Tool: "telegram_notify",
                Permission: ToolPermission.Interact.ToString(),
                Args: AuditLog.SummarizeArgs(GatewayJson.ToElement(new
                {
                    kind = kind.ToString(),
                    chat_id = options.ChatId,
                    images_count = imagePaths?.Count ?? 0,
                    text
                })),
                Ok: success,
                ErrorCode: error != null ? "TELEGRAM_SEND_FAILED" : null,
                DurationMs: sw.ElapsedMilliseconds,
                Note: error
            ));
        }
    }

    private static string FormatKindHeader(NotificationKind kind) => kind switch
    {
        NotificationKind.RunResult => "🏁 <b>[RunResult] Kết quả kịch bản</b>",
        NotificationKind.RevitHang => "⚠️ <b>[RevitHang] Cảnh báo Revit bị treo</b>",
        NotificationKind.RevitCrash => "🔥 <b>[RevitCrash] Revit gặp sự cố/crash</b>",
        NotificationKind.AddinBlocked => "🛡️ <b>[AddinBlocked] Add-in chưa đăng ký bị chặn</b>",
        NotificationKind.AgentNeedsHuman => "🙋 <b>[AgentNeedsHuman] AI Agent cần trợ giúp từ bạn</b>",
        _ => $"📢 <b>[{kind}] Thông báo RevitAgent</b>"
    };
}
