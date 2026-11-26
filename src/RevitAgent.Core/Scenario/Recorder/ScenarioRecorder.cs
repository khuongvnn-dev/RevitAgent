using System.Collections.Concurrent;
using System.Text.Json;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Scenario.Recorder;

/// <summary>One successful tool call captured while recording.</summary>
public sealed record RecordedCall(int Seq, DateTimeOffset At, string Tool, ToolPermission Permission, JsonElement Args, JsonElement? Result, bool ResultTruncated, long DurationMs);

/// <summary>Recording state of one MCP session.</summary>
public sealed class RecordingSession
{
    private readonly object _gate = new();
    private readonly List<RecordedCall> _calls = new();

    public RecordingSession(string sessionId, string? label)
    {
        SessionId = sessionId;
        Label = label;
    }

    public string SessionId { get; }

    public string? Label { get; }

    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    public DateTimeOffset? Stopped { get; private set; }

    public bool Active => Stopped is null;

    /// <summary>Calls not kept because the per-session cap was reached.</summary>
    public int Dropped { get; private set; }

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    internal void Add(string tool, ToolPermission permission, JsonElement args, JsonElement? result, bool truncated, long ms)
    {
        lock (_gate)
        {
            if (!Active)
            {
                return;
            }

            if (_calls.Count >= ScenarioRecorder.MaxCallsPerSession)
            {
                Dropped++;
                return;
            }

            _calls.Add(new RecordedCall(_calls.Count + 1, DateTimeOffset.Now, tool, permission, args, result, truncated, ms));
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            Stopped ??= DateTimeOffset.Now;
        }
    }
}

/// <summary>
/// Scenario recorder (explore → record → freeze → replay without AI). Observes every completed tool call and, for MCP
/// sessions that called recorder_start, keeps the successful calls that matter for a replay: Interact/Write/Admin tools
/// (clicks, typing, commands, document/sandbox calls) plus the verification reads the agent made (ui_expect, ui_wait,
/// ui_read_table, model_query_elements, schedule_read, doc_info). Never recorded: failed calls, recorder_* / scenario_* /
/// gateway_* tools and tool-forwarding wrappers (their inner calls are recorded instead). recorder_export turns a
/// recording into a draft scenario folder.
/// </summary>
public sealed class ScenarioRecorder : IToolCallObserver
{
    public const int MaxCallsPerSession = 5000;
    public const int MaxSessions = 32;
    public const int MaxResultChars = 200_000;

    /// <summary>Read tools recorded anyway: they are the agent's verifications (become checks) or replay timing (ui_wait).</summary>
    public static readonly IReadOnlySet<string> VerificationReads = new HashSet<string>(StringComparer.Ordinal)
    {
        "ui_expect", "ui_wait", "ui_read_table", "model_query_elements", "schedule_read", "doc_info",
    };

    public static readonly IReadOnlyList<string> ExcludedPrefixes = new[] { "recorder_", "scenario_", "gateway_" };

    private readonly ConcurrentDictionary<string, RecordingSession> _sessions = new(StringComparer.Ordinal);

    public static bool ShouldRecord(string tool, ToolPermission permission) =>
        !ExcludedPrefixes.Any(p => tool.StartsWith(p, StringComparison.Ordinal)) &&
        (permission >= ToolPermission.Interact || VerificationReads.Contains(tool));

    public void OnCompleted(ToolCall call, IGatewayTool tool, ToolResult result, TimeSpan duration)
    {
        if (result.IsError || !_sessions.TryGetValue(call.SessionId, out var session) || !session.Active || !ShouldRecord(tool.Name, tool.Permission))
        {
            return;
        }

        JsonElement? stored = null;
        var truncated = false;
        if (result.Structured is { } s)
        {
            if (s.GetRawText().Length <= MaxResultChars)
            {
                stored = s.Clone();
            }
            else
            {
                truncated = true;
            }
        }

        session.Add(tool.Name, tool.Permission, call.Arguments.Clone(), stored, truncated, (long)duration.TotalMilliseconds);
    }

    /// <summary>Starts recording for <paramref name="sessionId"/>; throws CONFLICT when already recording and not <paramref name="restart"/>.</summary>
    public RecordingSession Start(string sessionId, string? label, bool restart)
    {
        if (_sessions.TryGetValue(sessionId, out var existing) && existing.Active && !restart)
        {
            throw new GatewayException(
                Contracts.Protocol.GatewayErrorCodes.Conflict,
                $"Already recording in this session since {existing.Started:HH:mm:ss} ({existing.Calls.Count} calls). Call recorder_stop, or recorder_start with restart: true to discard it.");
        }

        var session = new RecordingSession(sessionId, label);
        _sessions[sessionId] = session;
        Evict();
        return session;
    }

    public RecordingSession Stop(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s))
        {
            throw GatewayException.NotFound("No recording in this session. Call recorder_start first.");
        }

        s.Stop();
        return s;
    }

    public RecordingSession? Get(string sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s : null;

    private void Evict()
    {
        if (_sessions.Count <= MaxSessions)
        {
            return;
        }

        foreach (var old in _sessions.Values.Where(s => !s.Active).OrderBy(s => s.Started).Take(_sessions.Count - MaxSessions))
        {
            _sessions.TryRemove(old.SessionId, out _);
        }
    }
}
