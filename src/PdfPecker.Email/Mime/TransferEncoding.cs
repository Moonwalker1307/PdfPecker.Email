namespace PdfPecker.Email.Mime;

/// <summary>
/// Decodes Content-Transfer-Encoding: base64 and quoted-printable. Both decoders are lenient, as mail clients are:
/// characters that do not belong are skipped and broken escapes are kept as text, so a damaged part still shows what
/// can be read. Neither output can be longer than its input.
/// </summary>
internal static class TransferEncoding
{
    private static readonly sbyte[] Base64Values = BuildBase64Values();

    private static sbyte[] BuildBase64Values()
    {
        var values = new sbyte[256];
        Array.Fill(values, (sbyte)-1);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (var i = 0; i < alphabet.Length; i++) values[alphabet[i]] = (sbyte)i;
        // The URL-safe alphabet turns up in mail written by web services.
        values['-'] = 62;
        values['_'] = 63;
        return values;
    }

    /// <summary>Decodes a body in the named transfer encoding; 7bit, 8bit, binary and unknown encodings are copied.</summary>
    internal static byte[] Decode(ReadOnlySpan<byte> body, string? encoding)
    {
        if (encoding is not null)
        {
            if (encoding.Equals("base64", StringComparison.OrdinalIgnoreCase)) return DecodeBase64(body);
            if (encoding.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase)) return DecodeQuotedPrintable(body, header: false);
        }
        return body.ToArray();
    }

    internal static byte[] DecodeBase64(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length / 4 * 3 + 3];
        var length = DecodeBase64(input, output);
        return length == output.Length ? output : output.AsSpan(0, length).ToArray();
    }

    /// <summary>Decodes base64 into <paramref name="output"/>, which holds at least input.Length / 4 * 3 + 3 bytes.</summary>
    internal static int DecodeBase64(ReadOnlySpan<byte> input, Span<byte> output)
    {
        // Well-formed mail is lines of base64 whose length is a multiple of four. Those lines go through the
        // vectorised decoder of the base class library, several times faster than the loop below, which takes over
        // from the first line that is not clean.
        var written = 0;
        while (input.Length > 0)
        {
            var lf = input.IndexOf((byte)'\n');
            var line = lf < 0 ? input : input[..lf];
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (line.Length % 4 != 0
                || System.Buffers.Text.Base64.DecodeFromUtf8(line, output[written..], out var consumed, out var decoded) != System.Buffers.OperationStatus.Done
                || consumed != line.Length)
                break;
            written += decoded;
            input = lf < 0 ? default : input[(lf + 1)..];
            // Padding ends a block; what follows starts afresh, which the loop below handles.
            if (line.Length > 0 && line[^1] == '=') break;
        }
        return written + DecodeBase64Slow(input, output[written..]);
    }

    private static int DecodeBase64Slow(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var values = Base64Values;
        int written = 0, bits = 0, count = 0;
        foreach (var b in input)
        {
            var v = values[b];
            if (v < 0)
            {
                // Padding ends a block. Some senders glue several padded blocks together, so decoding goes on after
                // it with a fresh group. Other characters are line breaks or damage and are skipped.
                if (b == '=' && count > 0)
                {
                    written = Flush(output, written, bits, count);
                    bits = 0;
                    count = 0;
                }
                continue;
            }
            bits = bits << 6 | (byte)v;
            if (++count == 4)
            {
                output[written] = (byte)(bits >> 16);
                output[written + 1] = (byte)(bits >> 8);
                output[written + 2] = (byte)bits;
                written += 3;
                bits = 0;
                count = 0;
            }
        }
        return Flush(output, written, bits, count);
    }

    // A final group of two or three characters carries one or two bytes; a lone character carries none.
    private static int Flush(Span<byte> output, int written, int bits, int count)
    {
        if (count == 2)
        {
            output[written++] = (byte)(bits >> 4);
        }
        else if (count == 3)
        {
            output[written++] = (byte)(bits >> 10);
            output[written++] = (byte)(bits >> 2);
        }
        return written;
    }

    /// <summary>
    /// Decodes quoted-printable. In <paramref name="header"/> mode (RFC 2047's Q encoding) an underscore is a space and
    /// there are no soft line breaks.
    /// </summary>
    internal static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> input, bool header)
    {
        var output = new byte[input.Length];
        var written = 0;
        var i = 0;
        while (i < input.Length)
        {
            var next = header ? input[i..].IndexOfAny((byte)'=', (byte)'_') : input[i..].IndexOf((byte)'=');
            if (next < 0)
            {
                input[i..].CopyTo(output.AsSpan(written));
                written += input.Length - i;
                break;
            }
            input.Slice(i, next).CopyTo(output.AsSpan(written));
            written += next;
            i += next;
            if (input[i] == '_')
            {
                output[written++] = (byte)' ';
                i++;
                continue;
            }
            // '=' followed by two hex digits is a byte.
            if (i + 2 < input.Length && Hex(input[i + 1]) >= 0 && Hex(input[i + 2]) >= 0)
            {
                output[written++] = (byte)(Hex(input[i + 1]) << 4 | Hex(input[i + 2]));
                i += 3;
                continue;
            }
            if (!header)
            {
                // '=' at the end of a line, perhaps with trailing spaces a gateway added, is a soft line break.
                var j = i + 1;
                while (j < input.Length && (input[j] == ' ' || input[j] == '\t')) j++;
                if (j < input.Length && input[j] == '\n') { i = j + 1; continue; }
                if (j + 1 < input.Length && input[j] == '\r' && input[j + 1] == '\n') { i = j + 2; continue; }
                if (j == input.Length) { i = j; continue; }
            }
            // A broken escape is kept as it is.
            output[written++] = (byte)'=';
            i++;
        }
        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }

    internal static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => -1,
    };
}
