using PdfPecker.Email.Mime;

namespace PdfPecker.Email;

/// <summary>
/// An e-mail message read from its MIME source (an .eml file) or from an Outlook .msg file: the headers, the body that a
/// mail client would show and the attachments. Reading never fails on damaged mail; it fails only when a message goes over one of the
/// <see cref="EmailLimits"/>.
/// </summary>
public sealed partial class EmailMessage
{
    private readonly MimeEntity _root;
    private readonly List<BodyPart> _body = [];
    private readonly List<EmailAttachment> _attachments = [];
    private readonly Dictionary<string, MimeEntity> _byContentId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MimeEntity> _byLocation = new(StringComparer.Ordinal);
    private string? _textBody, _htmlBody;
    private bool _textFound, _htmlFound;

    private EmailMessage(byte[] source, MimeEntity root)
    {
        Source = source;
        _root = root;
        Subject = DecodedHeader(root, "Subject");
        From = HeaderValues.ParseAddresses(root.Header("From"));
        Sender = HeaderValues.ParseAddresses(root.Header("Sender"));
        To = HeaderValues.ParseAddresses(root.Header("To"));
        Cc = HeaderValues.ParseAddresses(root.Header("Cc"));
        Bcc = HeaderValues.ParseAddresses(root.Header("Bcc"));
        ReplyTo = HeaderValues.ParseAddresses(root.Header("Reply-To"));
        Date = HeaderValues.ParseDate(root.Header("Date"));
        MessageId = root.Header("Message-ID")?.Trim().TrimStart('<').TrimEnd('>');
        Language = root.Header("Content-Language")?.Split(',')[0].Trim() is { Length: > 0 and <= 35 } language ? language : null;
        IndexResources(root);
        var unreferenced = new List<MimeEntity>();
        var attachments = new List<MimeEntity>();
        Collect(root, preferText: false, _body, attachments, unreferenced);
        var referenced = ReferencedContentIds();
        // Related parts and inline parts that the HTML never shows are listed as attachments, as mail clients do, so
        // nothing in the message is silently lost.
        foreach (var part in unreferenced)
        {
            if (!IsReferenced(part, referenced)) attachments.Add(part);
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in attachments)
        {
            if (part.Disposition != "attachment" && IsReferenced(part, referenced)) continue;
            _attachments.Add(new EmailAttachment(part, FileNames.Make(part, _attachments.Count + 1, names)));
        }
    }

    /// <summary>
    /// Reads a message from its MIME bytes, such as the content of an .eml file, or from an Outlook .msg file, which is
    /// recognised by its content.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="PdfLimitException">The message goes over a limit in <paramref name="limits"/>.</exception>
    public static EmailMessage Parse(byte[] message, EmailLimits? limits = null) => Parse(message, limits, CancellationToken.None);

    /// <summary>Reads a message as <see cref="Parse(byte[], EmailLimits?)"/>, stopping when the token is cancelled.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="PdfLimitException">The message goes over a limit in <paramref name="limits"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static EmailMessage Parse(byte[] message, EmailLimits? limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        limits ??= EmailLimits.Default;
        if (Msg.CompoundFile.IsCompoundFile(message))
        {
            if (message.Length > limits.MaxMessageBytes)
                throw new PdfLimitException($"The message is {message.Length} bytes, over the limit of {limits.MaxMessageBytes} (EmailLimits.MaxMessageBytes).");
            // The MIME written for an Outlook message is base64 and so larger than the file; the size limit was
            // checked on the file, and the parts and nesting limits still apply to the MIME.
            var (mime, length) = Msg.OutlookMessage.ToMime(message, limits, cancellationToken);
            var relaxed = new EmailLimits { MaxMessageBytes = int.MaxValue, MaxParts = limits.MaxParts * 3 + 3, MaxNestingDepth = limits.MaxNestingDepth * 3 + 3 };
            return new EmailMessage(mime, ExpandTnef(MimeParser.Parse(mime, length, relaxed, cancellationToken), limits, cancellationToken)) { OriginalExtension = ".msg", OriginalMediaType = "application/vnd.ms-outlook", Original = message };
        }
        if (Msg.TnefObject.IsTnef(message))
        {
            // A winmail.dat on its own: the message it holds, written as MIME like an Outlook .msg file.
            if (message.Length > limits.MaxMessageBytes)
                throw new PdfLimitException($"The message is {message.Length} bytes, over the limit of {limits.MaxMessageBytes} (EmailLimits.MaxMessageBytes).");
            var tnef = Msg.TnefObject.Read(message, cancellationToken)!;
            var (mime, length) = Msg.OutlookMessage.ToMime(tnef, message.Length, limits, cancellationToken, headers: true, body: true);
            var relaxed = new EmailLimits { MaxMessageBytes = int.MaxValue, MaxParts = limits.MaxParts * 3 + 3, MaxNestingDepth = limits.MaxNestingDepth * 3 + 3 };
            return new EmailMessage(mime, MimeParser.Parse(mime, length, relaxed, cancellationToken)) { OriginalExtension = ".dat", OriginalMediaType = "application/ms-tnef", Original = message };
        }
        return new EmailMessage(message, ExpandTnef(MimeParser.Parse(message, limits, cancellationToken), limits, cancellationToken));
    }

    /// <summary>Reads a message from a stream, up to <see cref="EmailLimits.MaxMessageBytes"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="PdfLimitException">The message goes over a limit in <paramref name="limits"/>.</exception>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static EmailMessage Parse(Stream message, EmailLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        limits ??= EmailLimits.Default;
        return Parse(ReadAll(message, limits.MaxMessageBytes), limits);
    }

    /// <summary>Reads a message from an .eml or Outlook .msg file.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="PdfLimitException">The file goes over a limit in <paramref name="limits"/>.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the file.</exception>
    public static EmailMessage Load(string path, EmailLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return Parse(stream, limits);
    }

    internal static byte[] ReadAll(Stream stream, int maxBytes)
    {
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > maxBytes)
                throw new PdfLimitException($"The message is {remaining} bytes, over the limit of {maxBytes} (EmailLimits.MaxMessageBytes).");
        }
        using var buffer = stream.CanSeek ? new MemoryStream((int)(stream.Length - stream.Position)) : new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new PdfLimitException($"The message is over the limit of {maxBytes} bytes (EmailLimits.MaxMessageBytes).");
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    /// <summary>
    /// The buffer that holds the message's MIME source; for an Outlook message, the MIME written for it, which may be
    /// followed by unused bytes. Parts refer to ranges of it.
    /// </summary>
    internal byte[] Source { get; }

    /// <summary>The extension of the file the message was read from: ".eml", or ".msg" or ".dat" (winmail.dat).</summary>
    internal string OriginalExtension { get; private init; } = ".eml";

    /// <summary>The media type of the file the message was read from.</summary>
    internal string OriginalMediaType { get; private init; } = "message/rfc822";

    /// <summary>The bytes the message was read from: the .msg file for an Outlook message, else the MIME source.</summary>
    internal byte[] Original { get => _original ?? Source; private init => _original = value; }

    private byte[]? _original;

    /// <summary>The subject, decoded; null when the message has none.</summary>
    public string? Subject { get; }

    /// <summary>The From header's mailboxes, usually one.</summary>
    public IReadOnlyList<EmailAddress> From { get; }

    /// <summary>The Sender header's mailbox, when someone sent the message on behalf of From.</summary>
    public IReadOnlyList<EmailAddress> Sender { get; }

    /// <summary>The To recipients.</summary>
    public IReadOnlyList<EmailAddress> To { get; }

    /// <summary>The Cc recipients.</summary>
    public IReadOnlyList<EmailAddress> Cc { get; }

    /// <summary>The Bcc recipients, which only the sender's copy of a message has.</summary>
    public IReadOnlyList<EmailAddress> Bcc { get; }

    /// <summary>The Reply-To mailboxes.</summary>
    public IReadOnlyList<EmailAddress> ReplyTo { get; }

    /// <summary>The Date header with its time zone offset, or null when it is missing or cannot be read.</summary>
    public DateTimeOffset? Date { get; }

    /// <summary>The Message-ID without angle brackets, or null.</summary>
    public string? MessageId { get; }

    /// <summary>The first language of the Content-Language header, such as "da", or null.</summary>
    public string? Language { get; }

    /// <summary>The plain text body, or null when the message has none.</summary>
    public string? TextBody
    {
        get
        {
            if (!_textFound)
            {
                _textBody = FindBody(_root, "plain")?.DecodeText();
                _textFound = true;
            }
            return _textBody;
        }
    }

    /// <summary>The HTML body as the message has it (not sanitised), or null when the message has none.</summary>
    public string? HtmlBody
    {
        get
        {
            if (!_htmlFound)
            {
                _htmlBody = FindBody(_root, "html")?.DecodeText();
                _htmlFound = true;
            }
            return _htmlBody;
        }
    }

    /// <summary>
    /// The attachments: parts marked as attachments, parts that are not text, attached messages (as .eml files), and
    /// inline images the HTML body does not show. Images the HTML body shows are not attachments.
    /// </summary>
    public IReadOnlyList<EmailAttachment> Attachments => _attachments;

    /// <summary>The decoded value of the first header with the given name, or null.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public string? GetHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return DecodedHeader(_root, name);
    }

    /// <summary>The parts shown as the body, for the HTML view or, when asked, the plain text view.</summary>
    internal IReadOnlyList<BodyPart> SelectBody(bool preferText)
    {
        if (!preferText) return _body;
        var body = new List<BodyPart>();
        Collect(_root, preferText: true, body, [], []);
        return body;
    }

    /// <summary>The part with the given Content-ID or Content-Location, for inline images; null when there is none.</summary>
    internal MimeEntity? FindResource(string reference)
    {
        if (reference.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
        {
            var id = Unescape(reference.AsSpan(4).Trim().TrimStart('<').TrimEnd('>').ToString());
            if (_byContentId.TryGetValue(id, out var part)) return part;
            foreach (var (key, value) in _byContentId)
            {
                if (key.Equals(id, StringComparison.OrdinalIgnoreCase)) return value;
            }
            return null;
        }
        return _byLocation.GetValueOrDefault(reference);
    }

    internal bool HasLocation(string reference) => _byLocation.ContainsKey(reference);

    private static string? DecodedHeader(MimeEntity entity, string name)
    {
        var raw = entity.Header(name);
        return raw is null ? null : BidiControls.Remove(HeaderValues.DecodeWords(raw));
    }

    private void IndexResources(MimeEntity entity)
    {
        if (entity.ContentId is { Length: > 0 } id) _byContentId.TryAdd(id, entity);
        if (entity.ContentLocation is { Length: > 0 } location && !entity.IsMultipart) _byLocation.TryAdd(location, entity);
        // Parts of an attached message belong to that message, not to this one.
        if (entity.MediaType == "message") return;
        foreach (var child in entity.Children) IndexResources(child);
    }

    /// <summary>
    /// Picks the parts a mail client shows as the body and sorts the rest into attachments, following RFC 2046: the
    /// preferred alternative of a multipart/alternative, the root of a multipart/related, the content of a
    /// multipart/signed, and every inline text part of a multipart/mixed in order.
    /// </summary>
    private static void Collect(MimeEntity entity, bool preferText, List<BodyPart> body, List<MimeEntity> attachments, List<MimeEntity> unreferenced)
    {
        if (entity.IsMultipart)
        {
            var children = entity.Children;
            if (children.Count == 0) return;
            switch (entity.SubType)
            {
                case "alternative":
                {
                    var chosen = ChooseAlternative(children, preferText);
                    Collect(chosen, preferText, body, attachments, unreferenced);
                    // Alternatives that are neither text nor HTML, such as a calendar invitation, are kept as files.
                    foreach (var child in children)
                    {
                        if (child != chosen && !child.IsMultipart && child.MediaType != "text") attachments.Add(child);
                        else if (child != chosen && child.Is("text", "calendar")) attachments.Add(child);
                    }
                    return;
                }
                case "related":
                {
                    var start = entity.Parameter("start")?.Trim().TrimStart('<').TrimEnd('>');
                    var root = (start is null ? null : children.Find(c => c.ContentId == start)) ?? children[0];
                    Collect(root, preferText, body, attachments, unreferenced);
                    foreach (var child in children)
                    {
                        if (child != root) CollectResource(child, attachments, unreferenced);
                    }
                    return;
                }
                case "signed":
                    Collect(children[0], preferText, body, attachments, unreferenced);
                    for (var i = 1; i < children.Count; i++) attachments.Add(children[i]);
                    return;
                default:
                    foreach (var child in children)
                    {
                        if (child.IsMultipart && child.Disposition != "attachment") Collect(child, preferText, body, attachments, unreferenced);
                        else CollectLeaf(child, body, attachments, unreferenced, top: false);
                    }
                    return;
            }
        }
        CollectLeaf(entity, body, attachments, unreferenced, top: true);
    }

    private static void CollectLeaf(MimeEntity entity, List<BodyPart> body, List<MimeEntity> attachments, List<MimeEntity> unreferenced, bool top)
    {
        if (entity.Disposition == "attachment" || entity.IsMultipart)
        {
            attachments.Add(entity);
            return;
        }
        if (entity.MediaType == "text" && entity.SubType is "plain" or "html" && (entity.FileName is null || entity.Disposition == "inline" || top))
        {
            body.Add(new BodyPart(entity.SubType == "html" ? BodyKind.Html : BodyKind.Text, entity));
            return;
        }
        if (entity.MediaType == "image" && entity.Disposition == "inline" && IsShowableImage(entity))
        {
            // Mail clients that put images between text parts of a multipart/mixed (Apple Mail) mean them to be seen
            // there; if the HTML also refers to the image by Content-ID, it is shown only where the HTML puts it.
            if (entity.ContentId is null || !body.Exists(p => p.Kind == BodyKind.Html))
            {
                body.Add(new BodyPart(BodyKind.Image, entity));
                return;
            }
            unreferenced.Add(entity);
            return;
        }
        attachments.Add(entity);
    }

    private static void CollectResource(MimeEntity entity, List<MimeEntity> attachments, List<MimeEntity> unreferenced)
    {
        if (entity.IsMultipart || entity.Disposition == "attachment") attachments.Add(entity);
        else unreferenced.Add(entity);
    }

    internal static bool IsShowableImage(MimeEntity entity)
        => entity.MediaType == "image" && entity.SubType is "png" or "jpeg" or "jpg" or "pjpeg" or "gif" or "webp" or "bmp" or "tiff" or "svg+xml";

    private static MimeEntity ChooseAlternative(List<MimeEntity> children, bool preferText)
    {
        if (preferText)
        {
            for (var i = children.Count - 1; i >= 0; i--)
            {
                if (children[i].Disposition != "attachment" && children[i].Is("text", "plain")) return children[i];
            }
        }
        // The last alternative is the richest (RFC 2046 section 5.1.4); HTML or a multipart holding it is preferred
        // over plain text, and anything this package cannot show is passed over.
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var c = children[i];
            if (c.Disposition == "attachment") continue;
            if (c.Is("text", "html") || c.IsMultipart && ContainsHtml(c)) return c;
        }
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var c = children[i];
            if (c.Disposition != "attachment" && (c.Is("text", "plain") || c.IsMultipart)) return c;
        }
        return children[0];
    }

    private static bool ContainsHtml(MimeEntity entity)
    {
        if (entity.Is("text", "html")) return true;
        if (!entity.IsMultipart) return false;
        foreach (var child in entity.Children)
        {
            if (ContainsHtml(child)) return true;
        }
        return false;
    }

    /// <summary>The first text part of the given subtype in the body area of the message, alternatives included.</summary>
    private static MimeEntity? FindBody(MimeEntity entity, string subType)
    {
        if (entity.IsMultipart)
        {
            foreach (var child in entity.Children)
            {
                if (child.Disposition == "attachment") continue;
                if (FindBody(child, subType) is { } found) return found;
            }
            return null;
        }
        return entity.Is("text", subType) && entity.Disposition != "attachment" ? entity : null;
    }

    private bool IsReferenced(MimeEntity part, HashSet<string> contentIds)
    {
        if (part.ContentId is not null && contentIds.Contains(part.ContentId)) return true;
        if (part.ContentLocation is not { Length: > 0 } location) return false;
        foreach (var body in _body)
        {
            if (body.Kind == BodyKind.Html && body.Entity.DecodeText().Contains(location, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The Content-IDs that the HTML body parts refer to with cid: URLs.</summary>
    private HashSet<string> ReferencedContentIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in _body)
        {
            if (part.Kind != BodyKind.Html) continue;
            var html = part.Entity.DecodeText();
            var i = 0;
            while ((i = html.IndexOf("cid:", i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                i += 4;
                var end = i;
                while (end < html.Length && html[end] is not ('"' or '\'' or ')' or '>' or ' ' or '\t' or '\r' or '\n' or '&')) end++;
                if (end > i) ids.Add(Unescape(html[i..end].Trim('<', '>')));
                i = end;
            }
        }
        foreach (var part in _body)
        {
            if (part.Kind == BodyKind.Image && part.Entity.ContentId is { } id) ids.Add(id);
        }
        return ids;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('%', StringComparison.Ordinal)) return s;
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return s;
        }
    }
}

internal enum BodyKind
{
    Text,
    Html,
    Image,
}

internal readonly record struct BodyPart(BodyKind Kind, MimeEntity Entity);
