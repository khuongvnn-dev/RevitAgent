using System.Diagnostics;
using System.Text.Encodings.Web;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Manages human escalation over Telegram:
/// - Sends photo/text with inline option buttons.
/// - Polls updates via long polling, accepting callbacks ONLY from the configured chat ID.
/// - Answers callback, removes buttons, sends confirmation.
/// - Falls back to safe option on timeout and sends notification.
/// - One question at a time (queues others).
/// - Returns safe option immediately when Telegram is not configured or disabled.
/// - Audits every ask, answer, and timeout without exposing the token.
/// </summary>
public sealed class TelegramHumanEscalation : IHumanEscalation
{
    private readonly TelegramConfigStore _configStore;
    private readonly TelegramClient _client;
    private readonly AuditLog _auditLog;
    private readonly SemaphoreSlim _queue = new(1, 1);

    public TelegramHumanEscalation(TelegramConfigStore configStore, TelegramClient client, AuditLog auditLog)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    public TimeSpan? DefaultTimeout
    {
        get
        {
            var o = _configStore.Load();
            return TimeSpan.FromMinutes(o.AskTimeoutMinutes > 0 ? o.AskTimeoutMinutes : 30);
        }
    }

    public async Task<HumanAnswer> AskAsync(HumanQuestion q, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(q);

        var askedAt = DateTimeOffset.UtcNow;
        var options = _configStore.Load();

        // 1. If Telegram is not configured or disabled, return safe_option immediately
        if (!options.Enabled || !options.IsConfigured)
        {
            var answer = new HumanAnswer(
                Choice: q.SafeOption,
                AnsweredBy: "system",
                TimedOut: true,
                AskedAt: askedAt,
                AnsweredAt: DateTimeOffset.UtcNow,
                Reason: "Telegram channel is disabled or unconfigured");

            RecordAudit("human_ask", answer, q, 0);
            return answer;
        }

        // 2. Queue questions: only one question active at a time
        await _queue.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            return await AskInternalAsync(options, q, askedAt, ct);
        }
        finally
        {
            sw.Stop();
            _queue.Release();
        }
    }

    private async Task<HumanAnswer> AskInternalAsync(
        TelegramOptions options,
        HumanQuestion q,
        DateTimeOffset askedAt,
        CancellationToken ct)
    {
        var questionId = Guid.NewGuid().ToString("N")[..8];
        var token = options.BotToken!;
        var chatId = options.ChatId;

        // Build inline buttons (each option on its own row for readability)
        var buttonRows = new List<List<InlineKeyboardButton>>();
        for (int i = 0; i < q.Options.Count; i++)
        {
            var optText = q.Options[i];
            var callbackData = $"q:{questionId}:{i}";
            buttonRows.Add(new List<InlineKeyboardButton>
            {
                new InlineKeyboardButton(optText, callbackData)
            });
        }
        var replyMarkup = new InlineKeyboardMarkup(buttonRows);

        // Format message
        var title = HtmlEncoder.Default.Encode(q.Title ?? "Yêu cầu quyết định từ Agent");
        var text = HtmlEncoder.Default.Encode(q.Text ?? string.Empty);
        var formattedMessage = $"❓ <b>{title}</b>\n\n{text}";

        int sentMessageId = 0;
        try
        {
            if (!string.IsNullOrEmpty(q.ScreenshotPath) && File.Exists(q.ScreenshotPath))
            {
                var bytes = await File.ReadAllBytesAsync(q.ScreenshotPath, ct);
                var sendRes = await _client.SendPhotoAsync(
                    token,
                    chatId,
                    bytes,
                    Path.GetFileName(q.ScreenshotPath),
                    caption: formattedMessage,
                    replyMarkup: replyMarkup,
                    parseMode: "HTML",
                    ct: ct);
                sentMessageId = sendRes.Result?.MessageId ?? 0;
            }
            else
            {
                var sendRes = await _client.SendMessageAsync(
                    token,
                    chatId,
                    formattedMessage,
                    replyMarkup: replyMarkup,
                    parseMode: "HTML",
                    ct: ct);
                sentMessageId = sendRes.Result?.MessageId ?? 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var redacted = TelegramClient.Redact(ex.Message, token);
            _auditLog.Diagnostic($"Failed to send Telegram question '{questionId}': {redacted}", ex);

            var fallback = new HumanAnswer(
                Choice: q.SafeOption,
                AnsweredBy: "system",
                TimedOut: true,
                AskedAt: askedAt,
                AnsweredAt: DateTimeOffset.UtcNow,
                Reason: $"Telegram send failed: {redacted}");

            RecordAudit("human_ask", fallback, q, 0);
            return fallback;
        }

        // Determine question timeout
        var timeoutDuration = q.Timeout ?? TimeSpan.FromMinutes(options.AskTimeoutMinutes > 0 ? options.AskTimeoutMinutes : 30);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutDuration);

        long? offset = null;
        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                IReadOnlyList<TelegramUpdate> updates;
                try
                {
                    updates = await _client.GetUpdatesAsync(token, offset, timeoutSec: 1, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timed out via timeoutCts
                    break;
                }

                foreach (var update in updates)
                {
                    offset = update.UpdateId + 1;

                    // Security: ONLY accept callback queries and messages from configured chat ID
                    if (update.CallbackQuery is { } cb)
                    {
                        var fromChatId = cb.Message?.Chat?.Id ?? cb.From?.Id ?? 0;
                        if (fromChatId != chatId)
                        {
                            _auditLog.Diagnostic($"Unauthorized callback query rejected from chat {fromChatId} (expected {chatId})");
                            _auditLog.Record(new AuditEntry(
                                At: DateTimeOffset.UtcNow,
                                CallId: Guid.NewGuid().ToString("N")[..12],
                                SessionId: "telegram",
                                Tool: "telegram_callback_rejected",
                                Permission: ToolPermission.Interact.ToString(),
                                Args: AuditLog.SummarizeArgs(GatewayJson.ToElement(new { rejected_chat_id = fromChatId, user = cb.From?.Username })),
                                Ok: false,
                                ErrorCode: "UNAUTHORIZED_CHAT",
                                DurationMs: 0,
                                Note: "Update chat_id != configured chat_id"
                            ));
                            continue;
                        }

                        // Check if this callback matches our active question
                        if (cb.Data != null && cb.Data.StartsWith($"q:{questionId}:", StringComparison.Ordinal))
                        {
                            var parts = cb.Data.Split(':');
                            if (parts.Length == 3 && int.TryParse(parts[2], out var idx) && idx >= 0 && idx < q.Options.Count)
                            {
                                var choice = q.Options[idx];
                                var answeredBy = cb.From?.Username ?? cb.From?.FirstName ?? cb.From?.Id.ToString() ?? "owner";

                                // 1. Answer callback query
                                try
                                {
                                    await _client.AnswerCallbackQueryAsync(cb.Id, $"Đã chọn: {choice}", botToken: token, ct: CancellationToken.None);
                                }
                                catch { }

                                // 2. Remove buttons
                                if (sentMessageId != 0)
                                {
                                    try
                                    {
                                        await _client.EditMessageReplyMarkupAsync(token, chatId, sentMessageId, replyMarkup: null, CancellationToken.None);
                                    }
                                    catch { }
                                }

                                // 3. Send confirmation message
                                try
                                {
                                    await _client.SendMessageAsync(token, chatId, $"Đã chọn: {choice}", replyMarkup: null, parseMode: null, CancellationToken.None);
                                }
                                catch { }

                                var answer = new HumanAnswer(
                                    Choice: choice,
                                    AnsweredBy: answeredBy,
                                    TimedOut: false,
                                    AskedAt: askedAt,
                                    AnsweredAt: DateTimeOffset.UtcNow);

                                RecordAudit("human_ask", answer, q, (long)(DateTimeOffset.UtcNow - askedAt).TotalMilliseconds);
                                return answer;
                            }
                        }
                    }
                    else if (update.Message is { } msg)
                    {
                        if (msg.Chat?.Id != chatId)
                        {
                            _auditLog.Diagnostic($"Unauthorized message rejected from chat {msg.Chat?.Id} (expected {chatId})");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled externally: take the buttons away and tell the owner why, then propagate.
            if (sentMessageId != 0)
            {
                try
                {
                    await _client.EditMessageReplyMarkupAsync(token, chatId, sentMessageId, replyMarkup: null, CancellationToken.None);
                    await _client.SendMessageAsync(token, chatId, q.CancelNote?.Invoke() ?? "Câu hỏi đã bị hủy.", replyMarkup: null, parseMode: null, CancellationToken.None);
                }
                catch { }
            }

            throw;
        }

        // Timeout reached -> Fall back to safe_option
        // 1. Remove buttons
        if (sentMessageId != 0)
        {
            try
            {
                await _client.EditMessageReplyMarkupAsync(token, chatId, sentMessageId, replyMarkup: null, CancellationToken.None);
            }
            catch { }
        }

        // 2. Send timeout notification
        try
        {
            await _client.SendMessageAsync(token, chatId, $"Hết giờ — chọn an toàn: {q.SafeOption}", replyMarkup: null, parseMode: null, CancellationToken.None);
        }
        catch { }

        var timeoutAnswer = new HumanAnswer(
            Choice: q.SafeOption,
            AnsweredBy: "system",
            TimedOut: true,
            AskedAt: askedAt,
            AnsweredAt: DateTimeOffset.UtcNow,
            Reason: HumanAnswerReasons.Timeout);

        RecordAudit("human_ask", timeoutAnswer, q, (long)(DateTimeOffset.UtcNow - askedAt).TotalMilliseconds);
        return timeoutAnswer;
    }

    private void RecordAudit(string tool, HumanAnswer answer, HumanQuestion q, long durationMs)
    {
        _auditLog.Record(new AuditEntry(
            At: answer.AnsweredAt,
            CallId: Guid.NewGuid().ToString("N")[..12],
            SessionId: "telegram",
            Tool: tool,
            Permission: ToolPermission.Interact.ToString(),
            Args: AuditLog.SummarizeArgs(GatewayJson.ToElement(new
            {
                title = q.Title,
                options = q.Options,
                safe_option = q.SafeOption,
                timed_out = answer.TimedOut,
                choice = answer.Choice,
                answered_by = answer.AnsweredBy,
                reason = answer.Reason
            })),
            Ok: true,
            ErrorCode: answer.TimedOut ? "TIMED_OUT" : null,
            DurationMs: durationMs,
            Note: answer.TimedOut ? $"Safe option '{answer.Choice}' selected" : $"User answered '{answer.Choice}'"
        ));
    }
}
