namespace RevitAgent.Contracts.Bridge.Wire
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Ordered JSON object (insertion order, ordinal keys) used by the Bridge wire format.
    /// Supports collection initializers: <c>new WireObject { { "id", 1 }, { "name", "Level 1" } }</c>.
    /// </summary>
    internal sealed class WireObject : IEnumerable<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _items = new List<KeyValuePair<string, object?>>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count => _items.Count;

        public IEnumerable<string> Keys
        {
            get
            {
                foreach (var kv in _items)
                {
                    yield return kv.Key;
                }
            }
        }

        /// <summary>Gets the value (null when missing) or sets/replaces it.</summary>
        public object? this[string key]
        {
            get => TryGetValue(key, out var v) ? v : null;
            set => Set(key, value);
        }

        public void Add(string key, object? value) => Set(key, value);

        public WireObject Set(string key, object? value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (_index.TryGetValue(key, out var i))
            {
                _items[i] = new KeyValuePair<string, object?>(key, value);
            }
            else
            {
                _index[key] = _items.Count;
                _items.Add(new KeyValuePair<string, object?>(key, value));
            }

            return this;
        }

        public bool ContainsKey(string key) => _index.ContainsKey(key);

        public bool TryGetValue(string key, out object? value)
        {
            if (_index.TryGetValue(key, out var i))
            {
                value = _items[i].Value;
                return true;
            }

            value = null;
            return false;
        }

        public bool Remove(string key)
        {
            if (!_index.TryGetValue(key, out var i))
            {
                return false;
            }

            _items.RemoveAt(i);
            _index.Clear();
            for (var j = 0; j < _items.Count; j++)
            {
                _index[_items[j].Key] = j;
            }

            return true;
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => WireJson.Serialize(this);
    }

    /// <summary>A pre-serialized JSON fragment written verbatim by <see cref="WireJson"/>.</summary>
    internal sealed class WireRawJson
    {
        public WireRawJson(string json) => Json = json ?? "null";

        public string Json { get; }

        public override string ToString() => Json;
    }

    /// <summary>Malformed JSON text.</summary>
    internal sealed class WireJsonException : FormatException
    {
        public WireJsonException(string message, int position)
            : base(message + " (at position " + position.ToString(CultureInfo.InvariantCulture) + ")")
        {
            Position = position;
        }

        public int Position { get; }
    }

    /// <summary>
    /// Minimal, dependency-free JSON reader/writer (RFC 8259) for the Bridge wire protocol.
    /// The Bridge add-in cannot use System.Text.Json/Newtonsoft (other add-ins in the same Revit
    /// process ship conflicting versions), so this file is linked into it as source.
    /// <para>Parse model: <see cref="WireObject"/>, <see cref="List{T}"/> of object, string, long
    /// (integers that fit), double, bool, null.</para>
    /// <para>Writer accepts the parse model plus: other integer/floating types, decimal, enums (name),
    /// DateTime/DateTimeOffset (ISO 8601), Guid, IDictionary, any IEnumerable (array) and
    /// <see cref="WireRawJson"/>. Other objects are written as their invariant string.</para>
    /// </summary>
    internal static class WireJson
    {
        public const int MaxDepth = 96;

        public static object? Parse(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var reader = new Reader(json);
            reader.SkipWhitespace();
            var value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw new WireJsonException("Unexpected characters after the JSON value", reader.Position);
            }

            return value;
        }

        /// <summary>Parses text that must be a JSON object.</summary>
        public static WireObject ParseObject(string json) =>
            Parse(json) as WireObject ?? throw new WireJsonException("Expected a JSON object", 0);

        public static string Serialize(object? value)
        {
            var sb = new StringBuilder(256);
            Write(sb, value, 0);
            return sb.ToString();
        }

        public static void Write(StringBuilder sb, object? value) => Write(sb, value, 0);

        private static void Write(StringBuilder sb, object? value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException("JSON value nested deeper than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels (cycle?).");
            }

            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case WireRawJson raw:
                    sb.Append(raw.Json);
                    return;
                case WireObject obj:
                    WriteObject(sb, obj, depth);
                    return;
                case char c:
                    WriteString(sb, c.ToString());
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case double d:
                    WriteDouble(sb, d);
                    return;
                case float f:
                    if (float.IsNaN(f) || float.IsInfinity(f))
                    {
                        sb.Append("null");
                    }
                    else
                    {
                        sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                    }

                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case short or ushort or byte or sbyte or uint or ulong:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case Enum e:
                    WriteString(sb, e.ToString());
                    return;
                case DateTime dt:
                    WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture));
                    return;
                case DateTimeOffset dto:
                    WriteString(sb, dto.ToString("o", CultureInfo.InvariantCulture));
                    return;
                case Guid g:
                    WriteString(sb, g.ToString("D"));
                    return;
                case IDictionary dict:
                    WriteDictionary(sb, dict, depth);
                    return;
                case IEnumerable seq:
                    WriteArray(sb, seq, depth);
                    return;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return;
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }

            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteObject(StringBuilder sb, WireObject obj, int depth)
        {
            sb.Append('{');
            var first = true;
            foreach (var kv in obj)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                Write(sb, kv.Value, depth + 1);
            }

            sb.Append('}');
        }

        private static void WriteDictionary(StringBuilder sb, IDictionary dict, int depth)
        {
            sb.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append(':');
                Write(sb, entry.Value, depth + 1);
            }

            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable seq, int depth)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in seq)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                Write(sb, item, depth + 1);
            }

            sb.Append(']');
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029 || char.IsSurrogate(c))
                        {
                            // Surrogates are escaped too: a lone surrogate would otherwise become invalid UTF-8.
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _pos;

            public Reader(string s) => _s = s;

            public int Position => _pos;

            public bool AtEnd => _pos >= _s.Length;

            public void SkipWhitespace()
            {
                while (_pos < _s.Length)
                {
                    var c = _s[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        _pos++;
                    }
                    else if (c == (char)0xFEFF && _pos == 0)
                    {
                        _pos++; // tolerate a UTF-8 BOM decoded as U+FEFF
                    }
                    else
                    {
                        break;
                    }
                }
            }

            public object? ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new WireJsonException("JSON nested deeper than " + MaxDepth.ToString(CultureInfo.InvariantCulture) + " levels", _pos);
                }

                if (AtEnd)
                {
                    throw new WireJsonException("Unexpected end of JSON", _pos);
                }

                var c = _s[_pos];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return ReadString();
                    case 't':
                        ExpectLiteral("true");
                        return true;
                    case 'f':
                        ExpectLiteral("false");
                        return false;
                    case 'n':
                        ExpectLiteral("null");
                        return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw new WireJsonException("Unexpected character '" + c + "'", _pos);
                }
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_s, _pos, literal, 0, literal.Length) != 0)
                {
                    throw new WireJsonException("Invalid literal", _pos);
                }

                _pos += literal.Length;
            }

            private WireObject ReadObject(int depth)
            {
                var obj = new WireObject();
                _pos++; // {
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _pos++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw new WireJsonException("Expected a property name", _pos);
                    }

                    var key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':')
                    {
                        throw new WireJsonException("Expected ':'", _pos);
                    }

                    _pos++;
                    SkipWhitespace();
                    obj.Set(key, ReadValue(depth + 1));
                    SkipWhitespace();
                    var c = Peek();
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }

                    if (c == '}')
                    {
                        _pos++;
                        return obj;
                    }

                    throw new WireJsonException("Expected ',' or '}'", _pos);
                }
            }

            private List<object?> ReadArray(int depth)
            {
                var list = new List<object?>();
                _pos++; // [
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _pos++;
                    return list;
                }

                while (true)
                {
                    SkipWhitespace();
                    list.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    var c = Peek();
                    if (c == ',')
                    {
                        _pos++;
                        continue;
                    }

                    if (c == ']')
                    {
                        _pos++;
                        return list;
                    }

                    throw new WireJsonException("Expected ',' or ']'", _pos);
                }
            }

            private char Peek()
            {
                if (AtEnd)
                {
                    throw new WireJsonException("Unexpected end of JSON", _pos);
                }

                return _s[_pos];
            }

            private string ReadString()
            {
                _pos++; // opening quote
                StringBuilder? sb = null;
                var runStart = _pos;
                while (true)
                {
                    if (AtEnd)
                    {
                        throw new WireJsonException("Unterminated string", _pos);
                    }

                    var c = _s[_pos];
                    if (c == '"')
                    {
                        string result;
                        if (sb == null)
                        {
                            result = _s.Substring(runStart, _pos - runStart);
                        }
                        else
                        {
                            sb.Append(_s, runStart, _pos - runStart);
                            result = sb.ToString();
                        }

                        _pos++;
                        return result;
                    }

                    if (c < 0x20)
                    {
                        throw new WireJsonException("Control character in string", _pos);
                    }

                    if (c != '\\')
                    {
                        _pos++;
                        continue;
                    }

                    sb ??= new StringBuilder();
                    sb.Append(_s, runStart, _pos - runStart);
                    _pos++; // backslash
                    if (AtEnd)
                    {
                        throw new WireJsonException("Unterminated escape", _pos);
                    }

                    var e = _s[_pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _s.Length)
                            {
                                throw new WireJsonException("Truncated \\u escape", _pos);
                            }

                            if (!int.TryParse(_s.Substring(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                            {
                                throw new WireJsonException("Invalid \\u escape", _pos);
                            }

                            sb.Append((char)code);
                            _pos += 4;
                            break;
                        default:
                            throw new WireJsonException("Invalid escape '\\" + e + "'", _pos - 1);
                    }

                    runStart = _pos;
                }
            }

            private object ReadNumber()
            {
                var start = _pos;
                var isInteger = true;
                if (_s[_pos] == '-')
                {
                    _pos++;
                }

                if (AtEnd || !IsDigit(_s[_pos]))
                {
                    throw new WireJsonException("Invalid number", start);
                }

                if (_s[_pos] == '0')
                {
                    _pos++;
                }
                else
                {
                    while (!AtEnd && IsDigit(_s[_pos]))
                    {
                        _pos++;
                    }
                }

                if (!AtEnd && _s[_pos] == '.')
                {
                    isInteger = false;
                    _pos++;
                    if (AtEnd || !IsDigit(_s[_pos]))
                    {
                        throw new WireJsonException("Invalid number fraction", start);
                    }

                    while (!AtEnd && IsDigit(_s[_pos]))
                    {
                        _pos++;
                    }
                }

                if (!AtEnd && (_s[_pos] == 'e' || _s[_pos] == 'E'))
                {
                    isInteger = false;
                    _pos++;
                    if (!AtEnd && (_s[_pos] == '+' || _s[_pos] == '-'))
                    {
                        _pos++;
                    }

                    if (AtEnd || !IsDigit(_s[_pos]))
                    {
                        throw new WireJsonException("Invalid number exponent", start);
                    }

                    while (!AtEnd && IsDigit(_s[_pos]))
                    {
                        _pos++;
                    }
                }

                var text = _s.Substring(start, _pos - start);
                if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    return l;
                }

                return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private static bool IsDigit(char c) => c >= '0' && c <= '9';
        }
    }

    /// <summary>Typed accessors over parsed <see cref="WireObject"/> values (lenient numeric conversions).</summary>
    internal static class WireValues
    {
        public static string? AsString(object? v) => v switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };

        /// <summary>Integer from a JSON number (integral) or numeric string; null when absent; throws FormatException otherwise.</summary>
        public static long? AsLong(object? v)
        {
            switch (v)
            {
                case null:
                    return null;
                case long l:
                    return l;
                case int i:
                    return i;
                case double d when Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 9.0e15:
                    return (long)Math.Round(d);
                case string s when long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    throw new FormatException("not an integer");
            }
        }

        public static double? AsDouble(object? v)
        {
            switch (v)
            {
                case null:
                    return null;
                case long l:
                    return l;
                case int i:
                    return i;
                case double d:
                    return d;
                case string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    throw new FormatException("not a number");
            }
        }

        public static bool? AsBool(object? v)
        {
            switch (v)
            {
                case null:
                    return null;
                case bool b:
                    return b;
                case string s when bool.TryParse(s.Trim(), out var parsed):
                    return parsed;
                case long l when l == 0 || l == 1:
                    return l == 1;
                default:
                    throw new FormatException("not a boolean");
            }
        }
    }
}
