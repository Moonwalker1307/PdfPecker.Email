namespace PdfPecker.Email.Mime;

/// <summary>
/// Splits a message into its tree of MIME entities (RFC 2045, 2046, 5322). It works on the message's bytes and only
/// records ranges; it never fails on damaged structure, only when the message goes over an <see cref="EmailLimits"/>
/// value. Each level searches its own range once for its boundary, so the work is linear in the size of the message
/// times the nesting depth, which the limits bound.
/// </summary>
internal sealed class MimeParser
{
    private readonly EmailLimits _limits;
    private readonly CancellationToken _cancellationToken;
    private int _parts;

    private MimeParser(EmailLimits limits, CancellationToken cancellationToken)
    {
        _limits = limits;
        _cancellationToken = cancellationToken;
    }

    internal static MimeEntity Parse(byte[] message, EmailLimits limits, CancellationToken cancellationToken)
    {
        if (message.Length > limits.MaxMessageBytes)
            throw new PdfLimitException($"The message is {message.Length} bytes, over the limit of {limits.MaxMessageBytes} (EmailLimits.MaxMessageBytes).");
        var start = 0;
        // An mbox "From " separator line before the headers is not a header.
        if (message.AsSpan().StartsWith("From "u8))
        {
            var lf = message.AsSpan().IndexOf((byte)'\n');
            start = lf < 0 ? message.Length : lf + 1;
        }
        return new MimeParser(limits, cancellationToken).ParseEntity(message, start, message.Length, 0, digest: false);
    }

    private MimeEntity ParseEntity(byte[] source, int start, int end, int depth, bool digest)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (++_parts > _limits.MaxParts)
            throw new PdfLimitException($"The message has more than {_limits.MaxParts} MIME parts (EmailLimits.MaxParts).");
        if (depth > _limits.MaxNestingDepth)
            throw new PdfLimitException($"The message nests deeper than {_limits.MaxNestingDepth} levels (EmailLimits.MaxNestingDepth).");

        var headers = ReadHeaders(source, start, end, out var bodyStart);
        var entity = new MimeEntity(source, headers, bodyStart, end);
        ReadContentHeaders(entity, digest);

        if (entity.IsMultipart)
        {
            var boundary = entity.Parameter("boundary");
            if (!string.IsNullOrEmpty(boundary) && boundary.Length <= 256 && SplitMultipart(entity, boundary, depth))
            {
                return entity;
            }
            // A multipart without a usable boundary is shown as text, as mail clients do.
            entity.MediaType = "text";
            entity.SubType = "plain";
        }
        else if (entity.MediaType == "message" && entity.SubType is "rfc822" or "global")
        {
            // The attached message is parsed too, for its subject and file name. Its transfer encoding should be 7bit,
            // 8bit or binary, but base64 occurs, in which case it is parsed from the decoded bytes.
            var encoded = entity.TransferEncoding is not null
                && (entity.TransferEncoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
                    || entity.TransferEncoding.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase));
            entity.Children.Add(encoded
                ? ParseEntity(entity.DecodedBody, 0, entity.DecodedBody.Length, depth + 1, digest: false)
                : ParseEntity(source, bodyStart, end, depth + 1, digest: false));
        }
        return entity;
    }

    private static void ReadContentHeaders(MimeEntity entity, bool digest)
    {
        // Inside multipart/digest the default type is message/rfc822 (RFC 2046 section 5.1.5).
        var (type, sub, parameters) = (digest ? "message" : "text", digest ? "rfc822" : "plain", MimeEntity.EmptyParameters);
        var contentType = entity.Header("Content-Type");
        if (contentType is not null)
        {
            var parsed = HeaderValues.ParseContentType(contentType);
            if (parsed.Type.Length > 0 && parsed.SubType.Length > 0)
            {
                (type, sub) = (parsed.Type, parsed.SubType);
            }
            parameters = parsed.Parameters;
        }
        entity.MediaType = type;
        entity.SubType = sub;
        entity.ContentTypeParameters = parameters;

        var disposition = entity.Header("Content-Disposition");
        if (disposition is not null)
        {
            var (kind, dispositionParameters) = HeaderValues.ParseDisposition(disposition);
            entity.Disposition = kind.Length > 0 ? kind : null;
            entity.DispositionParameters = dispositionParameters;
        }
        var encoding = entity.Header("Content-Transfer-Encoding");
        entity.TransferEncoding = string.IsNullOrWhiteSpace(encoding) ? null : encoding.Trim().Trim('"');
        var id = entity.Header("Content-ID");
        if (!string.IsNullOrWhiteSpace(id)) entity.ContentId = id.Trim().TrimStart('<').TrimEnd('>').Trim();
        var location = entity.Header("Content-Location");
        if (!string.IsNullOrWhiteSpace(location)) entity.ContentLocation = location.Trim();
    }

    /// <summary>
    /// Reads header lines from <paramref name="start"/> up to the empty line that ends them. Lines without a colon are
    /// skipped, except that a first line which is not a header means the part has no headers at all.
    /// </summary>
    private static List<RawHeader> ReadHeaders(byte[] source, int start, int end, out int bodyStart)
    {
        var headers = new List<RawHeader>();
        var span = source.AsSpan(0, end);
        var pos = start;
        while (pos < end)
        {
            var lf = span[pos..].IndexOf((byte)'\n');
            var lineEnd = lf < 0 ? end : pos + lf; // index of '\n' or end
            var next = lf < 0 ? end : lineEnd + 1;
            var contentEnd = lineEnd > pos && span[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            if (contentEnd == pos)
            {
                bodyStart = next;
                return headers;
            }
            var first = span[pos];
            if ((first == ' ' || first == '\t') && headers.Count > 0)
            {
                var last = headers[^1];
                headers[^1] = last with { End = contentEnd };
                pos = next;
                continue;
            }
            var line = span[pos..contentEnd];
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsHeaderName(line[..colon]))
            {
                if (headers.Count == 0)
                {
                    bodyStart = pos;
                    return headers;
                }
                pos = next;
                continue;
            }
            var nameSpan = line[..colon].TrimEnd((byte)' ').TrimEnd((byte)'\t');
            headers.Add(new RawHeader(HeaderName(nameSpan), pos + colon + 1, contentEnd));
            pos = next;
        }
        bodyStart = end;
        return headers;
    }

    private static bool IsHeaderName(ReadOnlySpan<byte> name)
    {
        name = name.TrimEnd((byte)' ').TrimEnd((byte)'\t');
        if (name.IsEmpty || name.Length > 200) return false;
        foreach (var b in name)
        {
            if (b <= 32 || b >= 127) return false;
        }
        return true;
    }

    // The names every message has are shared strings instead of a new string per header.
    private static string HeaderName(ReadOnlySpan<byte> name)
    {
        foreach (var known in KnownNames)
        {
            if (name.Length == known.Length && System.Text.Ascii.EqualsIgnoreCase(name, known)) return known;
        }
        return System.Text.Encoding.ASCII.GetString(name);
    }

    private static readonly string[] KnownNames =
    [
        "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-ID", "Content-Location", "From", "To",
        "Cc", "Bcc", "Subject", "Date", "Message-ID", "Received", "MIME-Version", "Reply-To", "Sender", "DKIM-Signature",
        "Return-Path", "X-Mailer", "Content-Language", "Thread-Index", "References", "In-Reply-To",
    ];

    /// <summary>
    /// Splits a multipart body at its boundary lines. A delimiter is "--boundary" at the start of a line, followed by
    /// nothing but optional whitespace (or "--" for the last one), so a boundary that is a prefix of a nested one does
    /// not match. Returns false when the body has no delimiter at all.
    /// </summary>
    private bool SplitMultipart(MimeEntity entity, string boundary, int depth)
    {
        var source = entity.Source;
        var delimiter = new byte[boundary.Length + 2];
        delimiter[0] = (byte)'-';
        delimiter[1] = (byte)'-';
        for (var k = 0; k < boundary.Length; k++) delimiter[k + 2] = boundary[k] < 256 ? (byte)boundary[k] : (byte)'?';

        var end = entity.BodyEnd;
        var pos = entity.BodyStart;
        var partStart = -1;
        var found = false;
        var digest = entity.SubType == "digest";
        while (pos <= end)
        {
            var at = FindDelimiter(source, pos, end, delimiter, out var isClose, out var afterLine);
            if (at < 0) break;
            found = true;
            if (partStart >= 0)
            {
                // The line break before the delimiter belongs to the delimiter.
                var partEnd = at;
                if (partEnd > partStart && source[partEnd - 1] == '\n') partEnd--;
                if (partEnd > partStart && source[partEnd - 1] == '\r') partEnd--;
                entity.Children.Add(ParseEntity(source, partStart, Math.Max(partStart, partEnd), depth + 1, digest));
            }
            if (isClose) return true;
            partStart = afterLine;
            pos = afterLine;
        }
        // A missing closing delimiter is common in truncated mail: the last part runs to the end.
        if (partStart >= 0 && partStart < end)
        {
            entity.Children.Add(ParseEntity(source, partStart, end, depth + 1, digest));
        }
        return found;
    }

    private static int FindDelimiter(byte[] source, int from, int end, byte[] delimiter, out bool isClose, out int afterLine)
    {
        var span = source.AsSpan(0, end);
        var pos = from;
        while (pos < end)
        {
            var at = span[pos..].IndexOf(delimiter);
            if (at < 0) break;
            at += pos;
            pos = at + 1;
            if (at > 0 && source[at - 1] != '\n') continue;
            var k = at + delimiter.Length;
            isClose = k + 1 < end && source[k] == '-' && source[k + 1] == '-';
            if (isClose) k += 2;
            while (k < end && (source[k] == ' ' || source[k] == '\t' || source[k] == '\r')) k++;
            if (k < end && source[k] != '\n') continue;
            afterLine = k < end ? k + 1 : end;
            return at;
        }
        isClose = false;
        afterLine = end;
        return -1;
    }
}
