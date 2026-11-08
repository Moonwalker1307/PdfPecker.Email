namespace PdfPecker.Email;

/// <summary>Options for <see cref="EmailToPdf"/>.</summary>
public sealed class EmailPdfOptions
{
    /// <summary>
    /// The PdfPecker options for the PDF: page size, margins, fonts, <see cref="PdfOptions.Conformance"/> (PDF/A-3b or
    /// PDF/A-3u to archive with attachments), accessibility, signing and so on. They are copied, never changed.
    /// <see cref="PdfOptions.Title"/>, <see cref="PdfOptions.Author"/> and <see cref="PdfOptions.Language"/> default to
    /// the message's subject, sender and Content-Language. Because a message is untrusted input, local file access is
    /// always off: <see cref="PdfOptions.BaseDirectory"/> and <see cref="PdfOptions.AllowFileAccess"/> are ignored.
    /// <see cref="PdfOptions.ResourceResolver"/>, when set, is asked for every image and style sheet URL the message
    /// refers to that is not one of its own parts, so remote images load only when the caller fetches them; it receives
    /// URLs written by the sender and must treat them as untrusted.
    /// </summary>
    public PdfOptions Pdf { get; set; } = new();

    /// <summary>
    /// Embed the message's attachments in the PDF, listed in the viewer's attachments panel. Default: true. PDF/A-2
    /// does not allow attachments, so with <see cref="PdfConformance.PdfA2b"/> or <see cref="PdfConformance.PdfA2u"/>
    /// set this to false or use PDF/A-3.
    /// </summary>
    public bool EmbedAttachments { get; set; } = true;

    /// <summary>
    /// Also embed the original message as an .eml file (or the .msg file an Outlook message was read from), with the
    /// PDF/A-3 relationship Source, so the archived PDF keeps the message exactly as received. Default: false.
    /// </summary>
    public bool EmbedOriginalMessage { get; set; }

    /// <summary>
    /// The file name of the embedded original message. Default: "message.eml"; an Outlook message's ".eml" becomes ".msg".
    /// </summary>
    public string OriginalMessageFileName { get; set; } = "message.eml";

    /// <summary>Write the header block (subject, sender, date, recipients, attachments) above the body. Default: true.</summary>
    public bool ShowHeaders { get; set; } = true;

    /// <summary>
    /// Show the plain text alternative of a message that has both plain text and HTML. Default: false, the HTML.
    /// </summary>
    public bool PreferPlainText { get; set; }

    /// <summary>The words of the header block. Default: English.</summary>
    public EmailLabels Labels { get; set; } = new();

    /// <summary>
    /// Formats the Date header for the header block. Default: null, which writes it with the sender's time zone offset
    /// as "Sun, 8 Nov 2026 18:04 +01:00". Use it to show another time zone or a local format.
    /// </summary>
    public Func<DateTimeOffset, string>? DateFormatter { get; set; }

    /// <summary>The limits on the size and structure of the message.</summary>
    public EmailLimits Limits { get; set; } = new();

    internal static readonly EmailPdfOptions Default = new();
}
