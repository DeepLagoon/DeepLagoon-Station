using System.Globalization;
using System.Text;

namespace Content.Client._DeepLagoon.WebUI;

/// <summary>Sandbox-compatible scalar action arguments. Consumers validate their own fields.</summary>
public sealed class TguiActionData
{
    private readonly IReadOnlyDictionary<string, string> _root;
    private readonly Dictionary<string, TguiActionData> _objects = new(StringComparer.Ordinal);
    private TguiActionData(IReadOnlyDictionary<string, string> root) => _root = root;
    public string? String(string key) => _root.TryGetValue(key, out var value) ? value : null;
    public TguiActionData? Object(string key) => _objects.GetValueOrDefault(key);
    public bool TryInt(string key, out int value) => int.TryParse(String(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    public bool TryFloat(string key, out float value) => float.TryParse(String(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && float.IsFinite(value);

    public static bool TryParse(string payload, out TguiActionData? data, int maxLength = 8192)
    {
        data = null;
        // Content must run on the signed official WebView module. Do not depend
        // on fork-only JSON helpers or System.Text.Json (not sandbox-exposed).
        if (maxLength is < 1 or > 1048576 || payload.Length > maxLength) return false;
        var root = new ScalarJsonReader(payload).Read();
        if (root == null) return false;
        data = root;
        return true;
    }

    /// <summary>Strict, bounded JSON reader; arrays are validated without retaining their contents.</summary>
    private sealed class ScalarJsonReader(string text)
    {
        private int _position;

        public TguiActionData? Read()
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var data = new TguiActionData(fields);
            if (!Object(1, fields, data._objects)) return null;
            Whitespace();
            return _position == text.Length ? data : null;
        }

        private bool Object(int depth, Dictionary<string, string>? fields, Dictionary<string, TguiActionData>? objects = null)
        {
            if (depth > 16 || !Take('{')) return false;
            if (Take('}')) return true;
            var names = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                if (!String(out var key) || !names.Add(key) || !Take(':')) return false;
                Whitespace();
                if (At('{') && objects != null)
                {
                    var nestedFields = new Dictionary<string, string>(StringComparer.Ordinal);
                    var nested = new TguiActionData(nestedFields);
                    if (!Object(depth + 1, nestedFields, nested._objects)) return false;
                    objects.Add(key, nested);
                }
                else
                {
                    if (!Value(depth, out var value)) return false;
                    if (value != null && fields != null) fields.Add(key, value);
                }
                if (Take('}')) return true;
            } while (Take(','));
            return false;
        }

        private bool Value(int depth, out string? value)
        {
            value = null;
            Whitespace();
            if (_position == text.Length) return false;
            switch (text[_position])
            {
                case '"':
                    if (!String(out var stringValue)) return false;
                    value = stringValue;
                    return true;
                case '{': return Object(depth + 1, null);
                case '[':
                    if (depth >= 16) return false;
                    _position++;
                    if (Take(']')) return true;
                    do
                    {
                        if (!Value(depth + 1, out _)) return false;
                        if (Take(']')) return true;
                    } while (Take(','));
                    return false;
                case 't': value = "true"; return Literal("true");
                case 'f': value = "false"; return Literal("false");
                case 'n': return Literal("null");
                default: return Number(out value);
            }
        }

        private bool Number(out string? value)
        {
            value = null;
            var start = _position;
            if (At('-')) _position++;
            if (At('0')) _position++;
            else
            {
                if (_position == text.Length || text[_position] is < '1' or > '9') return false;
                Digits();
            }
            if (At('.'))
            {
                _position++;
                if (!Digits()) return false;
            }
            if (At('e') || At('E'))
            {
                _position++;
                if (At('+') || At('-')) _position++;
                if (!Digits()) return false;
            }
            value = text.Substring(start, _position - start);
            return true;
        }

        private bool Digits()
        {
            var start = _position;
            while (_position < text.Length && text[_position] is >= '0' and <= '9') _position++;
            return _position != start;
        }

        private bool String(out string value)
        {
            value = "";
            if (!Take('"')) return false;
            var result = new StringBuilder();
            while (_position < text.Length)
            {
                var c = text[_position++];
                if (c == '"')
                {
                    value = result.ToString();
                    return true;
                }
                if (c < ' ') return false;
                if (c == '\\')
                {
                    if (_position == text.Length) return false;
                    c = text[_position++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': break;
                        case 'b': c = '\b'; break;
                        case 'f': c = '\f'; break;
                        case 'n': c = '\n'; break;
                        case 'r': c = '\r'; break;
                        case 't': c = '\t'; break;
                        case 'u':
                            if (!Hex(out c)) return false;
                            if (char.IsHighSurrogate(c))
                            {
                                if (!At('\\')) return false;
                                _position++;
                                if (!At('u')) return false;
                                _position++;
                                if (!Hex(out var low) || !char.IsLowSurrogate(low)) return false;
                                result.Append(c).Append(low);
                                continue;
                            }
                            if (char.IsLowSurrogate(c)) return false;
                            break;
                        default: return false;
                    }
                }
                else if (char.IsHighSurrogate(c))
                {
                    if (_position == text.Length || !char.IsLowSurrogate(text[_position])) return false;
                    result.Append(c).Append(text[_position++]);
                    continue;
                }
                else if (char.IsLowSurrogate(c)) return false;
                result.Append(c);
            }
            return false;
        }

        private bool Hex(out char value)
        {
            value = default;
            if (text.Length - _position < 4) return false;
            var code = 0;
            for (var i = 0; i < 4; i++)
            {
                var digit = text[_position++];
                var number = digit switch
                {
                    >= '0' and <= '9' => digit - '0',
                    >= 'a' and <= 'f' => digit - 'a' + 10,
                    >= 'A' and <= 'F' => digit - 'A' + 10,
                    _ => -1
                };
                if (number < 0) return false;
                code = code * 16 + number;
            }
            value = (char) code;
            return true;
        }

        private bool Literal(string expected)
        {
            if (text.Length - _position < expected.Length) return false;
            foreach (var c in expected)
                if (text[_position++] != c) return false;
            return true;
        }

        private bool At(char c) => _position < text.Length && text[_position] == c;
        private bool Take(char c)
        {
            Whitespace();
            if (!At(c)) return false;
            _position++;
            return true;
        }

        private void Whitespace()
        {
            while (_position < text.Length && text[_position] is ' ' or '\t' or '\r' or '\n') _position++;
        }
    }
}
