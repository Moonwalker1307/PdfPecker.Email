using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace PdfPecker.Email.Mime;

/// <summary>
/// Turns the bytes of headers and text parts into strings. E-mail names its charsets loosely and often wrongly, so the
/// names are normalised the way mail clients and browsers do, and text without a usable charset is read as UTF-8 when
/// it is valid UTF-8 and as Windows-1252 otherwise.
/// </summary>
internal static class Charsets
{
    // The code-page encodings come from the provider directly instead of Encoding.RegisterProvider, which would change
    // Encoding.GetEncoding for the whole process the library runs in.
    private static readonly EncodingProvider CodePages = CodePagesEncodingProvider.Instance;

    internal static readonly Encoding Utf8Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    internal static readonly Encoding Windows1252 = CodePages.GetEncoding(1252)!;

    /// <summary>The encoding for a charset name, or null when the name is missing or unknown.</summary>
    internal static Encoding? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().Trim('"', '\'').ToLowerInvariant();
        // RFC 2231 allows a language after a star in an encoded word's charset (=?utf-8*en?Q?...?=).
        var star = n.IndexOf('*', StringComparison.Ordinal);
        if (star >= 0) n = n[..star];
        switch (n)
        {
            case "utf-8" or "utf8" or "unicode-1-1-utf-8" or "x-unicode20utf8":
                return Utf8Encoding;
            // As in browsers, Latin-1 and ASCII labels are read as Windows-1252: mail labelled ISO-8859-1 or us-ascii
            // routinely holds Windows-1252 quotes and dashes, and the two agree on every Latin-1 letter.
            case "us-ascii" or "ascii" or "iso-8859-1" or "iso8859-1" or "iso_8859-1" or "latin1" or "l1" or "cp819"
                or "windows-1252" or "cp1252" or "x-cp1252" or "ansi_x3.4-1968":
                return Windows1252;
            case "utf-16" or "utf-16le" or "unicode" or "ucs-2":
                return Encoding.Unicode;
            case "utf-16be" or "unicodefffe":
                return Encoding.BigEndianUnicode;
            case "utf-32" or "utf-32le":
                return Encoding.UTF32;
            case "utf-32be":
                return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            case "ks_c_5601-1987" or "ks_c_5601" or "euc-kr" or "korean" or "windows-949" or "cp949":
                return CodePages.GetEncoding(949);
            // GB18030 is a superset of GB2312 and GBK, and mail labelled with either often uses its extra characters.
            case "gb2312" or "gbk" or "x-gbk" or "cp936" or "windows-936" or "euc-cn" or "chinese":
                return CodePages.GetEncoding(54936);
            case "shift_jis" or "shift-jis" or "sjis" or "x-sjis" or "ms_kanji" or "windows-31j" or "cp932":
                return CodePages.GetEncoding(932);
            case "iso-8859-8-i" or "iso-8859-8-e":
                return CodePages.GetEncoding(28598);
            case "tis-620" or "iso-8859-11" or "windows-874":
                return CodePages.GetEncoding(874);
            case "macintosh" or "mac" or "x-mac-roman":
                return CodePages.GetEncoding(10000);
            case "big5" or "big5-hkscs" or "cn-big5" or "x-x-big5":
                return CodePages.GetEncoding(950);
        }
        // iso8859-15 and iso_8859-15 are common misspellings of iso-8859-15.
        if (n.StartsWith("iso8859", StringComparison.Ordinal)) n = "iso-8859-" + n[7..].TrimStart('-', '_');
        else if (n.StartsWith("iso_8859", StringComparison.Ordinal)) n = "iso-8859-" + n[8..].TrimStart('-', '_');
        if (n.StartsWith("cp", StringComparison.Ordinal)
            && int.TryParse(n.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var page))
            return CodePages.GetEncoding(page);
        try
        {
            return CodePages.GetEncoding(n);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Decodes text in the given charset, guessing as described above when it is missing or unknown.</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes, string? charset)
    {
        if (bytes.IsEmpty) return string.Empty;
        var encoding = Find(charset);
        string text;
        if (encoding is null || encoding == Windows1252 && !IsAscii(bytes) && Utf8.IsValid(bytes) && HasMultiByte(bytes))
        {
            // A missing charset, or an ASCII or Latin-1 label on text that is in fact UTF-8 (frequent from old
            // clients and scripts), is read as UTF-8 when the bytes are valid UTF-8.
            text = Guess(bytes);
        }
        else
        {
            text = encoding.GetString(bytes);
        }
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>Text without a declared charset: ASCII, else UTF-8 if valid, else Windows-1252.</summary>
    internal static string Guess(ReadOnlySpan<byte> bytes)
    {
        if (IsAscii(bytes)) return Encoding.ASCII.GetString(bytes);
        return Utf8.IsValid(bytes) ? Utf8Encoding.GetString(bytes) : Windows1252.GetString(bytes);
    }

    internal static bool IsAscii(ReadOnlySpan<byte> bytes) => System.Text.Ascii.IsValid(bytes);

    private static bool HasMultiByte(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyInRange((byte)0xC2, (byte)0xF4) >= 0;
}
