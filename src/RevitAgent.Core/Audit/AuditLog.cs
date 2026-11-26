using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitAgent.Core.Audit;

/// <summary>One audited tool call: tool, args summary, result.</summary>
public sealed record AuditEntry(
    DateTimeOffset At,
    string CallId,
    string SessionId,
    string Tool,
    string Permission,
    string Args,
    bool Ok,
    string? ErrorCode,
    long DurationMs,
    string? Note = null,
    bool GrantUsed = false);

/// <summary>
/// Append-only JSONL audit log at <c>&lt;root&gt;\audit\yyyyMMdd.jsonl</c>, plus an in-memory ring
/// buffer for the dashboard. Secrets in arguments are redacted; long arguments are clipped.
/// Thread-safe.
/// </summary>
public sealed class AuditLog
{
    private const int MaxArgsChars = 2_000;
    private const int RingSize = 500;

    private static readonly Regex SecretPattern = new(
        "(\"(?:[a-z_]*token|password|passwd|secret|api_key|apikey|authorization)\"\\s*:\\s*)(\"[^\"]*\"|[0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Queue<AuditEntry> _recent = new();
    private readonly AgentPaths _paths;

    public AuditLog(AgentPaths paths) => _paths = paths;

    /// <summary>Raised after an entry is recorded (dashboard live view). Handlers must be cheap.</summary>
    public event Action<AuditEntry>? Recorded;

    public IReadOnlyList<AuditEntry> Recent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    public static string SummarizeArgs(JsonElement args)
    {
        var raw = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
        raw = SecretPattern.Replace(raw, "$1\"[REDACTED]\"");
        return raw.Length <= MaxArgsChars ? raw : raw[..MaxArgsChars] + "…";
    }

    public void Record(AuditEntry entry)
    {
        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RingSize)
            {
                _recent.Dequeue();
            }

            try
            {
                var file = Path.Combine(_paths.AuditDir, entry.At.ToLocalTime().ToString("yyyyMMdd") + ".jsonl");
                File.AppendAllText(file, JsonSerializer.Serialize(entry, Tools.GatewayJson.Options) + "\n", new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // Audit must never break a tool call; the in-memory ring still has the entry.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Recorded?.Invoke(entry);
        }
        catch
        {
            // A broken subscriber must not affect the gateway.
        }
    }

    /// <summary>Writes a diagnostic line (with full exception) to logs\gateway-yyyyMMdd.log.</summary>
    public void Diagnostic(string message, Exception? ex = null)
    {
        lock (_gate)
        {
            try
            {
                var file = Path.Combine(_paths.LogDir, "gateway-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                var line = $"[{DateTimeOffset.Now:O}] {message}{(ex is null ? string.Empty : Environment.NewLine + ex)}{Environment.NewLine}";
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
