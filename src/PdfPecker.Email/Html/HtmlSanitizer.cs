using System.Collections.Frozen;
using System.Text;

namespace PdfPecker.Email.Html;

/// <summary>
/// Rewrites a message's HTML into a balanced fragment that holds only presentational markup. It tokenizes the HTML
/// the way browsers do (comments, raw-text elements, attributes in any quoting) and writes back only allowed elements
/// and attributes: scripts, frames, objects, forms, SVG and MathML are dropped with their content, unknown elements
/// (Outlook's o:p, for example) keep their content, links keep only http, https, mailto and tel, and images keep only
/// sources the message itself carries unless the caller allows remote images. Style elements are collected and
/// scoped by <see cref="CssFilter"/>. The work is a single pass, linear in the size of the HTML.
/// </summary>
internal sealed class HtmlSanitizer
{
    // Deeper nesting than this is flattened: tags are dropped and their text kept, so layout recursion stays bounded.
    private const int MaxDepth = 200;
    private const int MaxAttributes = 64;

    // Longer links are written as plain text. A link's URL is repeated in the link annotation of every line it covers,
    // so a huge URL on a long run of text would make the PDF grow with the square of its length.
    internal const int MaxLinkLength = 4096;

    private static readonly FrozenSet<string> Allowed = FrozenSet.ToFrozenSet(
    [
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "caption", "center",
        "cite", "code", "colgroup", "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font",
        "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "i", "ins", "kbd", "li", "main", "mark", "nav", "ol", "p",
        "pre", "q", "rp", "rt", "ruby", "s", "samp", "section", "small", "span", "strike", "strong", "sub", "summary", "sup",
        "table", "tbody", "td", "tfoot", "th", "thead", "time", "tr", "tt", "u", "ul", "var",
    ], StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedVoid = FrozenSet.ToFrozenSet(["br", "hr", "img", "wbr", "col"], StringComparer.Ordinal);

    // Elements whose content is not text: everything up to the matching end tag is dropped.
    private static readonly FrozenSet<string> DroppedWithContent = FrozenSet.ToFrozenSet(
        ["object", "applet", "template", "svg", "math", "select", "frameset", "audio", "video", "datalist"], StringComparer.Ordinal);

    // Elements whose content the tokenizer reads as raw text up to "</name".
    private static readonly FrozenSet<string> RawText = FrozenSet.ToFrozenSet(
        ["script", "style", "title", "textarea", "xmp", "iframe", "noembed", "noframes", "plaintext"], StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedAttributes = FrozenSet.ToFrozenSet(
    [
        "style", "class", "id", "title", "dir", "lang", "align", "valign", "width", "height", "bgcolor", "color", "border",
        "cellpadding", "cellspacing", "colspan", "rowspan", "face", "size", "alt", "nowrap", "abbr", "headers", "scope",
        "start", "type", "reversed", "value", "span", "datetime", "clear", "hspace", "vspace", "frame", "rules", "compact",
        "noshade", "open", "summary", "name", "role", "aria-label", "aria-hidden",
    ], StringComparer.Ordinal);

    // Starting one of these closes an open paragraph, as in a browser.
    private static readonly FrozenSet<string> ClosesParagraph = FrozenSet.ToFrozenSet(
    [
        "address", "article", "aside", "blockquote", "center", "details", "div", "dl", "figure", "footer", "h1", "h2", "h3",
        "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
    ], StringComparer.Ordinal);

    private readonly string _html;
    private readonly StringBuilder _out;
    private readonly StringBuilder _css;
    private readonly Func<string, bool> _imageAllowed;
    private readonly string _scope;
    private readonly List<string> _open = [];
    private readonly List<(string Name, string Value, char Quote)> _attributes = [];
    private string? _skipName;
    private int _skipDepth;
    private bool _bodySeen;

    private HtmlSanitizer(string html, StringBuilder output, StringBuilder css, Func<string, bool> imageAllowed, string scope)
    {
        _html = html;
        _out = output;
        _css = css;
        _imageAllowed = imageAllowed;
        _scope = scope;
    }

    /// <summary>The body element's style attribute, which the caller puts on the wrapper element instead.</summary>
    internal string? BodyStyle { get; private set; }

    /// <summary>
    /// Declarations from the body's bgcolor and text attributes. As presentational hints they lose to the message's
    /// own CSS, so the caller writes them in a rule before the message's style sheets rather than inline.
    /// </summary>
    internal string? BodyHints { get; private set; }

    internal string? BodyClass { get; private set; }

    /// <summary>
    /// Sanitises <paramref name="html"/>, appending the fragment to <paramref name="output"/> and the scoped CSS of its
    /// style elements to <paramref name="css"/>. <paramref name="imageAllowed"/> decides which image sources stay.
    /// </summary>
    internal static HtmlSanitizer Sanitize(string html, StringBuilder output, StringBuilder css, Func<string, bool> imageAllowed, string scope)
    {
        var sanitizer = new HtmlSanitizer(html, output, css, imageAllowed, scope);
        sanitizer.Run();
        return sanitizer;
    }

    private void Run()
    {
        var html = _html;
        var n = html.Length;
        var i = 0;
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                Text(i, n);
                break;
            }
            Text(i, lt);
            i = lt;
            var c = lt + 1 < n ? html[lt + 1] : '\0';
            if (c == '!')
            {
                // A comment ends at "-->" ("<!-->" is an empty one); doctypes, CDATA and other "<!" markup at '>'.
                if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
                {
                    var end = html.IndexOf("-->", lt + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                }
                else
                {
                    var end = html.IndexOf('>', lt + 2);
                    i = end < 0 ? n : end + 1;
                }
            }
            else if (c == '?')
            {
                var end = html.IndexOf('>', lt + 2);
                i = end < 0 ? n : end + 1;
            }
            else if (c == '/')
            {
                i = EndTag(lt + 2);
            }
            else if (char.IsAsciiLetter(c))
            {
                i = StartTag(lt + 1);
            }
            else
            {
                if (_skipDepth == 0) _out.Append("&lt;");
                i = lt + 1;
            }
        }
        for (var k = _open.Count - 1; k >= 0; k--) _out.Append("</").Append(_open[k]).Append('>');
        _open.Clear();
    }

    private void Text(int start, int end)
    {
        if (_skipDepth > 0 || end <= start) return;
        var text = _html.AsSpan(start, end - start);
        // Text keeps its character references as written, so it reads exactly as the browser would read it; it can
        // hold no markup, because it ends at the next '<'. NUL characters are dropped.
        if (text.IndexOf('\0') < 0)
        {
            _out.Append(text);
            return;
        }
        foreach (var ch in text)
        {
            if (ch != '\0') _out.Append(ch);
        }
    }

    private int ReadName(int i, out string name)
    {
        var start = i;
        while (i < _html.Length && !IsSpace(_html[i]) && _html[i] != '/' && _html[i] != '>') i++;
        name = _html.AsSpan(start, i - start).ToString().ToLowerInvariant();
        return i;
    }

    private int EndTag(int i)
    {
        var html = _html;
        if (i >= html.Length) return html.Length;
        if (!char.IsAsciiLetter(html[i]))
        {
            // "</>" is nothing; "</ ..." and similar are bogus comments up to '>'.
            var gt = html.IndexOf('>', i);
            return gt < 0 ? html.Length : gt + 1;
        }
        i = ReadName(i, out var name);
        var close = html.IndexOf('>', i);
        var next = close < 0 ? html.Length : close + 1;
        if (close < 0) return next; // a tag cut off by the end of the input is dropped, as in a browser
        if (_skipDepth > 0)
        {
            if (name == _skipName && --_skipDepth == 0) _skipName = null;
            return next;
        }
        if (name == "br")
        {
            _out.Append("<br>"); // browsers read "</br>" as "<br>"
            return next;
        }
        var index = _open.LastIndexOf(name);
        if (index < 0) return next;
        for (var k = _open.Count - 1; k >= index; k--) _out.Append("</").Append(_open[k]).Append('>');
        _open.RemoveRange(index, _open.Count - index);
        return next;
    }

    private int StartTag(int i)
    {
        var html = _html;
        i = ReadName(i, out var name);
        i = ReadAttributes(i, out var complete);
        if (!complete) return html.Length;

        if (RawText.Contains(name))
        {
            var contentStart = i;
            var end = FindEndTag(name, i);
            var contentEnd = end < 0 ? html.Length : end;
            if (name == "style" && _skipDepth == 0)
            {
                CssFilter.AppendScoped(_css, html.Substring(contentStart, contentEnd - contentStart), _scope);
                _css.Append('\n');
            }
            if (end < 0) return html.Length;
            var gt = html.IndexOf('>', end);
            return gt < 0 ? html.Length : gt + 1;
        }
        if (_skipDepth > 0)
        {
            if (name == _skipName) _skipDepth++;
            return i;
        }
        if (DroppedWithContent.Contains(name))
        {
            if (!SelfClosing(i))
            {
                _skipName = name;
                _skipDepth = 1;
            }
            return i;
        }
        if (name == "body")
        {
            ReadBodyAttributes();
            return i;
        }
        if (AllowedVoid.Contains(name))
        {
            if (_open.Count < MaxDepth) WriteTag(name);
            return i;
        }
        if (!Allowed.Contains(name)) return i;

        CloseImplied(name);
        if (_open.Count >= MaxDepth) return i;
        WriteTag(name);
        _open.Add(name);
        return i;
    }

    private bool SelfClosing(int next) => next >= 2 && _html[next - 2] == '/';

    /// <summary>Closes the elements a browser closes implicitly when <paramref name="name"/> starts.</summary>
    private void CloseImplied(string name)
    {
        switch (name)
        {
            case "li":
                PopIfTop("li");
                break;
            case "dt" or "dd":
                if (!PopIfTop("dt")) PopIfTop("dd");
                break;
            case "td" or "th":
                if (!PopIfTop("td")) PopIfTop("th");
                break;
            case "tr":
                if (!PopIfTop("td")) PopIfTop("th");
                PopIfTop("tr");
                break;
            case "thead" or "tbody" or "tfoot":
                if (!PopIfTop("td")) PopIfTop("th");
                PopIfTop("tr");
                if (!PopIfTop("thead") && !PopIfTop("tbody")) PopIfTop("tfoot");
                break;
        }
        if (ClosesParagraph.Contains(name)) PopIfTop("p");
    }

    private bool PopIfTop(string name)
    {
        if (_open.Count == 0 || _open[^1] != name) return false;
        _out.Append("</").Append(name).Append('>');
        _open.RemoveAt(_open.Count - 1);
        return true;
    }

    /// <summary>Reads attributes up to the tag's '>' into the attribute list; complete is false when the input ends first.</summary>
    private int ReadAttributes(int i, out bool complete)
    {
        var html = _html;
        var n = html.Length;
        _attributes.Clear();
        while (true)
        {
            while (i < n && (IsSpace(html[i]) || html[i] == '/')) i++;
            if (i >= n)
            {
                complete = false;
                return n;
            }
            if (html[i] == '>')
            {
                complete = true;
                return i + 1;
            }
            var nameStart = i;
            i++; // a name may start with '='
            while (i < n && !IsSpace(html[i]) && html[i] is not ('/' or '>' or '=')) i++;
            var attrName = html.AsSpan(nameStart, i - nameStart);
            while (i < n && IsSpace(html[i])) i++;
            string value = "";
            var quote = '\0';
            if (i < n && html[i] == '=')
            {
                i++;
                while (i < n && IsSpace(html[i])) i++;
                if (i < n && html[i] is '"' or '\'')
                {
                    quote = html[i];
                    var close = html.IndexOf(quote, i + 1);
                    if (close < 0)
                    {
                        complete = false;
                        return n;
                    }
                    value = html.Substring(i + 1, close - i - 1);
                    i = close + 1;
                }
                else
                {
                    var start = i;
                    while (i < n && !IsSpace(html[i]) && html[i] != '>') i++;
                    value = html.Substring(start, i - start);
                }
            }
            if (_attributes.Count < MaxAttributes)
            {
                // As in a browser, NUL in a value is U+FFFD; dropping it later would let "expre\0ssion" pass the
                // filters and come out as "expression".
                if (value.Contains('\0', StringComparison.Ordinal)) value = value.Replace('\0', '\uFFFD');
                _attributes.Add((attrName.ToString().ToLowerInvariant(), value, quote));
            }
        }
    }

    private void WriteTag(string name)
    {
        _out.Append('<').Append(name);
        foreach (var (attrName, raw, _) in _attributes)
        {
            if (attrName == "href")
            {
                if (name == "a" && SafeLink(raw) is { } link) AppendAttribute("href", link);
                continue;
            }
            if (attrName == "src")
            {
                if (name == "img" && SafeImage(raw) is { } src) AppendAttribute("src", src);
                continue;
            }
            if (attrName == "style")
            {
                var style = CssFilter.FilterDeclarations(Markup.DecodeReferences(raw));
                if (style.Length > 0) AppendAttribute("style", style);
                continue;
            }
            if (!AllowedAttributes.Contains(attrName)) continue;
            // Other values are written as they were, references and all, so they read the same; only the quote is
            // escaped, because every value is written in double quotes.
            _out.Append(' ').Append(attrName).Append("=\"");
            foreach (var ch in raw)
            {
                if (ch == '"') _out.Append("&quot;");
                else if (ch == '<') _out.Append("&lt;");
                else if (ch != '\0') _out.Append(ch);
            }
            _out.Append('"');
        }
        _out.Append('>');
    }

    private void AppendAttribute(string name, string decodedValue)
        => _out.Append(' ').Append(name).Append("=\"").AppendEscaped(decodedValue).Append('"');

    private void ReadBodyAttributes()
    {
        if (_bodySeen) return;
        _bodySeen = true;
        var hints = new StringBuilder();
        foreach (var (attrName, raw, _) in _attributes)
        {
            var value = Markup.DecodeReferences(raw).Trim();
            switch (attrName)
            {
                case "bgcolor" when IsColor(value):
                    hints.Append("background-color:").Append(value).Append(';');
                    break;
                case "text" when IsColor(value):
                    hints.Append("color:").Append(value).Append(';');
                    break;
                case "style":
                    var style = CssFilter.FilterDeclarations(value);
                    if (style.Length > 0) BodyStyle = style;
                    break;
                case "class":
                    BodyClass = value;
                    break;
            }
        }
        BodyHints = hints.Length > 0 ? hints.ToString() : null;
    }

    private static bool IsColor(string value)
    {
        if (value.Length is 0 or > 32) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '#') return false;
        }
        return true;
    }

    /// <summary>The link if it is http, https, mailto, tel or a fragment; otherwise null.</summary>
    internal static string? SafeLink(string raw)
    {
        var value = Markup.DecodeReferences(raw).Trim();
        if (value.Length is 0 or > MaxLinkLength) return null;
        if (value[0] == '#') return value;
        var scheme = Scheme(value);
        return scheme is "http" or "https" or "mailto" or "tel" ? value : null;
    }

    private string? SafeImage(string raw)
    {
        var value = Markup.DecodeReferences(raw).Trim();
        if (value.Length == 0) return null;
        var scheme = Scheme(value);
        if (scheme == "data") return value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ? value : null;
        return _imageAllowed(value) ? value : null;
    }

    /// <summary>The lower-case scheme of a URL, ignoring whitespace and control characters inside it, or null.</summary>
    internal static string? Scheme(string url)
    {
        var sb = new StringBuilder(16);
        foreach (var c in url)
        {
            if (c <= ' ') continue; // browsers ignore tabs and newlines inside a scheme
            if (c == ':') return sb.Length > 0 ? sb.ToString() : null;
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') || sb.Length > 32) return null;
            sb.Append(char.ToLowerInvariant(c));
        }
        return null;
    }

    /// <summary>The index of "&lt;/name" ending a raw-text element, or -1.</summary>
    private int FindEndTag(string name, int from)
    {
        var html = _html;
        while (true)
        {
            var at = html.IndexOf("</", from, StringComparison.Ordinal);
            if (at < 0) return -1;
            var end = at + 2 + name.Length;
            if (end <= html.Length && string.Compare(html, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == html.Length || IsSpace(html[end]) || html[end] is '/' or '>'))
                return at;
            from = at + 2;
        }
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';
}
