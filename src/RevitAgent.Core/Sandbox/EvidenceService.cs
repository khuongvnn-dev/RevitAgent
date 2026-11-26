using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Sandbox;

/// <summary>Who is writing right now (set by the tool registry for the duration of one tool call).</summary>
public sealed record EvidenceCaller(string Tool, string SessionId, string CallId);

/// <summary>
/// Remembers which run an MCP session (and which Revit process) works in, chooses the default evidence folder of every
/// capture (<c>&lt;run&gt;\evidence\</c>, else the captures folder) and appends one JSON line per written file to
/// <c>&lt;run&gt;\evidence\index.jsonl</c>, so a report can be checked against what was really captured.
/// </summary>
public sealed class EvidenceService
{
    public const string IndexFile = "index.jsonl";

    private static readonly AsyncLocal<EvidenceCaller?> Ambient = new();
    private static readonly object IndexGate = new();
    private static readonly Regex Unsafe = new("[^A-Za-z0-9_.-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, string> _sessionRuns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _pidRuns = new();

    /// <summary>The tool call running on this async flow, if any.</summary>
    public static EvidenceCaller? CurrentCaller => Ambient.Value;

    /// <summary>Marks the current async flow as running <paramref name="call"/> (disposing restores the previous caller).</summary>
    public static IDisposable Enter(ToolCall call)
    {
        var previous = Ambient.Value;
        Ambient.Value = new EvidenceCaller(call.Name, call.SessionId, call.CallId);
        return new Scope(previous);
    }

    // ---- session / pid -> run ----

    public void SetSessionRun(string? sessionId, string? runDir)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(runDir))
        {
            _sessionRuns[sessionId] = Path.GetFullPath(runDir);
        }
    }

    public string? SessionRunDir(string? sessionId) =>
        sessionId is not null && _sessionRuns.TryGetValue(sessionId, out var d) ? d : null;

    public void SetPidRun(int pid, string? runDir)
    {
        if (pid > 0 && !string.IsNullOrWhiteSpace(runDir))
        {
            _pidRuns[pid] = Path.GetFullPath(runDir);
        }
    }

    public string? PidRunDir(int pid) => _pidRuns.TryGetValue(pid, out var d) ? d : null;

    /// <summary>A model opened by <c>doc_open</c> inside a run folder makes that run current for the session and for the Revit pid.</summary>
    public void NoteModelOpened(string? sessionId, int pid, string modelPath)
    {
        if (FindRunDir(modelPath) is { } run)
        {
            SetSessionRun(sessionId, run);
            SetPidRun(pid, run);
        }
    }

    /// <summary>The run folder (contains run.json or run-info.json) that holds <paramref name="path"/>, or null.</summary>
    public static string? FindRunDir(string path)
    {
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var i = 0; dir is not null && i < 12; i++, dir = Path.GetDirectoryName(dir))
        {
            if (IsRunDir(dir))
            {
                return dir;
            }
        }

        return null;
    }

    private static bool IsRunDir(string dir) =>
        File.Exists(Path.Combine(dir, "run.json")) || File.Exists(Path.Combine(dir, SandboxService.RunInfoFile));

    // ---- default paths ----

    /// <summary>
    /// <c>&lt;current run&gt;\evidence</c> of the session (preferred) or, for gateway-initiated captures, of the pid; null when neither has a run.
    /// </summary>
    public string? EvidenceDirFor(string? sessionId, int? pid = null)
    {
        var run = SessionRunDir(sessionId) ?? (pid is { } p ? PidRunDir(p) : null);
        if (run is null || !Directory.Exists(run))
        {
            return null;
        }

        return Directory.CreateDirectory(Path.Combine(run, "evidence")).FullName;
    }

    /// <summary>Default file for a capture: in the current run's evidence folder, else in <paramref name="fallbackDir"/>.</summary>
    public string DefaultPath(string? sessionId, int? pid, string tool, string? detail, string ext, string fallbackDir)
    {
        var dir = EvidenceDirFor(sessionId, pid) ?? fallbackDir;
        return UniquePath(dir, FileName(tool, detail, ext));
    }

    /// <summary>Readable, sortable name: <c>HHmmss-fff_tool_detail.ext</c>.</summary>
    public static string FileName(string tool, string? detail, string ext, DateTime? now = null)
    {
        var name = $"{now ?? DateTime.Now:HHmmss-fff}_{Clean(tool)}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            name += "_" + Clean(detail);
        }

        return name + (ext.StartsWith('.') ? ext : "." + ext);
    }

    private static string Clean(string s)
    {
        var c = Unsafe.Replace(s.Trim(), "-").Trim('-');
        return c.Length == 0 ? "x" : c.Length > 60 ? c[..60] : c;
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(dir, $"{stem}-{n}{ext}");
        }

        return path;
    }

    // ---- index ----

    /// <summary>
    /// Appends one line to the <c>index.jsonl</c> of the run evidence folder that contains <paramref name="file"/> (any depth).
    /// No-op when the file is not inside a run's evidence folder. Never throws (evidence is best effort).
    /// </summary>
    public void Record(string file, string kind, string? tool = null, string? sessionId = null, int? pid = null, string? callId = null)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var evidence = FindEvidenceDir(full);
            if (evidence is null || string.Equals(Path.GetFileName(full), IndexFile, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var caller = Ambient.Value;
            long? bytes = File.Exists(full) ? new FileInfo(full).Length : null;
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["at"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                ["file"] = full,
                ["tool"] = tool ?? caller?.Tool,
                ["session_id"] = sessionId ?? caller?.SessionId,
                ["pid"] = pid,
                ["call_id"] = callId ?? caller?.CallId,
                ["kind"] = kind,
                ["bytes"] = bytes,
            });
            AppendLine(Path.Combine(evidence, IndexFile), line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best effort.
        }
    }

    /// <summary>Records every file below <paramref name="dir"/> (reports, screenshots of a script run).</summary>
    public void RecordDirectory(string dir, string? tool = null, string? sessionId = null, int? pid = null)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            Record(f, KindOf(f), tool, sessionId, pid);
        }
    }

    public static string KindOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" => "screenshot",
        ".json" or ".md" or ".html" or ".txt" => "report",
        _ => "export",
    };

    /// <summary>The nearest ancestor folder named <c>evidence</c> whose parent is a run folder, or null.</summary>
    public static string? FindEvidenceDir(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        for (var i = 0; dir is not null && i < 8; i++, dir = Path.GetDirectoryName(dir))
        {
            if (string.Equals(Path.GetFileName(dir), "evidence", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(dir) is { } run && IsRunDir(run))
            {
                return dir;
            }
        }

        return null;
    }

    private static void AppendLine(string indexPath, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (IndexGate)
        {
            for (var i = 0; i < 15; i++)
            {
                try
                {
                    using var fs = new FileStream(indexPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush();
                    return;
                }
                catch (IOException) when (i < 14)
                {
                    Thread.Sleep(20 + Random.Shared.Next(30));
                }
            }
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly EvidenceCaller? _previous;

        public Scope(EvidenceCaller? previous) => _previous = previous;

        public void Dispose() => Ambient.Value = _previous;
    }
}
