using System.Text.Json;

namespace RevitAgent.Core.Mcp;

/// <summary>
/// Bookkeeping for the Shim relay: follows newline-delimited JSON-RPC traffic in both directions and knows which
/// client requests have not been answered yet. It never alters the bytes; parsing is best effort (a line that is not
/// JSON is ignored). Lines split across reads are reassembled per direction.
/// </summary>
public sealed class PendingRequestTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly LineSplitter _up = new();
    private readonly LineSplitter _down = new();

    public int PendingCount
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    /// <summary>Feeds bytes travelling client to Host.</summary>
    public void ObserveUpstream(ReadOnlySpan<byte> bytes) => Observe(_up, bytes, upstream: true);

    /// <summary>Feeds bytes travelling Host to client.</summary>
    public void ObserveDownstream(ReadOnlySpan<byte> bytes) => Observe(_down, bytes, upstream: false);

    private void Observe(LineSplitter splitter, ReadOnlySpan<byte> bytes, bool upstream)
    {
        List<byte[]> lines;
        lock (_gate)
        {
            lines = splitter.Feed(bytes);
        }

        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var id)
                    || id.ValueKind == JsonValueKind.Null)
                {
                    continue; // notification, or not a request/response
                }

                var key = id.GetRawText();
                var hasMethod = doc.RootElement.TryGetProperty("method", out _);
                lock (_gate)
                {
                    if (upstream && hasMethod)
                    {
                        _pending.Add(key);
                    }
                    else if (!upstream && !hasMethod)
                    {
                        _pending.Remove(key);
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON: ignore.
            }
        }
    }

    private sealed class LineSplitter
    {
        private const int MaxLine = 64 * 1024 * 1024;
        private readonly List<byte> _buffer = new();
        private bool _overflow;

        public List<byte[]> Feed(ReadOnlySpan<byte> bytes)
        {
            var lines = new List<byte[]>();
            foreach (var b in bytes)
            {
                if (b == (byte)'\n')
                {
                    if (!_overflow && _buffer.Count > 0)
                    {
                        lines.Add(_buffer.ToArray());
                    }

                    _buffer.Clear();
                    _overflow = false;
                }
                else if (!_overflow)
                {
                    if (_buffer.Count >= MaxLine)
                    {
                        _overflow = true; // absurdly long line: stop tracking it
                        _buffer.Clear();
                    }
                    else
                    {
                        _buffer.Add(b);
                    }
                }
            }

            return lines;
        }
    }
}
