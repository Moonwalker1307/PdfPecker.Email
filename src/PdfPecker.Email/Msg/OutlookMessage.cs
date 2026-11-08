using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PdfPecker.Email.Msg;

/// <summary>
/// Turns an Outlook .msg file (MS-OXMSG: MAPI properties in a compound file) into the MIME message it stands for, so
/// the rest of the package reads it like any .eml: the headers, the HTML or plain text body (HTML encapsulated in
/// compressed RTF included), the attachments and attached messages, which become message/rfc822 parts. Missing or
/// damaged properties are left out; only the limits fail.
/// </summary>
internal static class OutlookMessage
{
    // Every leaf part is base64 and every header line starts with a name or a space, so no line of the output can be a
    // delimiter line; each level of attached messages has boundaries of its own, so a fixed boundary is safe.
    private const string Boundary = "=_pdfpecker-msg-";

    private static readonly System.Buffers.SearchValues<char> LocationSpecials = System.Buffers.SearchValues.Create("\"\\<>=?");

    private static readonly System.Buffers.SearchValues<char> AddressSpecials = System.Buffers.SearchValues.Create("<>,;\" ");

    private const int PropertySubject = 0x0037;
    private const int PropertyClientSubmitTime = 0x0039;
    private const int PropertySentRepresentingName = 0x0042;
    private const int PropertySentRepresentingEmail = 0x0065;
    private const int PropertyTransportHeaders = 0x007D;
    private const int PropertyRecipientType = 0x0C15;
    private const int PropertySenderName = 0x0C1A;
    private const int PropertySenderEmail = 0x0C1F;
    private const int PropertyDeliveryTime = 0x0E06;
    private const int PropertyBody = 0x1000;
    private const int PropertyRtfCompressed = 0x1009;
    private const int PropertyHtml = 0x1013;
    private const int PropertyMessageId = 0x1035;
    private const int PropertyDisplayName = 0x3001;
    private const int PropertyEmailAddress = 0x3003;
    private const int PropertyCreationTime = 0x3007;
    private const int PropertyModificationTime = 0x3008;
    private const int PropertySmtpAddress = 0x39FE;
    private const int PropertyAttachData = 0x3701;
    private const int PropertyAttachFileName = 0x3704;
    private const int PropertyAttachMethod = 0x3705;
    private const int PropertyAttachLongFileName = 0x3707;
    private const int PropertyAttachMimeTag = 0x370E;
    private const int PropertyAttachContentId = 0x3712;
    private const int PropertyAttachContentLocation = 0x3713;
    private const int PropertyInternetCodePage = 0x3FDE;
    private const int PropertyMessageCodePage = 0x3FFD;
    private const int PropertySenderSmtp = 0x5D01;
    private const int PropertySentRepresentingSmtp = 0x5D02;
    private const int PropertyAttachmentHidden = 0x7FFE;

    /// <summary>
    /// The MIME bytes of the message in an Outlook .msg file: the first <c>Length</c> bytes of the buffer, which is not
    /// copied to its exact size because attachments can make it large.
    /// </summary>
    internal static (byte[] Buffer, int Length) ToMime(byte[] msg, EmailLimits limits, CancellationToken cancellationToken)
    {
        var file = CompoundFile.Open(msg);
        if (file.Entries.Count == 0) return ([], 0);
        return ToMime(new StorageObject(file, file.Entries[0], 32), msg.Length, limits, cancellationToken, headers: true, body: true);
    }

    /// <summary>
    /// The MIME bytes of a message's MAPI properties, from an .msg file or a winmail.dat. Without
    /// <paramref name="headers"/> only the content is written (a part to put in place of a winmail.dat), and without
    /// <paramref name="body"/> only the attachments.
    /// </summary>
    internal static (byte[] Buffer, int Length) ToMime(MapiObject message, int sizeHint, EmailLimits limits, CancellationToken cancellationToken, bool headers, bool body)
    {
        // Attachments dominate the size, and base64 lines of 76 characters make them 78/57 times larger.
        var output = new MimeBuffer((int)Math.Min(Array.MaxLength, (long)sizeHint * 78 / 57 + 8192));
        var parts = 0;
        new Writer(limits, cancellationToken, output).Message(message, depth: 0, ref parts, headers, body);
        return (output.Buffer, output.Written.Length);
    }

    /// <summary>Whether the message has an HTML body, directly or encapsulated in compressed RTF.</summary>
    internal static bool HasHtmlBody(MapiObject message) => Writer.Body(message).Html is not null;

    /// <summary>Whether the message has any body text.</summary>
    internal static bool HasBody(MapiObject message) => Writer.Body(message) is var (html, text) && (html is not null || !string.IsNullOrWhiteSpace(text));

    private sealed class Writer(EmailLimits limits, CancellationToken cancellationToken, MimeBuffer output)
    {
        internal void Message(MapiObject properties, int depth, ref int parts, bool headers = true, bool body = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth >= limits.MaxNestingDepth)
                throw new PdfLimitException($"The Outlook message nests attached messages deeper than {limits.MaxNestingDepth} (EmailLimits.MaxNestingDepth).");
            if (headers) MessageHeaders(properties);

            var (html, text) = body ? Body(properties) : (null, null);
            var inline = new List<MapiObject>();
            var attached = new List<MapiObject>();
            foreach (var attachment in properties.Attachments)
            {
                if (++parts > limits.MaxParts)
                    throw new PdfLimitException($"The Outlook message has more than {limits.MaxParts} parts (EmailLimits.MaxParts).");
                // Attachments with a Content-ID are the HTML's images; they go with the body in a multipart/related,
                // and the rest are the message's attachments.
                (html is not null && attachment.String(PropertyAttachContentId) is { Length: > 0 } ? inline : attached).Add(attachment);
            }

            var boundary = Boundary + depth.ToString(CultureInfo.InvariantCulture) + "-";
            if (attached.Count == 0 && inline.Count == 0)
            {
                WriteBody(html, text, boundary + "a");
                return;
            }
            Raw("Content-Type", $"multipart/mixed; boundary=\"{boundary}m\"");
            output.Append("\r\n");
            if (body)
            {
                output.Append("--").Append(boundary).Append("m\r\n");
                if (inline.Count > 0)
                {
                    Raw("Content-Type", $"multipart/related; boundary=\"{boundary}r\"");
                    output.Append("\r\n");
                    output.Append("--").Append(boundary).Append("r\r\n");
                    WriteBody(html, text, boundary + "a");
                    foreach (var attachment in inline)
                    {
                        output.Append("--").Append(boundary).Append("r\r\n");
                        Attachment(attachment, inlineImage: true, depth, ref parts);
                    }
                    output.Append("--").Append(boundary).Append("r--\r\n");
                }
                else
                {
                    WriteBody(html, text, boundary + "a");
                }
            }
            foreach (var attachment in attached)
            {
                output.Append("--").Append(boundary).Append("m\r\n");
                Attachment(attachment, inlineImage: false, depth, ref parts);
            }
            output.Append("--").Append(boundary).Append("m--\r\n");
        }

        private void MessageHeaders(MapiObject properties)
        {
            // From and Sender: Outlook stores the mailbox the message is from as "sent representing", and the one who
            // sent it, when someone sent it on another's behalf, as the sender.
            var sender = Mailbox(properties.String(PropertySenderName), properties.String(PropertySenderSmtp), properties.String(PropertySenderEmail));
            var representing = Mailbox(properties.String(PropertySentRepresentingName), properties.String(PropertySentRepresentingSmtp), properties.String(PropertySentRepresentingEmail));
            var from = representing ?? sender;
            var transport = TransportHeaders(properties.String(PropertyTransportHeaders));

            Header("From", from);
            if (sender is not null && representing is not null && !string.Equals(sender, representing, StringComparison.OrdinalIgnoreCase))
                Header("Sender", sender);
            WriteRecipients(properties);
            CopyHeader(transport, "Reply-To");
            var date = properties.Time(PropertyClientSubmitTime) ?? properties.Time(PropertyDeliveryTime);
            if (date is { } d) Raw("Date", d.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture));
            else CopyHeader(transport, "Date");
            Header("Subject", properties.String(PropertySubject));
            if (properties.String(PropertyMessageId) is { Length: > 0 } id) Raw("Message-ID", id);
            CopyHeader(transport, "Content-Language");
            Raw("MIME-Version", "1.0");
        }

        internal static (string? Html, string? Text) Body(MapiObject properties)
        {
            var text = properties.String(PropertyBody);
            if (properties.Binary(PropertyHtml) is { Length: > 0 } htmlBytes)
                return (properties.Encoding.GetString(htmlBytes), text);
            if (properties.String(PropertyHtml) is { Length: > 0 } htmlString)
                return (htmlString, text);
            if (properties.Binary(PropertyRtfCompressed) is { Length: > 0 } compressed)
            {
                var (rtfText, isHtml) = RtfText.Read(CompressedRtf.Decompress(compressed));
                if (isHtml) return (rtfText, text);
                if (string.IsNullOrEmpty(text)) return (null, rtfText);
            }
            return (null, text);
        }

        private void WriteBody(string? html, string? text, string boundary)
        {
            if (html is not null && !string.IsNullOrEmpty(text))
            {
                Raw("Content-Type", $"multipart/alternative; boundary=\"{boundary}\"");
                output.Append("\r\n");
                output.Append("--").Append(boundary).Append("\r\n");
                TextPart("text/plain", text);
                output.Append("--").Append(boundary).Append("\r\n");
                TextPart("text/html", html);
                output.Append("--").Append(boundary).Append("--\r\n");
                return;
            }
            if (html is not null) TextPart("text/html", html);
            else TextPart("text/plain", text ?? "");
        }

        private void TextPart(string type, string content)
        {
            Raw("Content-Type", type + "; charset=utf-8");
            Raw("Content-Transfer-Encoding", "base64");
            output.Append("\r\n");
            output.Base64(Mime.Charsets.Utf8Encoding.GetBytes(content));
        }

        private void Attachment(MapiObject properties, bool inlineImage, int depth, ref int parts)
        {
            var method = properties.Int(PropertyAttachMethod) ?? 1;
            var name = properties.String(PropertyAttachLongFileName) ?? properties.String(PropertyAttachFileName) ?? properties.String(PropertyDisplayName);
            var disposition = inlineImage ? "inline" : "attachment";
            var dates = DateParameters(properties);

            // An attached message is a storage of its own, written out as a message/rfc822 part.
            // It is written in place as 8bit rather than base64, which would make every level of nesting a third
            // larger than the one inside it.
            if (method == 5 && properties.EmbeddedMessage is { } embedded)
            {
                // The display name is the attached message's subject; the short file name is a truncated copy of it.
                name = properties.String(PropertyDisplayName) ?? properties.String(PropertyAttachLongFileName) ?? embedded.String(PropertySubject) ?? name;
                name = string.IsNullOrWhiteSpace(name) ? "message.eml" : name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? name : name + ".eml";
                Raw("Content-Type", "message/rfc822");
                Raw("Content-Disposition", disposition + "; " + FileNameParameter(name) + dates);
                Raw("Content-Transfer-Encoding", "8bit");
                output.Append("\r\n");
                Message(embedded, depth + 1, ref parts);
                return;
            }

            // By value is the only kind with the file's bytes; OLE objects and links to files have none to keep.
            var data = method == 1 ? properties.Binary(PropertyAttachData) ?? [] : [];
            var mediaType = Token(properties.String(PropertyAttachMimeTag)) ?? "application/octet-stream";
            if (mediaType.IndexOf('/') <= 0) mediaType = "application/octet-stream";
            // A .eml file attached by value is still a file, not a nested message part, so it keeps a file's type.
            if (mediaType == "application/octet-stream" && FileNames.MediaTypeOf(name) is { } guessed && guessed != "message/rfc822") mediaType = guessed;
            Raw("Content-Type", mediaType);
            Raw("Content-Disposition", disposition + (string.IsNullOrWhiteSpace(name) ? "" : "; " + FileNameParameter(name)) + dates);
            if (properties.String(PropertyAttachContentId) is { Length: > 0 } contentId)
                Raw("Content-ID", "<" + Clean(contentId).Trim('<', '>') + ">");
            if (properties.String(PropertyAttachContentLocation) is { Length: > 0 } location)
                Header("Content-Location", location);
            else if (inlineImage && !string.IsNullOrWhiteSpace(name) && name.AsSpan().IndexOfAny(LocationSpecials) < 0)
                // Outlook also shows an image whose HTML refers to it by its file name rather than by its Content-ID.
                Raw("Content-Location", name);
            if (properties.Bool(PropertyAttachmentHidden) == true && !inlineImage)
                Raw("X-Outlook-Hidden", "true");
            Raw("Content-Transfer-Encoding", "base64");
            output.Append("\r\n");
            output.Base64(data);
        }

        private static string DateParameters(MapiObject properties)
        {
            var result = "";
            if (properties.Time(PropertyCreationTime) is { } created)
                result += "; creation-date=\"" + created.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture) + "\"";
            if (properties.Time(PropertyModificationTime) is { } modified)
                result += "; modification-date=\"" + modified.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'", CultureInfo.InvariantCulture) + "\"";
            return result;
        }

        private void WriteRecipients(MapiObject message)
        {
            var to = new List<string>();
            var cc = new List<string>();
            var bcc = new List<string>();
            foreach (var properties in message.Recipients)
            {
                var mailbox = Mailbox(properties.String(PropertyDisplayName), properties.String(PropertySmtpAddress), properties.String(PropertyEmailAddress));
                if (mailbox is null) continue;
                (properties.Int(PropertyRecipientType) switch { 2 => cc, 3 => bcc, _ => to }).Add(mailbox);
            }
            if (to.Count > 0) output.Append("To: ").AppendJoin(",\r\n ", to).Append("\r\n");
            if (cc.Count > 0) output.Append("Cc: ").AppendJoin(",\r\n ", cc).Append("\r\n");
            if (bcc.Count > 0) output.Append("Bcc: ").AppendJoin(",\r\n ", bcc).Append("\r\n");
        }

        /// <summary>A mailbox as header text, with the name as an encoded word, or null when there is neither.</summary>
        private static string? Mailbox(string? name, string? smtp, string? address)
        {
            // Exchange addresses ("/O=ORG/OU=..."), which most internal mail has in place of an SMTP address, mean
            // nothing to a reader; they are dropped when the SMTP address is missing.
            var email = Clean(smtp ?? "").Trim();
            if (email.IndexOf('@') <= 0) email = Clean(address ?? "").Trim();
            if (email.IndexOf('@') <= 0 || email.AsSpan().IndexOfAny(AddressSpecials) >= 0) email = "";
            name = Clean(name ?? "").Trim();
            if (name.Length == 0 && email.Length == 0) return null;
            if (string.Equals(name, email, StringComparison.OrdinalIgnoreCase)) name = "";
            if (name.Length == 0) return "<" + email + ">";
            var word = EncodedWord(name);
            return email.Length == 0 ? word + " <>" : word + " <" + email + ">";
        }

        private static List<(string Name, string Value)> TransportHeaders(string? headers)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(headers)) return result;
            foreach (var line in headers.Split('\n'))
            {
                var text = line.TrimEnd('\r');
                if (text.Length == 0) break;
                if (text[0] is ' ' or '\t')
                {
                    if (result.Count > 0) result[^1] = (result[^1].Item1, result[^1].Item2 + " " + text.Trim());
                    continue;
                }
                var colon = text.IndexOf(':');
                if (colon > 0) result.Add((text[..colon].Trim(), text[(colon + 1)..].Trim()));
            }
            return result;
        }

        private void CopyHeader(List<(string Name, string Value)> headers, string name)
        {
            foreach (var (key, value) in headers)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    Raw(name, value);
                    return;
                }
            }
        }

        private void Header(string name, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            // From and Sender are already mailbox text with encoded names; other values are encoded whole.
            Raw(name, name is "From" or "Sender" ? value : EncodedWord(Clean(value)));
        }

        private void Raw(string name, string value) => output.Append(name).Append(": ").Append(Clean(value)).Append("\r\n");

        private static string FileNameParameter(string name)
        {
            var clean = Clean(name);
            var bytes = Mime.Charsets.Utf8Encoding.GetBytes(clean);
            var sb = new StringBuilder("filename*=utf-8''", bytes.Length * 3 + 20);
            foreach (var b in bytes)
            {
                if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'-' or (byte)'_')
                    sb.Append((char)b);
                else
                    sb.Append('%').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 15]);
            }
            return sb.ToString();
        }

        private static string? Token(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            foreach (var c in trimmed)
            {
                if (c <= ' ' || c >= 127 || c is ';' or '"' or '(' or ')' or '<' or '>' or ',' or '\\') return null;
            }
            return trimmed.ToLowerInvariant();
        }

        /// <summary>Header text without control characters, so no value can end the header or start a new one.</summary>
        private static string Clean(string value)
        {
            if (!value.AsSpan().ContainsAnyInRange('\0', '\x1F') && !value.Contains('\x7F', StringComparison.Ordinal)) return value;
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] < ' ' || chars[i] == '\x7F') chars[i] = ' ';
            }
            return new string(chars);
        }

        /// <summary>Text as RFC 2047 encoded words; plain ASCII words are left as they are.</summary>
        private static string EncodedWord(string text)
        {
            var plain = true;
            foreach (var c in text)
            {
                if (c is < ' ' or > '~' or '"' or '\\' or '(' or ')' or '<' or '>' or ',' or ';' or ':' or '@' or '[' or ']' or '=' or '?')
                {
                    plain = false;
                    break;
                }
            }
            if (plain) return text;
            var sb = new StringBuilder(text.Length * 2);
            // Each word holds at most 45 bytes of whole characters, so a long name is split into folded lines.
            var start = 0;
            while (start < text.Length)
            {
                var end = start;
                var bytes = 0;
                while (end < text.Length)
                {
                    var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1;
                    var size = Mime.Charsets.Utf8Encoding.GetByteCount(text.AsSpan(end, width));
                    if (bytes + size > 45 && end > start) break;
                    bytes += size;
                    end += width;
                }
                if (sb.Length > 0) sb.Append("\r\n ");
                sb.Append("=?utf-8?B?").Append(Convert.ToBase64String(Mime.Charsets.Utf8Encoding.GetBytes(text[start..end]))).Append("?=");
                start = end;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The MIME being written, as UTF-8 bytes: header text is UTF-8 (raw only where the transport headers had it raw),
    /// and attachments go straight from their bytes to base64 lines without a string in between.
    /// </summary>
    private sealed class MimeBuffer(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(capacity, 256)];
        private int _length;

        internal ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

        internal MimeBuffer Append(string text)
        {
            Reserve(Mime.Charsets.Utf8Encoding.GetMaxByteCount(text.Length));
            _length += Mime.Charsets.Utf8Encoding.GetBytes(text, _buffer.AsSpan(_length));
            return this;
        }

        internal MimeBuffer AppendJoin(string separator, List<string> values)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) Append(separator);
                Append(values[i]);
            }
            return this;
        }

        /// <summary>Data in base64 lines of 76 characters.</summary>
        internal void Base64(ReadOnlySpan<byte> data)
        {
            var lines = (data.Length + 56) / 57;
            Reserve(lines * 78);
            for (var i = 0; i < data.Length; i += 57)
            {
                System.Buffers.Text.Base64.EncodeToUtf8(data.Slice(i, Math.Min(57, data.Length - i)), _buffer.AsSpan(_length), out _, out var written);
                _length += written;
                _buffer[_length++] = (byte)'\r';
                _buffer[_length++] = (byte)'\n';
            }
        }

        internal byte[] Buffer => _buffer;

        private void Reserve(int count)
        {
            if (_buffer.Length - _length >= count) return;
            var size = Math.Max((long)_buffer.Length * 2, (long)_length + count);
            if (size > Array.MaxLength)
                throw new PdfLimitException("The Outlook message is too large to convert.");
            Array.Resize(ref _buffer, (int)size);
        }
    }

    /// <summary>The MAPI properties of one object (message, recipient or attachment) in a .msg file.</summary>
    private sealed class StorageObject : MapiObject
    {
        private readonly CompoundFile _file;
        private readonly Dictionary<int, (int Type, long Value)> _fixed = [];
        private List<MapiObject>? _recipients, _attachments;

        internal StorageObject(CompoundFile file, DirectoryEntry storage, int headerSize)
        {
            _file = file;
            Children = file.Children(storage);
            if (Children.TryGetValue("__properties_version1.0", out var stream))
            {
                var bytes = file.Read(stream);
                for (var i = headerSize; i + 16 <= bytes.Length; i += 16)
                {
                    var tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
                    _fixed.TryAdd((int)(tag >> 16), ((int)(tag & 0xFFFF), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(i + 8))));
                }
            }
            var codePage = Int(PropertyInternetCodePage) ?? Int(PropertyMessageCodePage) ?? 1252;
            Encoding = RtfText.CodePage(codePage);
        }

        private Dictionary<string, DirectoryEntry> Children { get; }

        /// <summary>The encoding of the object's 8-bit strings and HTML.</summary>
        internal override Encoding Encoding { get; }

        internal override string? String(int id)
        {
            var name = "__substg1.0_" + id.ToString("X4", CultureInfo.InvariantCulture);
            if (Children.TryGetValue(name + "001F", out var unicode))
                return TrimNul(Encoding.Unicode.GetString(_file.Read(unicode)));
            if (Children.TryGetValue(name + "001E", out var ansi))
                return TrimNul(Encoding.GetString(_file.Read(ansi)));
            return null;
        }

        internal override byte[]? Binary(int id) =>
            Children.TryGetValue("__substg1.0_" + id.ToString("X4", CultureInfo.InvariantCulture) + "0102", out var entry) ? _file.Read(entry) : null;

        internal override IReadOnlyList<MapiObject> Recipients => _recipients ??= Storages("__recip_version1.0_", 8);

        internal override IReadOnlyList<MapiObject> Attachments => _attachments ??= Storages("__attach_version1.0_", 8);

        internal override MapiObject? EmbeddedMessage =>
            Children.TryGetValue("__substg1.0_3701000D", out var entry) && entry.Type == 1 ? new StorageObject(_file, entry, 24) : null;

        // Directory order is a tree order, not the order in which the objects were added; their names count up.
        private List<MapiObject> Storages(string prefix, int headerSize)
        {
            var found = new List<(string Name, DirectoryEntry Entry)>();
            foreach (var (name, entry) in Children)
            {
                if (entry.Type == 1 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) found.Add((name, entry));
            }
            found.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return found.ConvertAll(f => (MapiObject)new StorageObject(_file, f.Entry, headerSize));
        }

        internal override int? Int(int id) => _fixed.TryGetValue(id, out var p) && p.Type == 0x0003 ? (int)p.Value : null;

        internal override bool? Bool(int id) => _fixed.TryGetValue(id, out var p) && p.Type == 0x000B ? (p.Value & 0xFFFF) != 0 : null;

        internal override DateTimeOffset? Time(int id) => _fixed.TryGetValue(id, out var p) && p.Type == 0x0040 ? FromFileTime(p.Value) : null;

        private static string TrimNul(string value)
        {
            var end = value.IndexOf('\0', StringComparison.Ordinal);
            return end < 0 ? value : value[..end];
        }
    }
}
