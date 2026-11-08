using System.Globalization;
using System.Text;
using PdfPecker.Email.Mime;

namespace PdfPecker.Email.Html;

/// <summary>
/// Writes the HTML document that PdfPecker turns into the PDF: a header block with the message's subject, sender,
/// date, recipients and attachments, then the body. The header uses inline styles and the message's own CSS is scoped
/// to the body's wrapper, so a message cannot restyle or hide the header.
/// </summary>
internal sealed class EmailHtmlWriter
{
    internal const string BodyClass = "pdfpecker-email-body";

    // Images in the body that have no Content-ID are referred to by these made-up URLs, which the resolver serves.
    internal const string PartScheme = "pdfpecker-part:";

    private readonly EmailMessage _message;
    private readonly EmailPdfOptions _options;
    private readonly bool _remoteImages;
    private readonly List<MimeEntity> _parts = [];

    private EmailHtmlWriter(EmailMessage message, EmailPdfOptions options, bool remoteImages)
    {
        _message = message;
        _options = options;
        _remoteImages = remoteImages;
    }

    /// <summary>The HTML, and the parts that the made-up part URLs in it refer to.</summary>
    internal static (string Html, List<MimeEntity> Parts) Write(EmailMessage message, EmailPdfOptions options, bool remoteImages)
    {
        var writer = new EmailHtmlWriter(message, options, remoteImages);
        return (writer.Write(), writer._parts);
    }

    private string Write()
    {
        var body = new StringBuilder(4096);
        var css = new StringBuilder();
        string? bodyStyle = null;
        string? bodyClass = null;
        string? bodyHints = null;
        foreach (var part in _message.SelectBody(_options.PreferPlainText))
        {
            switch (part.Kind)
            {
                case BodyKind.Html:
                {
                    var sanitizer = HtmlSanitizer.Sanitize(part.Entity.DecodeText(), body, css, ImageAllowed, BodyClass);
                    bodyStyle ??= sanitizer.BodyStyle;
                    bodyHints ??= sanitizer.BodyHints;
                    bodyClass ??= sanitizer.BodyClass;
                    break;
                }
                case BodyKind.Text:
                {
                    var format = part.Entity.Parameter("format");
                    var delsp = part.Entity.Parameter("delsp");
                    body.Append("<div class=\"pdfpecker-email-text\">");
                    PlainText.AppendHtml(body, part.Entity.DecodeText(),
                        string.Equals(format, "flowed", StringComparison.OrdinalIgnoreCase),
                        string.Equals(delsp, "yes", StringComparison.OrdinalIgnoreCase));
                    body.Append("</div>");
                    break;
                }
                case BodyKind.Image:
                {
                    _parts.Add(part.Entity);
                    body.Append("<div class=\"pdfpecker-email-image\"><img src=\"").Append(PartScheme)
                        .Append((_parts.Count - 1).ToString(CultureInfo.InvariantCulture)).Append("\" alt=\"\"></div>");
                    break;
                }
            }
        }

        var subject = string.IsNullOrWhiteSpace(_message.Subject) ? _options.Labels.NoSubject : _message.Subject;
        var html = new StringBuilder(body.Length + css.Length + 2048);
        html.Append("<!DOCTYPE html>\n<html");
        if (_message.Language is { } language && IsLanguageTag(language)) html.Append(" lang=\"").Append(language).Append('"');
        html.Append("><head><meta charset=\"utf-8\"><title>").AppendEscaped(subject).Append("</title>\n<style>\n")
            .Append("body{margin:0;font-family:Helvetica,Arial,sans-serif;font-size:10.5pt;color:#000;background:#fff}\n")
            .Append('.').Append(BodyClass).Append(" img{max-width:100%}\n")
            .Append(".pdfpecker-email-text{white-space:pre-wrap;overflow-wrap:anywhere}\n")
            .Append(".pdfpecker-email-image{margin:8pt 0}\n");
        if (bodyHints is not null) html.Append('.').Append(BodyClass).Append('{').Append(bodyHints).Append("}\n");
        html
            .Append("</style>\n");
        if (!IsBlank(css)) html.Append("<style>\n").Append(css).Append("</style>\n");
        html.Append("</head><body>\n");
        if (_options.ShowHeaders) WriteHeader(html, subject);
        html.Append("<div class=\"").Append(BodyClass);
        if (!string.IsNullOrWhiteSpace(bodyClass)) html.Append(' ').AppendEscaped(bodyClass);
        html.Append('"');
        if (bodyStyle is not null) html.Append(" style=\"").AppendEscaped(bodyStyle).Append('"');
        html.Append(">\n").Append(body).Append("\n</div>\n</body></html>\n");
        return html.ToString();
    }

    private bool ImageAllowed(string src)
    {
        if (src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return true;
        if (_message.HasLocation(src)) return true;
        var scheme = HtmlSanitizer.Scheme(src);
        return _remoteImages && scheme is "http" or "https";
    }

    private void WriteHeader(StringBuilder html, string subject)
    {
        var labels = _options.Labels;
        // Inline styles, so the message's CSS cannot change the header.
        html.Append("<div style=\"border-bottom:1px solid #b4b4b4;padding:0 0 8pt 0;margin:0 0 12pt 0;font-family:Helvetica,Arial,sans-serif;font-size:10pt;color:#000;background:#fff;display:block\">")
            .Append("<div style=\"font-size:15pt;font-weight:bold;margin:0 0 6pt 0;display:block\">").AppendEscaped(subject).Append("</div>")
            .Append("<table style=\"border-collapse:collapse;border:0;margin:0;font-size:10pt;display:table\">");
        var from = _message.From.Count > 0 ? _message.From : _message.Sender;
        Row(html, labels.From, Addresses(from));
        if (_message.Date is { } date) Row(html, labels.Date, _options.DateFormatter?.Invoke(date) ?? FormatDate(date));
        Row(html, labels.To, Addresses(_message.To));
        Row(html, labels.Cc, Addresses(_message.Cc));
        Row(html, labels.Bcc, Addresses(_message.Bcc));
        if (_message.ReplyTo.Count > 0 && !SameAddresses(_message.ReplyTo, from)) Row(html, labels.ReplyTo, Addresses(_message.ReplyTo));
        if (_message.Attachments.Count > 0)
        {
            var list = new StringBuilder();
            foreach (var attachment in _message.Attachments)
            {
                if (list.Length > 0) list.Append(", ");
                list.Append(attachment.FileName).Append(" (").Append(FormatSize(attachment.Size)).Append(')');
            }
            Row(html, labels.Attachments, list.ToString());
        }
        html.Append("</table></div>\n");
    }

    private static void Row(StringBuilder html, string label, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        html.Append("<tr><td style=\"color:#555;padding:1pt 10pt 1pt 0;vertical-align:top;white-space:nowrap;border:0\">").AppendEscaped(label)
            .Append("</td><td style=\"padding:1pt 0;vertical-align:top;border:0;overflow-wrap:anywhere\">").AppendEscaped(value).Append("</td></tr>");
    }

    private static string Addresses(IReadOnlyList<EmailAddress> addresses)
    {
        if (addresses.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var a in addresses)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(a);
        }
        return sb.ToString();
    }

    private static bool SameAddresses(IReadOnlyList<EmailAddress> a, IReadOnlyList<EmailAddress> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Address.Equals(b[i].Address, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static string FormatDate(DateTimeOffset date)
        => date.ToString("ddd, d MMM yyyy HH:mm zzz", CultureInfo.InvariantCulture);

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };

    private static bool IsBlank(StringBuilder sb)
    {
        foreach (var chunk in sb.GetChunks())
        {
            if (!chunk.Span.IsWhiteSpace()) return false;
        }
        return true;
    }

    private static bool IsLanguageTag(string tag)
    {
        foreach (var c in tag)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }
        return true;
    }
}
