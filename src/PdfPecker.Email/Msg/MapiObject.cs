using System.Text;

namespace PdfPecker.Email.Msg;

/// <summary>
/// The MAPI properties of one Outlook object (a message, a recipient or an attachment), wherever they are stored: in an
/// .msg file's compound file or in a winmail.dat (TNEF) stream. <see cref="OutlookMessage"/> writes MIME from them.
/// </summary>
internal abstract class MapiObject
{
    /// <summary>The encoding of the object's 8-bit strings and HTML.</summary>
    internal abstract Encoding Encoding { get; }

    /// <summary>A string property (PT_UNICODE or PT_STRING8), or null.</summary>
    internal abstract string? String(int id);

    /// <summary>A binary property (PT_BINARY), or null.</summary>
    internal abstract byte[]? Binary(int id);

    /// <summary>A PT_LONG property, or null.</summary>
    internal abstract int? Int(int id);

    /// <summary>A PT_BOOLEAN property, or null.</summary>
    internal abstract bool? Bool(int id);

    /// <summary>A PT_SYSTIME property in UTC, or null when it is missing or out of range.</summary>
    internal abstract DateTimeOffset? Time(int id);

    /// <summary>A message's recipients, in their order.</summary>
    internal abstract IReadOnlyList<MapiObject> Recipients { get; }

    /// <summary>A message's attachments, in their order.</summary>
    internal abstract IReadOnlyList<MapiObject> Attachments { get; }

    /// <summary>An attachment's attached message (attach method 5), or null.</summary>
    internal abstract MapiObject? EmbeddedMessage { get; }

    /// <summary>A FILETIME as a date, or null for the "none" values Outlook writes and dates out of any mail's range.</summary>
    internal static DateTimeOffset? FromFileTime(long value)
    {
        if (value <= 0 || value >= DateTime.MaxValue.ToFileTimeUtc() - 1) return null;
        var date = DateTimeOffset.FromFileTime(value).ToUniversalTime();
        return date.Year is < 1980 or > 4000 ? null : date;
    }
}
