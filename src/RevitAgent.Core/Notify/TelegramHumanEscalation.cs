using System.Text.Encodings.Web;
using System.Threading.Channels;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Manages human escalation over Telegram:
/// - Sends photo/text with inline option buttons.
/// - Polls updates via long polling (ONE shared poller; callbacks are routed to the question whose random id they carry),
///   accepting callbacks ONLY from the configured chat ID (and, for a private chat, only from that user).
/// - Answers callback, removes buttons, sends confirmation.
/// - Falls back to safe option on timeout (or after repeated polling failures) and sends notification.
/// - One human_ask question at a time (queues others). Owner approvals (<see cref="HumanQuestion.BypassQueue"/>) do not
///   wait behind a pending human_ask: both can be open at once.
/// - Returns safe option immediately when Telegram is not configured or disabled.
/// - Audits every ask, answer, and timeout without exposing the token.
/// </summary>
public sealed class TelegramHumanEscalation : IHumanEscalation
{
    /// <summary>Consecutive getUpdates failures after which the open questions fall back to their safe option.</summary>
    private const int MaxConsecutivePollFailures = 5;

    private readonly TelegramConfigStore _configStore;
    private readonly TelegramClient _client;
    private readonly AuditLog _auditLog;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly TimeSpan _backoffUnit;

    // Shared poller state (guarded by _pollGate). Telegram allows one getUpdates consumer per bot, and an offset
    // confirms every earlier update, so two questions must never poll independently.
    private readonly object _pollGate = new();
    private readonly Dictionary<string, ActiveQuestion> _active = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pollerCts;
    private Task? _poller;
    private long? _offset;

    public TelegramHumanEscalation(TelegramConfigStore configStore, TelegramClient client, AuditLog auditLog, TimeSpan? pollBackoffUnit = null)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _backoffUnit = pollBackoffUnit ?? TimeSpan.FromSeconds(1);
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

        // 2. Queue human_ask questions: only one at a time. An owner approval never waits behind one (a human_ask can
        //    stay open for hours and would otherwise starve the approval until it auto-denied).
        if (q.BypassQueue)
        {
            return await AskInternalAsync(options, q, askedAt, ct);
        }

        await _queue.WaitAsync(ct);
        try
        {
            return await AskInternalAsync(options, q, askedAt, ct);
        }
        finally
        {
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
        var title = HtmlEncoder.Default.Encode(q.Title ?? "Decision requested by the agent");
        var text = HtmlEncoder.Default.Encode(q.Text ?? string.Empty);
        var formattedMessage = $"❓ <b>{title}</b>\n\n{text}";

        // Register before sending so no callback of this question can arrive unrouted (polling starts after the send).
        var active = new ActiveQuestion(chatId, token);
        RegisterQuestion(questionId, active);
        try
        {
            return await AskRegisteredAsync(options, q, askedAt, ct, questionId, token, chatId, replyMarkup, formattedMessage, active);
        }
        finally
        {
            UnregisterQuestion(questionId);
        }
    }

    private async Task<HumanAnswer> AskRegisteredAsync(
        TelegramOptions options,
        HumanQuestion q,
        DateTimeOffset askedAt,
        CancellationToken ct,
        string questionId,
        string token,
        long chatId,
        InlineKeyboardMarkup replyMarkup,
        string formattedMessage,
        ActiveQuestion active)
    {
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

        EnsurePoller(token);

        // Determine question timeout
        var timeoutDuration = q.Timeout ?? TimeSpan.FromMinutes(options.AskTimeoutMinutes > 0 ? options.AskTimeoutMinutes : 30);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutDuration);

        string? failureReason = null;
        try
        {
            while (true)
            {
                TelegramCallbackQuery cb;
                try
                {
                    cb = await active.Callbacks.Reader.ReadAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timed out via timeoutCts
                    break;
                }
                catch (ChannelClosedException ex)
                {
                    failureReason = ex.InnerException?.Message ?? "Telegram polling failed";
                    break;
                }

                // Check if this callback matches an option of our question (the poller routed it by question id)
                var parts = (cb.Data ?? string.Empty).Split(':');
                if (parts.Length == 3 && int.TryParse(parts[2], out var idx) && idx >= 0 && idx < q.Options.Count)
                {
                    var choice = q.Options[idx];
                    var answeredBy = cb.From?.Username ?? cb.From?.FirstName ?? cb.From?.Id.ToString() ?? "owner";

                    // 1. Answer callback query
                    try
                    {
                        await _client.AnswerCallbackQueryAsync(cb.Id, $"Selected: {choice}", botToken: token, ct: CancellationToken.None);
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
                        await _client.SendMessageAsync(token, chatId, $"Selected: {choice}", replyMarkup: null, parseMode: null, CancellationToken.None);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled externally: take the buttons away and tell the owner why, then propagate.
            if (sentMessageId != 0)
            {
                try
                {
                    await _client.EditMessageReplyMarkupAsync(token, chatId, sentMessageId, replyMarkup: null, CancellationToken.None);
                    await _client.SendMessageAsync(token, chatId, q.CancelNote?.Invoke() ?? "The question was cancelled.", replyMarkup: null, parseMode: null, CancellationToken.None);
                }
                catch { }
            }

            throw;
        }

        // Timeout reached (or polling kept failing) -> Fall back to safe_option
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
            var note = failureReason is null
                ? $"Timed out - safe option chosen: {q.SafeOption}"
                : $"Could not read your answer - safe option chosen: {q.SafeOption}";
            await _client.SendMessageAsync(token, chatId, note, replyMarkup: null, parseMode: null, CancellationToken.None);
        }
        catch { }

        var timeoutAnswer = new HumanAnswer(
            Choice: q.SafeOption,
            AnsweredBy: "system",
            TimedOut: true,
            AskedAt: askedAt,
            AnsweredAt: DateTimeOffset.UtcNow,
            Reason: failureReason is null ? HumanAnswerReasons.Timeout : "Telegram polling failed: " + failureReason);

        RecordAudit("human_ask", timeoutAnswer, q, (long)(DateTimeOffset.UtcNow - askedAt).TotalMilliseconds);
        return timeoutAnswer;
    }

    // ---- shared poller ------------------------------------------------------------------------------------------

    private sealed class ActiveQuestion
    {
        public ActiveQuestion(long chatId, string token)
        {
            ChatId = chatId;
            Token = token;
        }

        public long ChatId { get; }

        public string Token { get; }

        public Channel<TelegramCallbackQuery> Callbacks { get; } = Channel.CreateUnbounded<TelegramCallbackQuery>();
    }

    private void RegisterQuestion(string questionId, ActiveQuestion question)
    {
        lock (_pollGate)
        {
            _active[questionId] = question;
        }
    }

    private void EnsurePoller(string token)
    {
        lock (_pollGate)
        {
            if (_poller is null && _active.Count > 0)
            {
                _pollerCts = new CancellationTokenSource();
                var pollerToken = _pollerCts.Token;
                _poller = Task.Run(() => PollLoopAsync(token, pollerToken));
            }
        }
    }

    private void UnregisterQuestion(string questionId)
    {
        lock (_pollGate)
        {
            _active.Remove(questionId);
            if (_active.Count == 0)
            {
                _pollerCts?.Cancel(); // the loop ends and clears _poller
            }
        }
    }

    private async Task PollLoopAsync(string token, CancellationToken pollerToken)
    {
        var failures = 0;
        try
        {
            while (!pollerToken.IsCancellationRequested)
            {
                IReadOnlyList<TelegramUpdate> updates;
                try
                {
                    updates = await _client.GetUpdatesAsync(token, _offset, timeoutSec: 1, pollerToken);
                    failures = 0;
                }
                catch (OperationCanceledException) when (pollerToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Transient (network error, HTTP timeout, Telegram error result): back off 1 s, 2 s, 4 s ... capped at 8 s.
                    failures++;
                    _auditLog.Diagnostic($"Telegram getUpdates failed ({failures}/{MaxConsecutivePollFailures}): {TelegramClient.Redact(ex.Message, token)}");
                    if (failures >= MaxConsecutivePollFailures)
                    {
                        FailAllQuestions(ex);
                        break;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromTicks(_backoffUnit.Ticks * Math.Min(1 << (failures - 1), 8)), pollerToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                foreach (var update in updates)
                {
                    _offset = update.UpdateId + 1;
                    Dispatch(update);
                }
            }
        }
        finally
        {
            lock (_pollGate)
            {
                _pollerCts?.Dispose();
                _pollerCts = null;
                _poller = null;
                // A question may have registered while the loop was ending: start a fresh poller for it.
                if (_active.Count > 0)
                {
                    _pollerCts = new CancellationTokenSource();
                    var next = _pollerCts.Token;
                    var nextToken = _active.Values.First().Token;
                    _poller = Task.Run(() => PollLoopAsync(nextToken, next));
                }
            }
        }
    }

    private void FailAllQuestions(Exception ex)
    {
        List<ActiveQuestion> all;
        lock (_pollGate)
        {
            all = _active.Values.ToList();
        }

        foreach (var q in all)
        {
            q.Callbacks.Writer.TryComplete(new InvalidOperationException(TelegramClient.Redact(ex.Message, q.Token)));
        }
    }

    /// <summary>Authorizes one update and routes a callback to the question it belongs to.</summary>
    private void Dispatch(TelegramUpdate update)
    {
        if (update.CallbackQuery is { } cb)
        {
            var data = cb.Data ?? string.Empty;
            var parts = data.Split(':');
            if (parts.Length != 3 || parts[0] != "q")
            {
                return;
            }

            ActiveQuestion? target;
            lock (_pollGate)
            {
                _active.TryGetValue(parts[1], out target);
            }

            if (target is null)
            {
                return; // stale or foreign callback
            }

            // Security: ONLY accept callbacks from the configured chat. In a private chat (positive id) the sender must
            // also be that user. A group (negative id) cannot be narrowed further: any member of the group can answer.
            var chatId = target.ChatId;
            var fromChatId = cb.Message?.Chat?.Id ?? cb.From?.Id ?? 0;
            var authorized = fromChatId == chatId && (chatId < 0 || cb.From?.Id == chatId);
            if (!authorized)
            {
                var rejectedId = fromChatId == chatId ? cb.From?.Id ?? 0 : fromChatId;
                _auditLog.Diagnostic($"Unauthorized callback query rejected from chat {rejectedId} (expected {chatId})");
                _auditLog.Record(new AuditEntry(
                    At: DateTimeOffset.UtcNow,
                    CallId: Guid.NewGuid().ToString("N")[..12],
                    SessionId: "telegram",
                    Tool: "telegram_callback_rejected",
                    Permission: ToolPermission.Interact.ToString(),
                    Args: AuditLog.SummarizeArgs(GatewayJson.ToElement(new { rejected_chat_id = rejectedId, user = cb.From?.Username })),
                    Ok: false,
                    ErrorCode: "UNAUTHORIZED_CHAT",
                    DurationMs: 0,
                    Note: "Update chat_id/user != configured chat_id"
                ));
                return;
            }

            target.Callbacks.Writer.TryWrite(cb);
        }
        else if (update.Message is { } msg)
        {
            long expected;
            lock (_pollGate)
            {
                expected = _active.Values.FirstOrDefault()?.ChatId ?? 0;
            }

            if (expected != 0 && msg.Chat?.Id != expected)
            {
                _auditLog.Diagnostic($"Unauthorized message rejected from chat {msg.Chat?.Id} (expected {expected})");
            }
        }
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
