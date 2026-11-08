using System.Globalization;
using System.Text;

namespace PdfPecker.Email.Html;

/// <summary>Escaping for text and attribute values, and the small character-reference decoder the sanitiser needs.</summary>
internal static class Markup
{
    /// <summary>Appends text with the characters that are markup in HTML escaped.</summary>
    internal static StringBuilder AppendEscaped(this StringBuilder sb, ReadOnlySpan<char> text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var replacement = text[i] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\0' => "",
                _ => null,
            };
            if (replacement is null) continue;
            sb.Append(text[start..i]).Append(replacement);
            start = i + 1;
        }
        return sb.Append(text[start..]);
    }

    internal static string Escape(string text) => new StringBuilder(text.Length + 16).AppendEscaped(text).ToString();

    /// <summary>
    /// Decodes numeric character references and the named ones that matter for checking URLs and CSS (the ones used
    /// to hide "javascript:" or "position: fixed" from a filter). Other named references are left as they are.
    /// </summary>
    internal static string DecodeReferences(string value)
    {
        var amp = value.IndexOf('&');
        if (amp < 0) return value;
        var sb = new StringBuilder(value.Length);
        sb.Append(value, 0, amp);
        var i = amp;
        while (i < value.Length)
        {
            var c = value[i];
            if (c != '&')
            {
                sb.Append(c);
                i++;
                continue;
            }
            var semi = value.IndexOf(';', i + 1, Math.Min(value.Length - i - 1, 34));
            if (i + 1 < value.Length && value[i + 1] == '#')
            {
                // Numeric references may lack the semicolon.
                var j = i + 2;
                var hex = j < value.Length && (value[j] == 'x' || value[j] == 'X');
                if (hex) j++;
                var digitsStart = j;
                while (j < value.Length && j - digitsStart < 8 && (hex ? char.IsAsciiHexDigit(value[j]) : char.IsAsciiDigit(value[j]))) j++;
                if (j > digitsStart && int.TryParse(value.AsSpan(digitsStart, j - digitsStart),
                        hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                {
                    if (code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)) sb.Append(char.ConvertFromUtf32(code));
                    else sb.Append('�');
                    i = j < value.Length && value[j] == ';' ? j + 1 : j;
                    continue;
                }
            }
            else if (semi > i + 1)
            {
                var named = value.AsSpan(i + 1, semi - i - 1).ToString().ToLowerInvariant() switch
                {
                    "amp" => "&",
                    "lt" => "<",
                    "gt" => ">",
                    "quot" => "\"",
                    "apos" => "'",
                    "colon" => ":",
                    "tab" => "\t",
                    "newline" => "\n",
                    "lpar" => "(",
                    "rpar" => ")",
                    "sol" => "/",
                    "bsol" => "\\",
                    "num" => "#",
                    "semi" => ";",
                    "comma" => ",",
                    "period" => ".",
                    "excl" => "!",
                    "nbsp" => " ",
                    _ => null,
                };
                if (named is not null)
                {
                    sb.Append(named);
                    i = semi + 1;
                    continue;
                }
            }
            sb.Append('&');
            i++;
        }
        return sb.ToString();
    }
}
