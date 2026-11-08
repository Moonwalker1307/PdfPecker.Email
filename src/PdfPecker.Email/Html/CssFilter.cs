using System.Text;

namespace PdfPecker.Email.Html;

/// <summary>
/// Keeps a message's CSS from reaching outside the message body. Every selector is scoped to the body's wrapper
/// element (html and body become the wrapper itself, as web mail clients do), @page and @import rules and named pages
/// are dropped so the message cannot change the PDF's pages or pull in other files, and fixed or absolute positioning is removed so
/// the body cannot paint over the header PdfPecker.Email writes above it.
/// </summary>
internal static class CssFilter
{
    private const int MaxNesting = 16;

    /// <summary>Appends the scoped form of a style sheet.</summary>
    internal static void AppendScoped(StringBuilder output, string css, string scope)
    {
        var clean = StripComments(css);
        var start = output.Length;
        Scope(output, clean, 0, clean.Length, scope, 0);
        // The CSS goes into a style element, which only "</style" can end; no such text survives the HTML tokenizer,
        // but "</" is broken up anyway so nothing in a sheet can close the element.
        output.Replace("</", "<\\/", start, output.Length - start);
    }

    private static void Scope(StringBuilder output, string css, int start, int end, string scope, int depth)
    {
        var i = start;
        while (i < end)
        {
            var c = css[i];
            if (char.IsWhiteSpace(c) || c == '}' || c == ';')
            {
                i++;
                continue;
            }
            // The old HTML comment markers around a style sheet's content are ignored by CSS too.
            if (string.CompareOrdinal(css, i, "<!--", 0, 4) == 0) { i += 4; continue; }
            if (string.CompareOrdinal(css, i, "-->", 0, 3) == 0) { i += 3; continue; }

            var preludeEnd = FindPreludeEnd(css, i, end);
            if (preludeEnd >= end) return;
            if (css[preludeEnd] == ';')
            {
                // A statement: @import, @charset, @namespace or damage. None of them is kept.
                i = preludeEnd + 1;
                continue;
            }
            var blockEnd = FindBlockEnd(css, preludeEnd + 1, end);
            if (c == '@')
            {
                var nameEnd = i + 1;
                while (nameEnd < preludeEnd && (char.IsAsciiLetterOrDigit(css[nameEnd]) || css[nameEnd] == '-')) nameEnd++;
                var name = css.AsSpan(i + 1, nameEnd - i - 1).ToString().ToLowerInvariant();
                switch (name)
                {
                    case "media" or "supports" or "container" or "layer" when depth < MaxNesting:
                        output.Append(css, i, preludeEnd - i).Append('{');
                        Scope(output, css, preludeEnd + 1, blockEnd, scope, depth + 1);
                        output.Append('}');
                        break;
                    case "font-face":
                        output.Append("@font-face{").Append(FilterDeclarations(css.AsSpan(preludeEnd + 1, blockEnd - preludeEnd - 1))).Append('}');
                        break;
                    case "keyframes" or "-webkit-keyframes" or "counter-style" or "property" or "font-feature-values":
                        output.Append(css, i, Math.Min(blockEnd + 1, end) - i);
                        break;
                    // @page and anything unknown would act outside the body, so they are left out.
                }
            }
            else
            {
                var selectors = ScopeSelectors(css.AsSpan(i, preludeEnd - i), scope);
                if (selectors.Length > 0)
                {
                    output.Append(selectors).Append('{')
                        .Append(FilterDeclarations(css.AsSpan(preludeEnd + 1, blockEnd - preludeEnd - 1))).Append('}');
                }
            }
            i = blockEnd + 1;
        }
    }

    /// <summary>The index of the '{' or ';' that ends a rule's prelude, outside strings and brackets, or end.</summary>
    private static int FindPreludeEnd(string css, int i, int end)
    {
        var depth = 0;
        while (i < end)
        {
            var c = css[i];
            if (c is '"' or '\'') i = SkipString(css, i, end);
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c is '{' or ';') return i;
            i++;
        }
        return end;
    }

    /// <summary>The index of the '}' that closes the block whose content starts at i, or end.</summary>
    private static int FindBlockEnd(ReadOnlySpan<char> css, int i, int end)
    {
        var depth = 1;
        while (i < end)
        {
            var c = css[i];
            if (c is '"' or '\'') i = SkipString(css, i, end);
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
            i++;
        }
        return end;
    }

    // Returns the index of the closing quote (or the end); the caller steps past it.
    private static int SkipString(ReadOnlySpan<char> css, int i, int end)
    {
        var quote = css[i];
        i++;
        while (i < end && css[i] != quote && css[i] != '\n')
        {
            if (css[i] == '\\') i++;
            i++;
        }
        return Math.Min(i, end - 1);
    }

    internal static string ScopeSelectors(ReadOnlySpan<char> prelude, string scope)
    {
        var sb = new StringBuilder(prelude.Length + 32);
        string? first = null;
        HashSet<string>? seen = null;
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= prelude.Length; i++)
        {
            if (i < prelude.Length)
            {
                var c = prelude[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
                if (c != ',' || depth > 0) continue;
            }
            var selector = prelude[start..i].Trim();
            start = i + 1;
            if (selector.IsEmpty) continue;
            var rest = selector;
            var replaced = false;
            while (true)
            {
                var trimmed = rest.TrimStart();
                // "html > body" is the wrapper too; any other combinator after them stays.
                if (replaced && trimmed.Length > 0 && trimmed[0] == '>') trimmed = trimmed[1..].TrimStart();
                if (StartsWithWord(trimmed, "html") || StartsWithWord(trimmed, "body")) rest = trimmed[4..];
                else if (StartsWithWord(trimmed, ":root")) rest = trimmed[5..];
                else break;
                replaced = true;
            }
            var scoped = replaced ? "." + scope + rest.ToString() : "." + scope + " " + selector.ToString();
            // "html, body" both become the wrapper; it is written once.
            if (seen is null ? first == scoped : !seen.Add(scoped)) continue;
            if (first is null) first = scoped;
            else seen ??= [first, scoped];
            if (sb.Length > 0) sb.Append(',');
            sb.Append(scoped);
        }
        return sb.ToString();
    }

    private static bool StartsWithWord(ReadOnlySpan<char> s, string word)
        => s.StartsWith(word, StringComparison.OrdinalIgnoreCase)
           && (s.Length == word.Length || !(char.IsAsciiLetterOrDigit(s[word.Length]) || s[word.Length] is '-' or '_'));

    /// <summary>Filters a declaration list (a rule's block or a style attribute).</summary>
    internal static string FilterDeclarations(ReadOnlySpan<char> block)
    {
        var text = block.IndexOf("/*") >= 0 ? StripComments(block.ToString()).AsSpan() : block;
        var sb = new StringBuilder(text.Length);
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length)
            {
                var c = text[i];
                if (c is '"' or '\'')
                {
                    var quote = c;
                    for (i++; i < text.Length && text[i] != quote; i++)
                    {
                        if (text[i] == '\\') i++;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == '{')
                {
                    // A nested rule (CSS nesting) is dropped with its block.
                    var close = FindBlockEnd(text, i + 1, text.Length);
                    i = close;
                    start = i + 1;
                    continue;
                }
                if (c != ';' || depth > 0) continue;
            }
            var declaration = text[start..Math.Min(i, text.Length)];
            start = i + 1;
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            var property = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            if (property.IsEmpty || value.IsEmpty || !Allowed(property, value)) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(property).Append(':').Append(value);
        }
        return sb.ToString();
    }

    private static bool Allowed(ReadOnlySpan<char> property, ReadOnlySpan<char> value)
    {
        foreach (var c in property)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        }
        if (value.Contains("expression(", StringComparison.OrdinalIgnoreCase) || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (property.Equals("position", StringComparison.OrdinalIgnoreCase))
        {
            return !(value.Contains('\\') || value.Contains("fixed", StringComparison.OrdinalIgnoreCase)
                || value.Contains("absolute", StringComparison.OrdinalIgnoreCase) || value.Contains("sticky", StringComparison.OrdinalIgnoreCase)
                || value.Contains("running", StringComparison.OrdinalIgnoreCase));
        }
        // A named page starts a new PDF page; Word's HTML, which Outlook sends, puts every message in "page: WordSection1".
        if (property.Equals("page", StringComparison.OrdinalIgnoreCase)) return false;
        return !property.Equals("behavior", StringComparison.OrdinalIgnoreCase) && !property.Equals("-moz-binding", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes /* comments */ outside strings.</summary>
    internal static string StripComments(string css)
    {
        var at = css.IndexOf("/*", StringComparison.Ordinal);
        if (at < 0) return css;
        var sb = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c is '"' or '\'')
            {
                var end = i + 1;
                while (end < css.Length && css[end] != c && css[end] != '\n')
                {
                    if (css[end] == '\\') end++;
                    end++;
                }
                end = Math.Min(end + 1, css.Length);
                sb.Append(css, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? css.Length : close + 2;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
