using System.Globalization;
using PdfPecker.Email.Html;
using PdfPecker.Email.Mime;

namespace PdfPecker.Email;

/// <summary>
/// Converts e-mail messages (.eml, MIME) to PDF with PdfPecker: a header block with the subject, sender, date,
/// recipients and attachments, then the body as a mail client shows it, with its inline images. The attachments are
/// embedded in the PDF. With <see cref="PdfOptions.Conformance"/> set to PDF/A-3b or PDF/A-3u the result is an archival
/// PDF that carries the attachments, and optionally the original message, as associated files.
/// </summary>
/// <remarks>
/// A message is untrusted input. Its HTML is sanitised (no scripts, frames, forms or objects; links only to http,
/// https, mailto and tel), its CSS cannot reach outside the body, nothing is read from the local file system, and
/// nothing is fetched from the network: remote images load only through a <see cref="PdfOptions.ResourceResolver"/>
/// the caller supplies. Damaged mail is shown as far as it can be read; only <see cref="EmailLimits"/> and PdfPecker's
/// own limits fail a conversion.
/// </remarks>
public static class EmailToPdf
{
    /// <summary>Converts a message's MIME bytes, such as an .eml file's content, or an Outlook .msg file to a PDF.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">The PDF options are invalid, for example attachments with PDF/A-2.</exception>
    /// <exception cref="PdfLimitException">The message goes over an <see cref="EmailLimits"/> value or a PdfPecker limit.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker cannot write the PDF.</exception>
    public static byte[] Convert(byte[] message, EmailPdfOptions? options = null) => Convert(message, options, CancellationToken.None);

    /// <summary>Converts a message's MIME bytes to a PDF, stopping when the token is cancelled.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">The PDF options are invalid, for example attachments with PDF/A-2.</exception>
    /// <exception cref="PdfLimitException">The message goes over an <see cref="EmailLimits"/> value or a PdfPecker limit.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker cannot write the PDF.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static byte[] Convert(byte[] message, EmailPdfOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= EmailPdfOptions.Default;
        return Convert(EmailMessage.Parse(message, options.Limits, cancellationToken), options, cancellationToken);
    }

    /// <summary>Converts a message that has already been read to a PDF.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">The PDF options are invalid, for example attachments with PDF/A-2.</exception>
    /// <exception cref="PdfLimitException">The document goes over a PdfPecker limit.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker cannot write the PDF.</exception>
    public static byte[] Convert(EmailMessage message, EmailPdfOptions? options = null) => Convert(message, options, CancellationToken.None);

    /// <summary>Converts a message that has already been read to a PDF, stopping when the token is cancelled.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentException">The PDF options are invalid, for example attachments with PDF/A-2.</exception>
    /// <exception cref="PdfLimitException">The document goes over a PdfPecker limit.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker cannot write the PDF.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static byte[] Convert(EmailMessage message, EmailPdfOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= EmailPdfOptions.Default;
        var (html, pdfOptions) = Prepare(message, options);
        return HtmlToPdf.Convert(html, pdfOptions, cancellationToken);
    }

    /// <summary>Reads a message from one stream and writes the PDF to another.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="pdf"/> is null.</exception>
    /// <exception cref="ArgumentException">The PDF options are invalid, for example attachments with PDF/A-2.</exception>
    /// <exception cref="PdfLimitException">The message goes over an <see cref="EmailLimits"/> value or a PdfPecker limit.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker cannot write the PDF.</exception>
    /// <exception cref="IOException">A stream cannot be read or written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static void Convert(Stream message, Stream pdf, EmailPdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= EmailPdfOptions.Default;
        var bytes = EmailMessage.ReadAll(message, options.Limits.MaxMessageBytes);
        var parsed = EmailMessage.Parse(bytes, options.Limits, cancellationToken);
        var (html, pdfOptions) = Prepare(parsed, options);
        HtmlToPdf.Convert(html, pdf, pdfOptions, cancellationToken);
    }

    /// <summary>Converts an .eml or Outlook .msg file to a PDF file.</summary>
    /// <exception cref="ArgumentNullException">A path is null.</exception>
    /// <exception cref="ArgumentException">The PDF options are invalid, for example attachments with PDF/A-2.</exception>
    /// <exception cref="PdfLimitException">The message goes over an <see cref="EmailLimits"/> value or a PdfPecker limit.</exception>
    /// <exception cref="PdfPeckerException">PdfPecker cannot write the PDF.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read or write a file.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static void ConvertFile(string emlPath, string pdfPath, EmailPdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emlPath);
        ArgumentNullException.ThrowIfNull(pdfPath);
        options ??= EmailPdfOptions.Default;
        byte[] bytes;
        using (var input = File.OpenRead(emlPath)) bytes = EmailMessage.ReadAll(input, options.Limits.MaxMessageBytes);
        // The PDF is written in full before the file is created, so a failed conversion leaves no half-written file.
        var pdf = Convert(bytes, options, cancellationToken);
        File.WriteAllBytes(pdfPath, pdf);
    }

    /// <summary>Converts a message's MIME bytes to a PDF on a thread-pool thread.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static Task<byte[]> ConvertAsync(byte[] message, EmailPdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Task.Run(() => Convert(message, options, cancellationToken), cancellationToken);
    }

    /// <summary>Reads a message from one stream and writes the PDF to another, asynchronously.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="pdf"/> is null.</exception>
    public static async Task ConvertAsync(Stream message, Stream pdf, EmailPdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(pdf);
        options ??= EmailPdfOptions.Default;
        var bytes = await ReadAllAsync(message, options.Limits.MaxMessageBytes, cancellationToken).ConfigureAwait(false);
        var result = await Task.Run(() => Convert(bytes, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        await pdf.WriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new PdfLimitException($"The message is over the limit of {maxBytes} bytes (EmailLimits.MaxMessageBytes).");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The HTML to convert and the PdfPecker options for it, copied from the caller's.</summary>
    internal static (string Html, PdfOptions Options) Prepare(EmailMessage message, EmailPdfOptions options)
    {
        var source = options.Pdf ?? new PdfOptions();
        var pdf = source.Clone();
        var callerResolver = source.ResourceResolver;
        var (html, parts) = EmailHtmlWriter.Write(message, options, remoteImages: callerResolver is not null);

        pdf.BaseDirectory = null;
        pdf.AllowFileAccess = false;
        pdf.FormFields = false;
        pdf.Title ??= string.IsNullOrWhiteSpace(message.Subject) ? options.Labels.NoSubject : message.Subject;
        if (pdf.Author is null && (message.From.Count > 0 ? message.From : message.Sender) is { Count: > 0 } from)
            pdf.Author = from[0].Name ?? from[0].Address;
        pdf.Language ??= message.Language;
        pdf.ResourceResolver = url =>
        {
            if (url.StartsWith(EmailHtmlWriter.PartScheme, StringComparison.Ordinal))
            {
                return int.TryParse(url.AsSpan(EmailHtmlWriter.PartScheme.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && index < parts.Count ? parts[index].DecodedBody : null;
            }
            if (message.FindResource(url) is { } part) return part.IsMultipart ? null : part.DecodedBody;
            // Only URLs that are not the message's own parts reach the caller, and only remote ones: cid: URLs and
            // relative paths have no meaning outside the message.
            if (callerResolver is null || url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return null;
            return HtmlSanitizer.Scheme(url) is "http" or "https" ? callerResolver(url) : null;
        };

        // The caller's attachments come first, then the message's, then the original message.
        var attachments = new List<PdfAttachment>(source.Attachments);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attachments) names.Add(a.FileName);
        if (options.EmbedAttachments)
        {
            foreach (var attachment in message.Attachments)
            {
                attachments.Add(new PdfAttachment(Unique(attachment.FileName, names), attachment.Data)
                {
                    MimeType = attachment.MediaType,
                    Relationship = PdfAttachmentRelationship.Supplement,
                    CreationDate = attachment.CreationDate,
                    ModDate = attachment.ModificationDate,
                });
            }
        }
        if (options.EmbedOriginalMessage)
        {
            var name = FileNames.Clean(options.OriginalMessageFileName);
            if (name.Length == 0) name = "message.eml";
            // An Outlook message is kept as the .msg file it was read from, not the MIME written for it.
            if (message.IsOutlookMessage && name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)) name = name[..^4] + ".msg";
            attachments.Add(new PdfAttachment(Unique(name, names), message.Original)
            {
                MimeType = message.IsOutlookMessage ? "application/vnd.ms-outlook" : "message/rfc822",
                Relationship = PdfAttachmentRelationship.Source,
            });
        }
        pdf.Attachments = attachments;
        return (html, pdf);
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var unique = name;
        for (var n = 2; !used.Add(unique); n++)
        {
            var ext = Path.GetExtension(name);
            unique = name[..^ext.Length] + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext;
        }
        return unique;
    }
}
