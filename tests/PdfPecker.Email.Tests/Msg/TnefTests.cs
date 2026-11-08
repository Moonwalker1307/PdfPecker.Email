using System.Text;
using PdfPecker.Email.Msg;

namespace PdfPecker.Email.Tests.Msg;

/// <summary>winmail.dat (TNEF): as an attachment of a MIME message, and on its own.</summary>
public sealed class TnefTests
{
    [Fact]
    public void Winmail_html_body_replaces_the_plain_text_and_its_attachments_come_out()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("tnef-outlook"));
        // The message's own headers stay.
        Assert.Equal("Quarterly report", message.Subject);
        Assert.Equal("sender@example.com", Assert.Single(message.From).Address);
        Assert.Contains("<p>Hello Ann, the report is attached.</p>", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain text that Outlook sends", Fixture.Html(message), StringComparison.Ordinal);
        // The logo is the HTML's image; the PDF keeps its long name; the attached message is an .eml; no winmail.dat.
        var names = message.Attachments.Select(a => a.FileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Original question.eml", "Quarterly report 2026.pdf"], names);
        var pdf = message.Attachments.Single(a => a.FileName.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal("application/pdf", pdf.MediaType);
        Assert.Equal(MsgFixtures.Sent, pdf.ModificationDate);
        var inner = EmailMessage.Parse(message.Attachments.Single(a => a.FileName.EndsWith(".eml", StringComparison.Ordinal)).Data.ToArray());
        Assert.Equal("Original question", inner.Subject);
        Assert.Equal("Could you send the figures?", inner.TextBody);
        Assert.Contains("<img src=\"cid:logo@example\">", Fixture.Html(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Winmail_without_html_keeps_the_message_text()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("tnef-plain"));
        Assert.Equal("The message's own text.", message.TextBody?.Trim());
        Assert.DoesNotContain("Rich text copy", Fixture.Html(message), StringComparison.Ordinal);
        Assert.Equal("notes.txt", Assert.Single(message.Attachments).FileName);
    }

    [Fact]
    public void Winmail_is_the_only_body_when_the_message_has_none()
    {
        var message = EmailMessage.Parse(MsgFixtures.WithWinmail("", MsgFixtures.PlainTnef()));
        Assert.Contains("Rich text copy", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Winmail_on_its_own_converts()
    {
        var tnef = MsgFixtures.OutlookTnef();
        var message = EmailMessage.Parse(tnef);
        Assert.Equal("Quarterly report", message.Subject);
        Assert.Contains("Hello Ann", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(2, message.Attachments.Count);
        var pdf = EmailToPdf.Convert(tnef, new EmailPdfOptions { EmbedOriginalMessage = true });
        var original = PdfAttachments.List(pdf).Single(a => a.FileName == "message.dat");
        Assert.Equal(tnef, original.Data);
    }

    [Fact]
    public void Whole_message_as_winmail_keeps_its_headers()
    {
        var eml = Encoding.ASCII.GetBytes("From: a@example.com\r\nSubject: Top level\r\nContent-Type: application/ms-tnef\r\nContent-Transfer-Encoding: base64\r\n\r\n"
            + Convert.ToBase64String(MsgFixtures.OutlookTnef()) + "\r\n");
        var message = EmailMessage.Parse(eml);
        Assert.Equal("Top level", message.Subject);
        Assert.Contains("Hello Ann", message.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(2, message.Attachments.Count);
    }

    [Fact]
    public void Damaged_winmail_stays_an_attachment()
    {
        var bytes = MsgFixtures.WithWinmail("text", "not tnef at all"u8.ToArray());
        var message = EmailMessage.Parse(bytes);
        Assert.Equal("winmail.dat", Assert.Single(message.Attachments).FileName);
        Assert.Equal("text", message.TextBody?.Trim());
    }

    [Fact]
    public void Cut_winmail_streams_read_as_far_as_they_go()
    {
        var tnef = MsgFixtures.OutlookTnef();
        for (var length = 6; length < tnef.Length; length += 7)
        {
            var message = EmailMessage.Parse(MsgFixtures.WithWinmail("text", tnef[..length]));
            _ = Fixture.Html(message);
            foreach (var a in message.Attachments) _ = a.Data.Length;
        }
    }

    [Fact]
    public void Attached_winmail_messages_respect_the_nesting_limit()
    {
        var tnef = new TnefBuilder().MessageProperties(new TnefBuilder.Properties().Unicode(0x1000, "bottom")).ToArray();
        for (var i = 0; i < 12; i++)
            tnef = new TnefBuilder().MessageProperties(new TnefBuilder.Properties().Unicode(0x1000, "level"))
                .Attachment("M", null, new TnefBuilder.Properties().Int(0x3705, 5).Message(0x3701, tnef)).ToArray();
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(tnef, new EmailLimits { MaxNestingDepth = 5 }));
        EmailMessage.Parse(tnef);
    }

    [Fact]
    public void Property_lists_with_huge_counts_stop_at_the_end_of_the_data()
    {
        var props = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x03, 0x00, 0x05, 0x37 };
        var tnef = new TnefBuilder().Attribute(1, 0x9003, 6, props).Attribute(1, 0x9004, 6, [0xFF, 0xFF, 0xFF, 0x7F]).ToArray();
        Assert.NotNull(TnefObject.Read(tnef, CancellationToken.None));
    }
}
