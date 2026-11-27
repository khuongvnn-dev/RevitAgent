using System.Text.Json.Serialization;

namespace RevitAgent.Core.Notify;

/// <summary>
/// A question to ask the human owner via Telegram.
/// </summary>
public sealed record HumanQuestion(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("screenshot_path")] string? ScreenshotPath,
    [property: JsonPropertyName("options")] IReadOnlyList<string> Options,
    [property: JsonPropertyName("safe_option")] string SafeOption,
    [property: JsonPropertyName("timeout")] TimeSpan? Timeout = null)
{
    /// <summary>Text sent to the owner when the question is cancelled by the caller (e.g. answered elsewhere); evaluated at cancel time.</summary>
    [JsonIgnore]
    public Func<string>? CancelNote { get; init; }

    /// <summary>
    /// True for owner approvals: the question is not queued behind a pending human_ask (which can stay open for hours and
    /// would starve the approval until it auto-denied). Concurrent questions are told apart by their callback ids.
    /// </summary>
    [JsonIgnore]
    public bool BypassQueue { get; init; }
}

/// <summary>Reason strings of <see cref="HumanAnswer"/> that callers rely on.</summary>
public static class HumanAnswerReasons
{
    /// <summary>The owner received the question and did not answer in time (the only case where a safe fallback is a decision).</summary>
    public const string Timeout = "Timed out waiting for response";
}

/// <summary>
/// Answer from the human owner or safe fallback on timeout.
/// </summary>
public sealed record HumanAnswer(
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("answered_by")] string AnsweredBy,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("asked_at")] DateTimeOffset AskedAt,
    [property: JsonPropertyName("answered_at")] DateTimeOffset AnsweredAt,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>
/// Human escalation channel interface.
/// </summary>
public interface IHumanEscalation
{
    /// <summary>The question timeout used when <see cref="HumanQuestion.Timeout"/> is null (null = unknown).</summary>
    TimeSpan? DefaultTimeout => null;

    Task<HumanAnswer> AskAsync(HumanQuestion q, CancellationToken ct = default);
}
