using System.Text;

namespace PdfPecker.Email.Html;

/// <summary>
/// Turns a plain text body into HTML: format=flowed paragraphs (RFC 3676) are joined again, the text is escaped, and
/// web and mail addresses become links. Line breaks and spacing are kept by the CSS (white-space: pre-wrap).
/// </summary>
internal static class PlainText
{
    internal static void AppendHtml(StringBuilder output, string text, bool flowed, bool deleteSpace)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (flowed) text = Unflow(text, deleteSpace);
        var i = 0;
        while (i < text.Length)
        {
            var link = NextLink(text, i, out var linkEnd);
            if (link < 0)
            {
                output.AppendEscaped(text.AsSpan(i));
                break;
            }
            output.AppendEscaped(text.AsSpan(i, link - i));
            var url = text[link..linkEnd];
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
            output.Append("<a href=\"").AppendEscaped(href).Append("\">").AppendEscaped(url).Append("</a>");
            i = linkEnd;
        }
    }

    /// <summary>Joins format=flowed lines: a line ending in a space continues on the next line of the same quote depth.</summary>
    internal static string Unflow(string text, bool deleteSpace)
    {
        var sb = new StringBuilder(text.Length);
        var lines = text.Split('\n');
        var previousDepth = -1;
        var continuing = false;
        foreach (var raw in lines)
        {
            var line = raw.AsSpan();
            var depth = 0;
            while (depth < line.Length && line[depth] == '>') depth++;
            var content = line[depth..];
            // Space-stuffing: one leading space is added by the sender and removed by the reader.
            if (content.Length > 0 && content[0] == ' ') content = content[1..];
            var isSignature = content.SequenceEqual("-- ");
            if (continuing && depth == previousDepth)
            {
                // Joined onto the previous line.
            }
            else
            {
                if (previousDepth >= 0) sb.Append('\n');
                if (depth > 0) sb.Append('>', depth).Append(' ');
            }
            var soft = !isSignature && content.Length > 0 && content[^1] == ' ';
            sb.Append(soft && deleteSpace ? content[..^1] : content);
            continuing = soft;
            previousDepth = depth;
        }
        return sb.ToString();
    }

    private static int NextLink(string text, int from, out int end)
    {
        end = 0;
        var i = from;
        while (i < text.Length)
        {
            var at = IndexOfStart(text, i);
            if (at < 0) return -1;
            // A link starts at a word boundary.
            if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '@' or '/' or '.'))
            {
                i = at + 1;
                continue;
            }
            var e = at;
            while (e < text.Length && !char.IsWhiteSpace(text[e]) && text[e] is not ('<' or '>' or '"' or '\'' or '`')) e++;
            // Trailing punctuation belongs to the sentence, and a closing bracket only when it has no opening one. The
            // brackets are counted once, so a run of them is not counted again for every one taken off.
            var open = text.AsSpan(at, e - at).Count('(');
            var close = text.AsSpan(at, e - at).Count(')');
            while (e > at)
            {
                var last = text[e - 1];
                if (last is '.' or ',' or ';' or ':' or '!' or '?') { e--; continue; }
                if (last == ')' && open < close) { e--; close--; continue; }
                break;
            }
            var minimum = text[at] is 'w' or 'W' ? 5 : text[at] is 'm' or 'M' ? 8 : 9;
            if (e - at > minimum && e - at <= HtmlSanitizer.MaxLinkLength)
            {
                end = e;
                return at;
            }
            // Past a run too long to be a link, so a huge run of "http://" is scanned once, not once per start.
            i = e - at > HtmlSanitizer.MaxLinkLength ? e : at + 1;
        }
        return -1;
    }

    private static readonly System.Buffers.SearchValues<char> StartLetters = System.Buffers.SearchValues.Create("hHwWmM");

    // One pass over the text for the first letter of any prefix: searching for each prefix in turn would scan the rest
    // of the text for the prefixes that are not there once per candidate, which is quadratic.
    private static int IndexOfStart(string text, int from)
    {
        var i = from;
        while (i < text.Length)
        {
            var next = text.AsSpan(i).IndexOfAny(StartLetters);
            if (next < 0) return -1;
            var at = i + next;
            var rest = text.AsSpan(at);
            if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return at;
            i = at + 1;
        }
        return -1;
    }
}
