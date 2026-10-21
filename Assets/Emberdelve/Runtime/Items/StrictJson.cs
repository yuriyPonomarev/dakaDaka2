using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Emberdelve.Items
{
    /// <summary>One parsed JSON value. A tree, so the container reader can reject unknown, missing and duplicate fields.</summary>
    internal sealed class JsonNode
    {
        public enum Kind { Object, Array, String, Number, Bool, Null }

        public Kind Type;
        /// <summary>The string value, or the raw number text exactly as it appeared in the document.</summary>
        public string Text;
        public bool Bool;
        public List<KeyValuePair<string, JsonNode>> Members;
        public List<JsonNode> Items;

        public string Describe()
        {
            return Type.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// R9: a small, strict, culture-independent JSON reader and writer for the persistence formats.
    ///
    /// Why not JsonUtility: it ignores unknown fields (a tamper-evidence hole), silently defaults a missing field
    /// (a missing rarity became Common), and gives no guarantee about how a float is spelled. Here every number is
    /// read and written with the invariant culture; a float is written as round-trip text (bit-exact; the exact spelling depends on the runtime formatter) that parses back to the
    /// same bits ("R", with a "G9" fallback if a runtime ever gets "R" wrong), so 0.09 and 0.1 survive any number of
    /// round trips under en-US, ru-RU or de-DE. The grammar is RFC 8259 (no comments, no trailing commas, no
    /// NaN/Infinity, no leading zeros), with a document size cap and a nesting cap.
    /// </summary>
    internal static class StrictJson
    {
        public const int MaxDocumentLength = 2 * 1024 * 1024;
        public const int MaxDepth = 12;

        // ------------------------------------------------------------------ reading

        public static bool TryParse(string json, out JsonNode root, out string reason)
        {
            root = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                reason = "json document is empty";
                return false;
            }

            if (json.Length > MaxDocumentLength)
            {
                reason = $"json document is {json.Length} characters, above the {MaxDocumentLength} limit";
                return false;
            }

            Parser parser = new Parser(json);
            try
            {
                parser.SkipWhitespace();
                JsonNode value = parser.ParseValue(0);
                parser.SkipWhitespace();
                if (!parser.AtEnd) throw new FormatException($"unexpected content after the document at position {parser.Position}");
                root = value;
                reason = null;
                return true;
            }
            catch (FormatException e)
            {
                reason = "malformed json: " + e.Message;
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;

            public Parser(string s)
            {
                _s = s;
            }

            public bool AtEnd => _i >= _s.Length;
            public int Position => _i;

            public void SkipWhitespace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _i++;
                    else break;
                }
            }

            public JsonNode ParseValue(int depth)
            {
                if (depth > MaxDepth) throw new FormatException($"nesting deeper than {MaxDepth}");
                if (_i >= _s.Length) throw new FormatException("unexpected end of document");

                char c = _s[_i];
                if (c == '{') return ParseObject(depth);
                if (c == '[') return ParseArray(depth);
                if (c == '"') return new JsonNode { Type = JsonNode.Kind.String, Text = ParseString() };
                if (c == '-' || (c >= '0' && c <= '9')) return new JsonNode { Type = JsonNode.Kind.Number, Text = ParseNumber() };
                if (Match("true")) return new JsonNode { Type = JsonNode.Kind.Bool, Bool = true };
                if (Match("false")) return new JsonNode { Type = JsonNode.Kind.Bool, Bool = false };
                if (Match("null")) return new JsonNode { Type = JsonNode.Kind.Null };
                throw new FormatException($"unexpected character '{c}' at position {_i}");
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
                _i += word.Length;
                return true;
            }

            private JsonNode ParseObject(int depth)
            {
                JsonNode node = new JsonNode { Type = JsonNode.Kind.Object, Members = new List<KeyValuePair<string, JsonNode>>() };
                _i++;
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return node;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != '"') throw new FormatException($"expected a member name at position {_i}");
                    string name = ParseString();
                    SkipWhitespace();
                    if (_i >= _s.Length || _s[_i] != ':') throw new FormatException($"expected ':' at position {_i}");
                    _i++;
                    SkipWhitespace();
                    node.Members.Add(new KeyValuePair<string, JsonNode>(name, ParseValue(depth + 1)));
                    SkipWhitespace();
                    if (_i >= _s.Length) throw new FormatException("unterminated object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return node; }
                    throw new FormatException($"expected ',' or '}}' at position {_i}");
                }
            }

            private JsonNode ParseArray(int depth)
            {
                JsonNode node = new JsonNode { Type = JsonNode.Kind.Array, Items = new List<JsonNode>() };
                _i++;
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return node;
                }

                while (true)
                {
                    SkipWhitespace();
                    node.Items.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    if (_i >= _s.Length) throw new FormatException("unterminated array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return node; }
                    throw new FormatException($"expected ',' or ']' at position {_i}");
                }
            }

            private string ParseString()
            {
                StringBuilder sb = new StringBuilder();
                _i++; // opening quote
                while (true)
                {
                    if (_i >= _s.Length) throw new FormatException("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new FormatException($"raw control character in a string at position {_i - 1}");
                    if (c != '\\') { sb.Append(c); continue; }

                    if (_i >= _s.Length) throw new FormatException("unterminated escape");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw new FormatException("truncated \\u escape");
                            int code = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                char h = _s[_i + k];
                                int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                                if (d < 0) throw new FormatException($"bad \\u escape at position {_i}");
                                code = code * 16 + d;
                            }
                            _i += 4;
                            sb.Append((char)code);
                            break;
                        default:
                            throw new FormatException($"unknown escape '\\{e}' at position {_i - 1}");
                    }
                }
            }

            private string ParseNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;

                if (_i >= _s.Length) throw new FormatException("a number needs digits");
                if (_s[_i] == '0') _i++;
                else if (_s[_i] >= '1' && _s[_i] <= '9') { while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++; }
                else throw new FormatException($"bad number at position {start}");

                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    int digits = _i;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                    if (_i == digits) throw new FormatException($"bad number at position {start}: no digits after the point");
                }

                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    int digits = _i;
                    while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
                    if (_i == digits) throw new FormatException($"bad number at position {start}: no exponent digits");
                }

                return _s.Substring(start, _i - start);
            }
        }

        // ------------------------------------------------------------------ strict object mapping

        /// <summary>
        /// Requires the node to be an object whose member names are EXACTLY the expected set: no unknown field,
        /// no duplicate, none missing. This is the "reject unknown fields" tamper-evidence policy.
        /// </summary>
        public static bool TryExpectObject(JsonNode node, string what, string[] fields, out Dictionary<string, JsonNode> map,
            out string reason)
        {
            map = null;
            if (node == null || node.Type != JsonNode.Kind.Object)
            {
                reason = $"{what} must be a json object (got {(node == null ? "nothing" : node.Describe())})";
                return false;
            }

            Dictionary<string, JsonNode> found = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            for (int i = 0; i < node.Members.Count; i++)
            {
                string name = node.Members[i].Key;
                if (Array.IndexOf(fields, name) < 0)
                {
                    reason = $"{what} has an unknown field '{name}'";
                    return false;
                }

                if (found.ContainsKey(name))
                {
                    reason = $"{what} repeats the field '{name}'";
                    return false;
                }

                found.Add(name, node.Members[i].Value);
            }

            for (int i = 0; i < fields.Length; i++)
            {
                if (!found.ContainsKey(fields[i]))
                {
                    reason = $"{what} is missing the field '{fields[i]}'";
                    return false;
                }
            }

            map = found;
            reason = null;
            return true;
        }

        public static bool TryString(JsonNode node, string what, out string value, out string reason)
        {
            if (node == null || node.Type != JsonNode.Kind.String)
            {
                value = null;
                reason = $"{what} must be a string (got {(node == null ? "nothing" : node.Describe())})";
                return false;
            }

            value = node.Text;
            reason = null;
            return true;
        }

        /// <summary>A whole number in int range. "1.0", "1e2" and "01" are not integers here.</summary>
        public static bool TryInt(JsonNode node, string what, out int value, out string reason)
        {
            value = 0;
            if (node == null || node.Type != JsonNode.Kind.Number)
            {
                reason = $"{what} must be a number (got {(node == null ? "nothing" : node.Describe())})";
                return false;
            }

            string text = node.Text;
            if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0 ||
                !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                value = 0;
                reason = $"{what} must be a whole number in int range (got '{text}')";
                return false;
            }

            reason = null;
            return true;
        }

        public static bool TryFloat(JsonNode node, string what, out float value, out string reason)
        {
            value = 0f;
            if (node == null || node.Type != JsonNode.Kind.Number)
            {
                reason = $"{what} must be a number (got {(node == null ? "nothing" : node.Describe())})";
                return false;
            }

            if (!float.TryParse(node.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0f;
                reason = $"{what} is not a finite float (got '{node.Text}')";
                return false;
            }

            // Some runtimes parse "-0" as +0; keep the sign so a value round-trips bit for bit.
            if (value == 0f && node.Text[0] == '-') value = -0f;

            reason = null;
            return true;
        }

        public static bool TryArray(JsonNode node, string what, int maxCount, out List<JsonNode> items, out string reason)
        {
            items = null;
            if (node == null || node.Type != JsonNode.Kind.Array)
            {
                reason = $"{what} must be an array (got {(node == null ? "nothing" : node.Describe())})";
                return false;
            }

            if (node.Items.Count > maxCount)
            {
                reason = $"{what} has {node.Items.Count} elements, above the limit of {maxCount}";
                return false;
            }

            items = node.Items;
            reason = null;
            return true;
        }

        // ------------------------------------------------------------------ writing

        public static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        /// <summary>TEST-ONLY seam: a different float spelling, to prove the checksum does not depend on spelling. Null in production.</summary>
        internal static Func<float, string> SpellFloatForTest;

        /// <summary>The exact bit pattern of a float as a quoted hex string. What checksums hash instead of decimal text.</summary>
        public static void AppendFloatBits(StringBuilder sb, float value)
        {
            sb.Append("\"bits:").Append(BitConverter.SingleToInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture)).Append('"');
        }

        /// <summary>
        /// Compact re-serialization of a parsed tree (members in document order, numbers as written), optionally
        /// leaving out one top-level member. Used to check the integrity of an OLDER-version document before it is
        /// migrated.
        /// </summary>
        public static string Serialize(JsonNode node, string skipTopLevelMember)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, node, skipTopLevelMember);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, JsonNode node, string skip)
        {
            switch (node.Type)
            {
                case JsonNode.Kind.Object:
                    sb.Append('{');
                    bool first = true;
                    for (int i = 0; i < node.Members.Count; i++)
                    {
                        if (skip != null && node.Members[i].Key == skip) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        AppendString(sb, node.Members[i].Key);
                        sb.Append(':');
                        Write(sb, node.Members[i].Value, null);
                    }
                    sb.Append('}');
                    break;
                case JsonNode.Kind.Array:
                    sb.Append('[');
                    for (int i = 0; i < node.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(sb, node.Items[i], null);
                    }
                    sb.Append(']');
                    break;
                case JsonNode.Kind.String: AppendString(sb, node.Text); break;
                case JsonNode.Kind.Number: sb.Append(node.Text); break;
                case JsonNode.Kind.Bool: sb.Append(node.Bool ? "true" : "false"); break;
                default: sb.Append("null"); break;
            }
        }

        public static void AppendInt(StringBuilder sb, int value)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Round-trip invariant text: parses back to the same float bits (the exact spelling is runtime-dependent, which is why checksums hash bit patterns, not this text). A non-finite value has no JSON
        /// spelling, so it is written as null, which every reader refuses for a number field.
        /// </summary>
        public static void AppendFloat(StringBuilder sb, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                sb.Append("null");
                return;
            }

            if (SpellFloatForTest != null)
            {
                sb.Append(SpellFloatForTest(value));
                return;
            }

            // ToString drops the sign of negative zero on some runtimes; spell it out so the bits survive.
            if (value == 0f)
            {
                sb.Append(BitConverter.SingleToInt32Bits(value) < 0 ? "-0" : "0");
                return;
            }

            string text = value.ToString("R", CultureInfo.InvariantCulture);
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float back) ||
                BitConverter.SingleToInt32Bits(back) != BitConverter.SingleToInt32Bits(value))
            {
                text = value.ToString("G9", CultureInfo.InvariantCulture);
            }

            sb.Append(text);
        }
    }
}
