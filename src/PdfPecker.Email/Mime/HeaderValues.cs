using System.Globalization;
using System.Text;

namespace PdfPecker.Email.Mime;

/// <summary>
/// Reads header values: unfolding, RFC 2047 encoded words, Content-Type style parameters with RFC 2231 continuations,
/// address lists and dates. Every reader accepts what real mail contains rather than only what the RFCs allow, and none
/// of them fails: unreadable parts are kept as text or left out.
/// </summary>
internal static class HeaderValues
{
    // An RFC 2047 encoded word is at most 75 characters; the search for its end stops well past that, so a header full
    // of "=?" does not make decoding quadratic.
    private const int MaxEncodedWordLength = 1024;

    /// <summary>A raw header value as text: line folds removed, encoded words left alone, trimmed.</summary>
    internal static string Unfold(ReadOnlySpan<byte> raw)
    {
        // Headers should be ASCII; RFC 6532 allows UTF-8, and old clients write raw Windows-1252.
        var text = Charsets.Guess(raw);
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0) return text.Trim();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c != '\r' && c != '\n') sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Decodes the RFC 2047 encoded words in unstructured text such as a subject.</summary>
    internal static string DecodeWords(string text)
    {
        var start = text.IndexOf("=?", StringComparison.Ordinal);
        if (start < 0) return text;
        var sb = new StringBuilder(text.Length);
        var pending = new List<byte>();
        string? pendingCharset = null;
        var i = 0;
        var lastWasWord = false;
        while (i < text.Length)
        {
            var at = text.IndexOf("=?", i, StringComparison.Ordinal);
            if (at < 0) break;
            if (!TryReadWord(text, at, out var charset, out var bytes, out var end))
            {
                // Not an encoded word: copy up to and including the "=?" and look further on.
                Flush();
                sb.Append(text, i, at + 2 - i);
                i = at + 2;
                lastWasWord = false;
                continue;
            }
            var between = text.AsSpan(i, at - i);
            // Whitespace between two encoded words is not part of the text (RFC 2047 section 6.2).
            if (!(lastWasWord && between.IsWhiteSpace()))
            {
                Flush();
                sb.Append(between);
            }
            // Adjacent words in one charset are joined before decoding, because senders split multi-byte characters
            // across words.
            if (pendingCharset is not null && !pendingCharset.Equals(charset, StringComparison.OrdinalIgnoreCase)) Flush();
            pendingCharset = charset;
            pending.AddRange(bytes);
            i = end;
            lastWasWord = true;
        }
        Flush();
        sb.Append(text, i, text.Length - i);
        return sb.ToString();

        void Flush()
        {
            if (pendingCharset is null) return;
            sb.Append(Charsets.Decode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending), pendingCharset));
            pending.Clear();
            pendingCharset = null;
        }
    }

    private static bool TryReadWord(string text, int at, out string charset, out byte[] bytes, out int end)
    {
        charset = "";
        bytes = [];
        end = 0;
        var limit = Math.Min(text.Length, at + MaxEncodedWordLength);
        var q1 = text.IndexOf('?', at + 2, limit - at - 2);
        if (q1 < 0 || q1 == at + 2 || q1 + 3 >= limit || text[q1 + 2] != '?') return false;
        var encoding = char.ToUpperInvariant(text[q1 + 1]);
        if (encoding != 'B' && encoding != 'Q') return false;
        var close = text.IndexOf("?=", q1 + 3, limit - q1 - 3, StringComparison.Ordinal);
        if (close < 0) return false;
        charset = text[(at + 2)..q1];
        if (charset.AsSpan().IndexOfAny(' ', '\t') >= 0) return false;
        var payload = text.AsSpan(q1 + 3, close - q1 - 3);
        var raw = new byte[payload.Length];
        for (var k = 0; k < payload.Length; k++) raw[k] = payload[k] < 256 ? (byte)payload[k] : (byte)'?';
        bytes = encoding == 'B' ? TransferEncoding.DecodeBase64(raw) : TransferEncoding.DecodeQuotedPrintable(raw, header: true);
        end = close + 2;
        return true;
    }

    /// <summary>Reads "type/subtype; name=value; ..." into its lower-case type and subtype and its parameters.</summary>
    internal static (string Type, string SubType, Dictionary<string, string> Parameters) ParseContentType(string value)
    {
        var semicolon = value.IndexOf(';');
        var media = (semicolon < 0 ? value : value[..semicolon]).Trim();
        var slash = media.IndexOf('/');
        string type = "", sub = "";
        if (slash > 0)
        {
            type = media[..slash].Trim().ToLowerInvariant();
            sub = media[(slash + 1)..].Trim().ToLowerInvariant();
            var space = sub.IndexOfAny([' ', '\t', '"']);
            if (space >= 0) sub = sub[..space];
        }
        return (type, sub, semicolon < 0 ? MimeEntity.EmptyParameters : ParseParameters(value.AsSpan(semicolon + 1)));
    }

    /// <summary>Reads a disposition ("attachment; filename=...") into its lower-case kind and its parameters.</summary>
    internal static (string Kind, Dictionary<string, string> Parameters) ParseDisposition(string value)
    {
        var semicolon = value.IndexOf(';');
        var kind = (semicolon < 0 ? value : value[..semicolon]).Trim().ToLowerInvariant();
        return (kind, semicolon < 0 ? MimeEntity.EmptyParameters : ParseParameters(value.AsSpan(semicolon + 1)));
    }

    /// <summary>
    /// Reads "; name=value" parameters. Values may be quoted, RFC 2231 extended (name*=utf-8''%E2%82%AC) or continued
    /// (name*0=, name*1*=), and, against the RFCs but commonly, RFC 2047 encoded.
    /// </summary>
    internal static Dictionary<string, string> ParseParameters(ReadOnlySpan<char> s)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SortedList<int, (string Value, bool Extended)>>? sections = null;
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ';' || char.IsWhiteSpace(s[i]))) i++;
            var nameStart = i;
            while (i < s.Length && s[i] != '=' && s[i] != ';') i++;
            var name = s[nameStart..i].Trim().ToString();
            if (i >= s.Length || s[i] == ';')
            {
                continue;
            }
            i++; // '='
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            string value;
            var quoted = i < s.Length && s[i] == '"';
            if (quoted)
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i]);
                    i++;
                }
                i++; // closing quote, or past the end when it is missing
                value = sb.ToString();
                // Anything between the closing quote and the next ';' is damage and is skipped.
                while (i < s.Length && s[i] != ';') i++;
            }
            else
            {
                var valueStart = i;
                // A token ends at ';'. Unquoted values with spaces (filename=my file.pdf) are common, so spaces stay.
                while (i < s.Length && s[i] != ';') i++;
                value = s[valueStart..i].Trim().ToString();
            }
            if (name.Length == 0) continue;
            var star = name.IndexOf('*');
            if (star < 0)
            {
                result.TryAdd(name, value);
                continue;
            }
            // RFC 2231: name* is one extended value; name*N and name*N* are its numbered sections.
            var baseName = name[..star];
            var rest = name.AsSpan(star + 1);
            var extended = rest.EndsWith("*");
            if (extended) rest = rest[..^1];
            var index = 0;
            if (rest.Length > 0 && !int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out index)) continue;
            if (rest.Length == 0) extended = true; // name* alone
            if (index > 999) continue;
            sections ??= new(StringComparer.OrdinalIgnoreCase);
            if (!sections.TryGetValue(baseName, out var list)) sections[baseName] = list = new SortedList<int, (string, bool)>();
            list.TryAdd(index, (value, extended));
        }
        if (sections is not null)
        {
            foreach (var (name, list) in sections)
            {
                // The joined RFC 2231 value wins over a plain one of the same name, which senders add for old readers.
                result[name] = JoinSections(list);
            }
        }
        foreach (var key in (ReadOnlySpan<string>)["filename", "name"])
        {
            if (result.TryGetValue(key, out var v) && v.Contains("=?", StringComparison.Ordinal)) result[key] = DecodeWords(v);
        }
        return result;
    }

    private static string JoinSections(SortedList<int, (string Value, bool Extended)> list)
    {
        string? charset = null;
        var bytes = new List<byte>();
        var first = true;
        foreach (var (value, extended) in list.Values)
        {
            var v = value.AsSpan();
            if (extended)
            {
                if (first)
                {
                    // charset'language'text
                    var q1 = v.IndexOf('\'');
                    var q2 = q1 < 0 ? -1 : v[(q1 + 1)..].IndexOf('\'');
                    if (q2 >= 0)
                    {
                        charset = v[..q1].ToString();
                        v = v[(q1 + 1 + q2 + 1)..];
                    }
                }
                for (var k = 0; k < v.Length; k++)
                {
                    if (v[k] == '%' && k + 2 < v.Length && TransferEncoding.Hex((byte)v[k + 1]) >= 0 && TransferEncoding.Hex((byte)v[k + 2]) >= 0
                        && v[k + 1] < 128 && v[k + 2] < 128)
                    {
                        bytes.Add((byte)(TransferEncoding.Hex((byte)v[k + 1]) << 4 | TransferEncoding.Hex((byte)v[k + 2])));
                        k += 2;
                    }
                    else
                    {
                        AppendUtf8(bytes, v[k]);
                    }
                }
            }
            else
            {
                foreach (var c in v) AppendUtf8(bytes, c);
            }
            first = false;
        }
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
        return string.IsNullOrEmpty(charset) ? Charsets.Guess(span) : Charsets.Decode(span, charset);
    }

    private static void AppendUtf8(List<byte> bytes, char c)
    {
        if (c < 128)
        {
            bytes.Add((byte)c);
            return;
        }
        Span<byte> buffer = stackalloc byte[4];
        var n = Encoding.UTF8.GetBytes([c], buffer);
        for (var k = 0; k < n; k++) bytes.Add(buffer[k]);
    }

    /// <summary>Reads an address list ("Name &lt;a@b&gt;, c@d (Comment), group: e@f;").</summary>
    internal static List<EmailAddress> ParseAddresses(string? value)
    {
        var result = new List<EmailAddress>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        var item = new StringBuilder();
        string? angle = null;
        string? comment = null;
        var depth = 0;
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c == '"')
            {
                // A quoted string, kept with its quotes so the display name can tell it was quoted.
                var end = i + 1;
                while (end < value.Length && value[end] != '"')
                {
                    if (value[end] == '\\') end++;
                    end++;
                }
                item.Append(value, i, Math.Min(end + 1, value.Length) - i);
                i = end + 1;
                continue;
            }
            if (c == '(')
            {
                var start = i + 1;
                depth = 1;
                i++;
                while (i < value.Length && depth > 0)
                {
                    if (value[i] == '\\') i++;
                    else if (value[i] == '(') depth++;
                    else if (value[i] == ')') depth--;
                    i++;
                }
                comment ??= value[start..Math.Max(start, i - 1)];
                continue;
            }
            if (c == '<')
            {
                var close = value.IndexOf('>', i + 1);
                if (close < 0) close = value.Length;
                angle = value[(i + 1)..close];
                i = close + 1;
                continue;
            }
            if (c == ',' || c == ';')
            {
                Add();
                i++;
                continue;
            }
            if (c == ':' && angle is null)
            {
                // A group's name ("Undisclosed recipients:") is not an address.
                item.Clear();
                comment = null;
                i++;
                continue;
            }
            item.Append(c);
            i++;
        }
        Add();
        return result;

        void Add()
        {
            var text = item.ToString().Trim();
            string? name;
            string address;
            if (angle is not null)
            {
                address = angle.Trim();
                // An obsolete source route (<@a.example,@b.example:user@c.example>) is dropped.
                var colon = address.LastIndexOf(':');
                if (address.StartsWith('@') && colon > 0) address = address[(colon + 1)..];
                name = text.Length > 0 ? text : comment;
            }
            else
            {
                address = text;
                name = comment;
            }
            address = BidiControls.Remove(address.Replace(" ", "", StringComparison.Ordinal));
            name = CleanName(name);
            if (name is not null && BidiControls.Remove(name) is var visible) name = visible.Length > 0 ? visible : null;
            if (address.Length > 0 || name is not null)
            {
                result.Add(new EmailAddress(name, address));
            }
            item.Clear();
            angle = null;
            comment = null;
        }
    }

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        if (n.Length >= 2 && n[0] == '"' && n[^1] == '"')
        {
            var sb = new StringBuilder(n.Length);
            for (var k = 1; k < n.Length - 1; k++)
            {
                if (n[k] == '\\' && k + 1 < n.Length - 1) k++;
                sb.Append(n[k]);
            }
            n = sb.ToString();
        }
        else
        {
            n = n.Replace("\"", "", StringComparison.Ordinal);
        }
        n = DecodeWords(n);
        // Runs of whitespace (from folding) become one space.
        var collapsed = new StringBuilder(n.Length);
        var space = false;
        foreach (var c in n)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = collapsed.Length > 0;
                continue;
            }
            if (space) collapsed.Append(' ');
            space = false;
            collapsed.Append(c);
        }
        return collapsed.Length == 0 ? null : collapsed.ToString();
    }

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>
    /// Reads an RFC 5322 date ("Sun, 8 Nov 2026 18:04:34 +0100 (CET)"), with the obsolete forms: two-digit years,
    /// zone names and missing seconds. Returns null when the date cannot be read.
    /// </summary>
    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        int? day = null, month = null, year = null, hour = null, minute = null, second = null;
        TimeSpan? offset = null;
        var pos = 0;
        while (pos < value.Length)
        {
            var tokenStart = pos;
            while (pos < value.Length && value[pos] is not (' ' or '\t' or ',')) pos++;
            var token = value.AsSpan(tokenStart, pos - tokenStart);
            pos++;
            if (token.IsEmpty) continue;
            if (token[0] == '(') break; // a trailing comment
            if (token.Contains(':') && hour is null)
            {
                var parts = token.ToString().Split(':');
                if (parts.Length >= 2 && Number(parts[0]) is { } h && Number(parts[1]) is { } m)
                {
                    hour = h;
                    minute = m;
                    second = parts.Length > 2 ? Number(parts[2]) ?? 0 : 0;
                }
                continue;
            }
            if ((token[0] == '+' || token[0] == '-') && token.Length == 5 && Number(token[1..].ToString()) is { } z)
            {
                var span = new TimeSpan(z / 100, z % 100, 0);
                offset = token[0] == '-' ? -span : span;
                continue;
            }
            if (char.IsAsciiDigit(token[0]))
            {
                if (Number(token.ToString()) is not { } n) continue;
                if (day is null && token.Length <= 2 && month is null) day = n;
                else if (day is null && token.Length <= 2) day = n; // "Nov 8 2026" order
                else if (year is null) year = token.Length <= 2 ? (n < 50 ? 2000 + n : 1900 + n) : token.Length == 3 ? 1900 + n : n;
                continue;
            }
            if (token.Length >= 3 && month is null)
            {
                var m = Array.IndexOf(Months, token[..3].ToString().ToLowerInvariant());
                if (m >= 0)
                {
                    month = m + 1;
                    continue;
                }
            }
            if (offset is null) offset = ZoneName(token);
        }
        if (day is null || month is null || year is null) return null;
        try
        {
            var o = offset ?? TimeSpan.Zero;
            if (o.Duration() > TimeSpan.FromHours(14)) o = TimeSpan.Zero;
            return new DateTimeOffset(year.Value, month.Value, day.Value, hour ?? 0, minute ?? 0, Math.Min(second ?? 0, 59), o);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int? Number(string s)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < 100_000 ? n : null;

    private static TimeSpan? ZoneName(ReadOnlySpan<char> token) => token.ToString().ToUpperInvariant() switch
    {
        "UT" or "UTC" or "GMT" or "Z" => TimeSpan.Zero,
        "EST" => TimeSpan.FromHours(-5),
        "EDT" => TimeSpan.FromHours(-4),
        "CST" => TimeSpan.FromHours(-6),
        "CDT" => TimeSpan.FromHours(-5),
        "MST" => TimeSpan.FromHours(-7),
        "MDT" => TimeSpan.FromHours(-6),
        "PST" => TimeSpan.FromHours(-8),
        "PDT" => TimeSpan.FromHours(-7),
        _ => null,
    };
}
