using PdfPecker.Email.Mime;

namespace PdfPecker.Email;

/// <summary>A file attached to a message, or an attached message, which is offered as an .eml file.</summary>
public sealed class EmailAttachment
{
    private readonly MimeEntity _entity;

    internal EmailAttachment(MimeEntity entity, string fileName)
    {
        _entity = entity;
        FileName = fileName;
        var parameters = entity.DispositionParameters;
        CreationDate = Mime.HeaderValues.ParseDate(parameters.GetValueOrDefault("creation-date"));
        ModificationDate = Mime.HeaderValues.ParseDate(parameters.GetValueOrDefault("modification-date"));
    }

    /// <summary>
    /// The file name, decoded and made safe to save: no path, no control characters, and unique among the message's
    /// attachments. A part without a name gets one from its type, such as "attachment-2.pdf".
    /// </summary>
    public string FileName { get; }

    /// <summary>The MIME type in lower case, such as "application/pdf".</summary>
    public string MediaType => _entity.ContentType;

    /// <summary>The Content-ID without angle brackets, or null.</summary>
    public string? ContentId => _entity.ContentId;

    /// <summary>True when the part asked to be shown inline (Content-Disposition: inline) rather than as a file.</summary>
    public bool IsInline => _entity.Disposition == "inline";

    /// <summary>The file's creation date from Content-Disposition, or null.</summary>
    public DateTimeOffset? CreationDate { get; }

    /// <summary>The file's modification date from Content-Disposition, or null.</summary>
    public DateTimeOffset? ModificationDate { get; }

    /// <summary>The size of the decoded content in bytes.</summary>
    public int Size => _entity.DecodedLength;

    /// <summary>The decoded content. It is decoded the first time it is read and the same array is returned after that.</summary>
    public byte[] Data => _entity.DecodedBody;

    internal MimeEntity Entity => _entity;

    /// <summary>The file name and size, for debugging and logs.</summary>
    public override string ToString() => $"{FileName} ({MediaType}, {Size} bytes)";
}
