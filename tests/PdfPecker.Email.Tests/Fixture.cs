using PdfPecker.Email.Html;

namespace PdfPecker.Email.Tests;

internal static class Fixture
{
    internal static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    internal static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    internal static EmailMessage Message(string name) => EmailMessage.Parse(Bytes(name));

    /// <summary>A message from text, with CRLF line ends as on the wire.</summary>
    internal static EmailMessage FromText(string text, EmailLimits? limits = null)
        => EmailMessage.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n")), limits);

    internal static string Html(EmailMessage message, EmailPdfOptions? options = null)
        => EmailToPdf.Prepare(message, options ?? new EmailPdfOptions()).Html;

    /// <summary>The sanitised fragment of an HTML string, without the document around it.</summary>
    internal static string Sanitize(string html, bool remote = false)
    {
        var output = new System.Text.StringBuilder();
        var css = new System.Text.StringBuilder();
        HtmlSanitizer.Sanitize(html, output, css, src => src.StartsWith("cid:", StringComparison.Ordinal) || remote, "scope");
        return output.ToString();
    }
}
