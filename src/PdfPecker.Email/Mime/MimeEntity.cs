namespace PdfPecker.Email.Mime;

/// <summary>A raw header: its name and the range of its value (folded, undecoded) in the entity's source.</summary>
internal readonly record struct RawHeader(string Name, int Start, int End);

/// <summary>
/// One MIME entity: a message, a part of a multipart, or the message inside a message/rfc822 part. It keeps ranges into
/// the message's bytes instead of copies, so parsing allocates little more than the tree; bodies are decoded when they
/// are used.
/// </summary>
internal sealed class MimeEntity
{
    private byte[]? _decoded;
    private string? _text;

    internal MimeEntity(byte[] source, List<RawHeader> headers, int bodyStart, int bodyEnd)
    {
        Source = source;
        Headers = headers;
        BodyStart = bodyStart;
        BodyEnd = bodyEnd;
    }

    internal byte[] Source { get; }
    internal List<RawHeader> Headers { get; }
    internal int BodyStart { get; }
    internal int BodyEnd { get; }

    /// <summary>The media type in lower case, such as "text".</summary>
    internal string MediaType { get; set; } = "text";

    /// <summary>The subtype in lower case, such as "plain".</summary>
    internal string SubType { get; set; } = "plain";

    internal Dictionary<string, string> ContentTypeParameters { get; set; } = EmptyParameters;

    /// <summary>"inline", "attachment" or null when the part has no Content-Disposition.</summary>
    internal string? Disposition { get; set; }

    internal Dictionary<string, string> DispositionParameters { get; set; } = EmptyParameters;

    internal string? TransferEncoding { get; set; }

    /// <summary>The Content-ID without its angle brackets.</summary>
    internal string? ContentId { get; set; }

    internal string? ContentLocation { get; set; }

    internal List<MimeEntity> Children { get; } = [];

    internal static readonly Dictionary<string, string> EmptyParameters = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsMultipart => MediaType == "multipart";

    internal bool Is(string mediaType, string subType) => MediaType == mediaType && SubType == subType;

    internal string ContentType => MediaType + "/" + SubType;

    internal ReadOnlySpan<byte> RawBody => Source.AsSpan(BodyStart, BodyEnd - BodyStart);

    /// <summary>The body with its transfer encoding removed, decoded once and kept.</summary>
    internal byte[] DecodedBody => _decoded ??= Mime.TransferEncoding.Decode(RawBody, TransferEncoding);

    /// <summary>The length of the decoded body, without decoding it when the encoding does not change the length.</summary>
    internal int DecodedLength => _decoded?.Length ?? (IsLengthPreserving ? BodyEnd - BodyStart : DecodedBody.Length);

    private bool IsLengthPreserving => TransferEncoding is null
        || !TransferEncoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
        && !TransferEncoding.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase);

    /// <summary>The body as text in the part's charset.</summary>
    internal string DecodeText() => _text ??= Charsets.Decode(DecodedBody, Parameter("charset") ?? SniffCharset());

    // An HTML part without a charset parameter may name its charset in a meta element near the start.
    private string? SniffCharset()
    {
        if (SubType != "html") return null;
        var body = DecodedBody.AsSpan(0, Math.Min(DecodedBody.Length, 2048));
        var at = IndexOfIgnoreCase(body, "charset="u8);
        if (at < 0) return null;
        var i = at + 8;
        while (i < body.Length && body[i] is (byte)'"' or (byte)'\'' or (byte)' ') i++;
        var start = i;
        while (i < body.Length && (char.IsAsciiLetterOrDigit((char)body[i]) || body[i] is (byte)'-' or (byte)'_' or (byte)':' or (byte)'.')) i++;
        return i > start ? System.Text.Encoding.ASCII.GetString(body[start..i]) : null;
    }

    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (System.Text.Ascii.EqualsIgnoreCase(haystack.Slice(i, needle.Length), needle)) return i;
        }
        return -1;
    }

    internal string? Parameter(string name) => ContentTypeParameters.GetValueOrDefault(name);

    /// <summary>The decoded value of the first header with the given name, or null.</summary>
    internal string? Header(string name)
    {
        foreach (var h in Headers)
        {
            if (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return HeaderValues.Unfold(Source.AsSpan(h.Start, h.End - h.Start));
        }
        return null;
    }

    /// <summary>The file name from Content-Disposition, or the older name parameter of Content-Type.</summary>
    internal string? FileName
    {
        get
        {
            var name = DispositionParameters.GetValueOrDefault("filename");
            if (string.IsNullOrWhiteSpace(name)) name = ContentTypeParameters.GetValueOrDefault("name");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }
}
