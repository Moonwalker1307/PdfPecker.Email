using System.Text;

namespace PdfPecker.Email.Tests;

public sealed class ConversionTests
{
    [Fact]
    public void Message_becomes_a_pdf_with_header_body_and_attachments()
    {
        var pdf = EmailToPdf.Convert(Fixture.Bytes("newsletter.eml"));
        var text = PdfText.Extract(pdf);
        Assert.Contains("Weekly update: café opening", text);
        Assert.Contains("Example News <news@example.org>", text);
        Assert.Contains("Our new café opens on Friday", text);
        // The PDF lists embedded files by name.
        var attachments = PdfAttachments.List(pdf);
        Assert.Equal(["Programme.pdf", "unused.png"], attachments.Select(a => a.FileName).Order(StringComparer.Ordinal));
        var programme = attachments.Single(a => a.FileName == "Programme.pdf");
        Assert.Equal("application/pdf", programme.MimeType);
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(programme.Data));
    }

    [Fact]
    public void Pdf_a3_carries_attachments_and_the_original_message()
    {
        var eml = Fixture.Bytes("forwarded.eml");
        var options = new EmailPdfOptions
        {
            EmbedOriginalMessage = true,
            Pdf = new PdfOptions { Conformance = PdfConformance.PdfA3u, CreationDate = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero) },
        };
        var pdf = EmailToPdf.Convert(eml, options);
        var attachments = PdfAttachments.List(pdf);
        Assert.Equal(5, attachments.Count);
        var original = attachments.Single(a => a.FileName == "message.eml");
        Assert.Equal(PdfAttachmentRelationship.Source, original.Relationship);
        Assert.Equal(eml, original.Data);
        Assert.All(attachments.Where(a => a != original), a => Assert.Equal(PdfAttachmentRelationship.Supplement, a.Relationship));
        Assert.Contains("pdfaid:part", Encoding.Latin1.GetString(pdf));
        // Fixed dates make the output reproducible.
        Assert.Equal(pdf, EmailToPdf.Convert(eml, options));
    }

    [Fact]
    public void Pdf_a2_rejects_attachments_unless_they_are_left_out()
    {
        var eml = Fixture.Bytes("forwarded.eml");
        Assert.ThrowsAny<ArgumentException>(() => EmailToPdf.Convert(eml, new EmailPdfOptions { Pdf = new PdfOptions { Conformance = PdfConformance.PdfA2b } }));
        var pdf = EmailToPdf.Convert(eml, new EmailPdfOptions { EmbedAttachments = false, Pdf = new PdfOptions { Conformance = PdfConformance.PdfA2b } });
        Assert.Empty(PdfAttachments.List(pdf));
    }

    [Fact]
    public void Caller_options_are_not_changed()
    {
        var pdfOptions = new PdfOptions { BaseDirectory = "/", AllowFileAccess = true };
        pdfOptions.Attachments.Add(new PdfAttachment("extra.txt", [1, 2, 3]));
        var pdf = EmailToPdf.Convert(Fixture.Bytes("newsletter.eml"), new EmailPdfOptions { Pdf = pdfOptions });
        Assert.Single(pdfOptions.Attachments);
        Assert.Null(pdfOptions.Title);
        Assert.Equal("/", pdfOptions.BaseDirectory);
        Assert.Equal(["Programme.pdf", "extra.txt", "unused.png"], PdfAttachments.List(pdf).Select(a => a.FileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Inline_images_are_served_and_remote_ones_only_by_the_caller()
    {
        var message = Fixture.Message("newsletter.eml");
        var (html, options) = EmailToPdf.Prepare(message, new EmailPdfOptions());
        Assert.DoesNotContain("tracker.example.net", html);
        Assert.NotNull(options.ResourceResolver!("cid:logo@example.org"));
        Assert.NotNull(options.ResourceResolver("cid:LOGO%40example.org"));
        Assert.NotNull(options.ResourceResolver("dot.png"));
        Assert.Null(options.ResourceResolver("cid:missing"));
        Assert.Null(options.ResourceResolver("https://tracker.example.net/pixel.gif"));

        var asked = new List<string>();
        var remote = new EmailPdfOptions { Pdf = new PdfOptions { ResourceResolver = url => { asked.Add(url); return null; } } };
        (html, options) = EmailToPdf.Prepare(message, remote);
        Assert.Contains("https://tracker.example.net/pixel.gif?id=42", html);
        Assert.NotNull(options.ResourceResolver!("cid:logo@example.org"));
        options.ResourceResolver("cid:missing");
        options.ResourceResolver("/etc/passwd");
        options.ResourceResolver("https://tracker.example.net/pixel.gif?id=42");
        Assert.Equal(["https://tracker.example.net/pixel.gif?id=42"], asked);
        Assert.Null(options.BaseDirectory);
        Assert.False(options.AllowFileAccess);
    }

    [Fact]
    public void Labels_dates_and_metadata_follow_the_options()
    {
        var message = Fixture.Message("plain-flowed.eml");
        var options = new EmailPdfOptions
        {
            Labels = new EmailLabels { From = "Fra", Date = "Sendt", To = "Til" },
            DateFormatter = d => d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
        };
        var (html, pdf) = EmailToPdf.Prepare(message, options);
        Assert.Contains(">Fra<", html);
        Assert.Contains(">Sendt<", html);
        Assert.Contains("2026-11-08 17:04 UTC", html);
        Assert.Contains("<html lang=\"da\">", html);
        Assert.Equal("Møde om budget for næste år", pdf.Title);
        Assert.Equal("Søren Æblegaard", pdf.Author);
        Assert.Equal("da", pdf.Language);

        var noHeader = EmailToPdf.Prepare(message, new EmailPdfOptions { ShowHeaders = false }).Html;
        Assert.DoesNotContain("soren@example.dk", noHeader);
    }

    [Fact]
    public void A_message_without_subject_gets_the_no_subject_title()
    {
        var message = Fixture.FromText("From: a@example.com\n\nbody\n");
        var (html, pdf) = EmailToPdf.Prepare(message, new EmailPdfOptions());
        Assert.Equal("(no subject)", pdf.Title);
        Assert.Contains("<title>(no subject)</title>", html);
        // PDF/UA needs a title; the subject or the label gives one.
        var bytes = EmailToPdf.Convert(message, new EmailPdfOptions { Pdf = new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 } });
        Assert.Contains("body", PdfText.Extract(bytes));
    }

    [Fact]
    public async Task Stream_file_and_async_overloads_agree()
    {
        var eml = Fixture.Bytes("mixed-apple.eml");
        var options = new EmailPdfOptions { Pdf = new PdfOptions { CreationDate = DateTimeOffset.UnixEpoch } };
        var expected = EmailToPdf.Convert(eml, options);

        using var output = new MemoryStream();
        EmailToPdf.Convert(new MemoryStream(eml), output, options);
        Assert.Equal(expected, output.ToArray());

        Assert.Equal(expected, await EmailToPdf.ConvertAsync(eml, options));
        using var asyncOutput = new MemoryStream();
        await EmailToPdf.ConvertAsync(new MemoryStream(eml), asyncOutput, options);
        Assert.Equal(expected, asyncOutput.ToArray());

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var pdfPath = Path.Combine(dir.FullName, "out.pdf");
            EmailToPdf.ConvertFile(Fixture.Path("mixed-apple.eml"), pdfPath, options);
            Assert.Equal(expected, File.ReadAllBytes(pdfPath));
            Assert.Equal("Photos", EmailMessage.Load(Fixture.Path("mixed-apple.eml")).Subject);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Cancellation_stops_a_conversion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => EmailToPdf.Convert(Fixture.Bytes("newsletter.eml"), null, cts.Token));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => EmailToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => EmailToPdf.Convert((EmailMessage)null!));
        Assert.Throws<ArgumentNullException>(() => EmailMessage.Parse((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => EmailMessage.Parse((Stream)null!));
    }
}
