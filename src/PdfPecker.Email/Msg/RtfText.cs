using System.Globalization;
using System.Text;

namespace PdfPecker.Email.Msg;

/// <summary>
/// Reads the text out of an RTF body. Outlook keeps HTML mail as HTML encapsulated in RTF (MS-OXRTFEX, \fromhtml1):
/// then the HTML is recovered from the \*\htmltag groups and the text outside \htmlrtf runs. Other RTF becomes plain
/// text: paragraphs, tabs and characters, without formatting. Group nesting is bounded and the work is linear.
/// </summary>
internal static class RtfText
{
    private const int MaxDepth = 512;

    // Destinations whose content is not body text.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "footerl", "footerr",
        "object", "listtable", "listoverridetable", "revtbl", "rsidtbl", "generator", "xmlnstbl", "themedata",
        "colorschememapping", "latentstyles", "datastore", "fldinst", "bkmkstart", "bkmkend", "pgdsctbl", "mmathPr",
    };

    /// <summary>The text of the RTF, and whether it is encapsulated HTML.</summary>
    internal static (string Text, bool IsHtml) Read(ReadOnlySpan<byte> rtf)
    {
        var html = rtf[..Math.Min(rtf.Length, 1024)].IndexOf("\\fromhtml1"u8) >= 0;
        var output = new StringBuilder(rtf.Length / 2);
        var pending = new List<byte>();
        var encoding = CodePage(1252);
        // Per group: skipping a destination, inside \htmlrtf, characters to skip after \u, and being an \htmltag group.
        var stack = new Stack<(bool Skip, bool HtmlRtf, int Uc, bool Tag)>();
        bool skip = false, htmlRtf = false, tag = false;
        var uc = 1;
        var skipChars = 0;
        var i = 0;

        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case (byte)'{':
                    if (stack.Count < MaxDepth) stack.Push((skip, htmlRtf, uc, tag));
                    i++;
                    // "{\*\name" marks an optional destination: skipped unless it is one we read.
                    if (i + 1 < rtf.Length && rtf[i] == '\\' && rtf[i + 1] == '*')
                    {
                        var j = i + 2;
                        while (j < rtf.Length && rtf[j] is (byte)' ' or (byte)'\r' or (byte)'\n') j++;
                        var word = ReadWord(rtf, j + 1, out _, out _);
                        if (j < rtf.Length && rtf[j] == '\\' && word.StartsWith("htmltag", StringComparison.Ordinal) && html) tag = true;
                        else if (j < rtf.Length && rtf[j] == '\\' && word == "mhtmltag" && html) skip = true;
                        else skip = true;
                        i = j;
                    }
                    continue;
                case (byte)'}':
                    Flush();
                    if (stack.Count > 0) (skip, htmlRtf, uc, tag) = stack.Pop();
                    i++;
                    continue;
                case (byte)'\\':
                    break;
                case (byte)'\r' or (byte)'\n':
                    i++;
                    continue;
                default:
                    if (skipChars > 0) skipChars--;
                    else if (Visible()) pending.Add(c);
                    i++;
                    continue;
            }

            // A control word or symbol.
            if (i + 1 >= rtf.Length) break;
            var next = rtf[i + 1];
            if (next is (byte)'\\' or (byte)'{' or (byte)'}')
            {
                if (skipChars > 0) skipChars--;
                else if (Visible()) pending.Add(next);
                i += 2;
                continue;
            }
            if (next == '\'')
            {
                if (i + 3 < rtf.Length && Hex(rtf[i + 2]) >= 0 && Hex(rtf[i + 3]) >= 0)
                {
                    if (skipChars > 0) skipChars--;
                    else if (Visible()) pending.Add((byte)(Hex(rtf[i + 2]) << 4 | Hex(rtf[i + 3])));
                    i += 4;
                }
                else
                {
                    i += 2;
                }
                continue;
            }
            if (!IsLetter(next))
            {
                // Control symbols: \~ is a non-breaking space, \- an optional hyphen, \_ a non-breaking hyphen.
                if (Visible() && skipChars == 0)
                {
                    if (next == '~') Append(" ");
                    else if (next == '_') Append("‑");
                    else if (next is (byte)'\r' or (byte)'\n') Append("\n");
                }
                i += 2;
                continue;
            }
            var name = ReadWord(rtf, i + 1, out var parameter, out var end);
            i = end;
            switch (name)
            {
                case "ansicpg" when parameter is { } cp:
                    Flush();
                    encoding = CodePage(cp);
                    break;
                case "u" when parameter is { } code:
                    Flush();
                    if (Visible()) output.Append((char)(code < 0 ? code + 65536 : code));
                    skipChars = uc;
                    break;
                case "uc" when parameter is { } count:
                    uc = Math.Clamp(count, 0, 10);
                    break;
                case "htmlrtf":
                    Flush();
                    htmlRtf = parameter != 0;
                    break;
                case "par" or "line" or "sect" or "page":
                    if (Visible()) Append(html ? "\r\n" : "\n");
                    break;
                case "row" when !html:
                    if (Visible()) Append("\n");
                    break;
                case "cell" when !html:
                    if (Visible()) Append("\t");
                    break;
                case "tab":
                    if (Visible()) Append("\t");
                    break;
                case "emdash": if (Visible()) Append("—"); break;
                case "endash": if (Visible()) Append("–"); break;
                case "lquote": if (Visible()) Append("‘"); break;
                case "rquote": if (Visible()) Append("’"); break;
                case "ldblquote": if (Visible()) Append("“"); break;
                case "rdblquote": if (Visible()) Append("”"); break;
                case "bullet": if (Visible()) Append("•"); break;
                default:
                    if (Skipped.Contains(name)) skip = true;
                    break;
            }
        }
        Flush();
        return (output.ToString(), html);

        bool Visible() => !skip && (tag || !htmlRtf);

        void Append(string s)
        {
            Flush();
            output.Append(s);
        }

        void Flush()
        {
            if (pending.Count == 0) return;
            output.Append(encoding.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending)));
            pending.Clear();
        }
    }

    private static string ReadWord(ReadOnlySpan<byte> rtf, int start, out int? parameter, out int end)
    {
        start = Math.Min(start, rtf.Length);
        var i = start;
        while (i < rtf.Length && i - start < 32 && IsLetter(rtf[i])) i++;
        var name = Encoding.ASCII.GetString(rtf[start..i]);
        parameter = null;
        var numberStart = i;
        if (i < rtf.Length && rtf[i] == '-') i++;
        while (i < rtf.Length && i - numberStart < 11 && char.IsAsciiDigit((char)rtf[i])) i++;
        if (i > numberStart && int.TryParse(rtf[numberStart..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            parameter = value;
        // One space after a control word belongs to it.
        if (i < rtf.Length && rtf[i] == ' ') i++;
        end = i;
        return name;
    }

    private static bool IsLetter(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

    private static int Hex(byte b) => Mime.TransferEncoding.Hex(b);

    internal static Encoding CodePage(int codePage)
    {
        switch (codePage)
        {
            case 65001: return Mime.Charsets.Utf8Encoding;
            case 1200: return Encoding.Unicode;
            case 1201: return Encoding.BigEndianUnicode;
            case 20127 or 28591 or 1252 or 0: return Mime.Charsets.Windows1252;
        }
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Mime.Charsets.Windows1252;
        }
        catch (ArgumentException)
        {
            return Mime.Charsets.Windows1252;
        }
    }
}
