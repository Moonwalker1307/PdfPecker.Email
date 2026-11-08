using System.Text;
using PdfPecker.Email.Msg;

namespace PdfPecker.Email.Tests.Msg;

/// <summary>Outlook .msg files: the compound file, the properties, compressed RTF and the MIME written for them.</summary>
public sealed class MsgTests
{
    [Fact]
    public void Html_message_reads_headers_body_and_attachments()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("msg-html"));
        Assert.Equal("Quarterly report – März", message.Subject);
        Assert.Equal(("Example Sender", "sender@example.com"), Pair(Assert.Single(message.From)));
        Assert.Empty(message.Sender);
        Assert.Equal("ann@example.org", Assert.Single(message.To).Address);
        Assert.Equal(("Doe, John", "john@example.org"), Pair(Assert.Single(message.Cc)));
        Assert.Equal("archive@example.net", Assert.Single(message.Bcc).Address);
        Assert.Equal("reports@example.com", Assert.Single(message.ReplyTo).Address);
        Assert.Equal(MsgFixtures.Sent, message.Date);
        Assert.Equal("report-2026-03@example.com", message.MessageId);
        Assert.Equal("de-DE", message.Language);
        Assert.Contains("<b>attached</b>", message.HtmlBody, StringComparison.Ordinal);
        Assert.StartsWith("Hello Ann,", message.TextBody, StringComparison.Ordinal);
        // The logo is the HTML's image, so only the PDF is an attachment, typed from its name.
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("report.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.MediaType);
        Assert.Equal("%PDF-1.4\n%%EOF\n"u8.ToArray(), attachment.Data.ToArray());
    }

    [Fact]
    public void Html_message_shows_the_inline_image_and_drops_scripts()
    {
        var html = Fixture.Html(EmailMessage.Parse(MsgFixtures.Get("msg-html")));
        Assert.Contains("<img src=\"cid:logo@example\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Encapsulated_html_comes_out_of_compressed_rtf()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("msg-rtf-html"));
        Assert.Null(message.TextBody);
        Assert.Contains("<p>Café €5 {ok}</p>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<div class=WordSection1>", message.HtmlBody, StringComparison.Ordinal);
        // Word's named page would start a new PDF page after the header.
        Assert.DoesNotContain("page:", Fixture.Html(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Rich_text_becomes_plain_text()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("msg-rtf-plain"));
        Assert.Null(message.HtmlBody);
        Assert.Equal("First line\nSecond\tline “quoted”\nexample.com\n", message.TextBody);
    }

    [Fact]
    public void Attached_message_becomes_a_message_part()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("msg-attached-message"));
        Assert.Equal("See the message below.", message.TextBody);
        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("Original question.eml", attachment.FileName);
        Assert.Equal("message/rfc822", attachment.MediaType);
        var inner = EmailMessage.Parse(attachment.Data.ToArray());
        Assert.Equal("Original question", inner.Subject);
        Assert.Equal("ann@example.org", Assert.Single(inner.From).Address);
        Assert.Equal(MsgFixtures.Sent.AddDays(-1), inner.Date);
        Assert.Equal("figures.csv", Assert.Single(inner.Attachments).FileName);
    }

    [Fact]
    public void Ansi_strings_use_the_message_code_page_and_exchange_addresses_are_dropped()
    {
        var message = EmailMessage.Parse(MsgFixtures.Get("msg-ansi"));
        Assert.Equal("Привет", message.Subject);
        var from = Assert.Single(message.From);
        Assert.Equal("Отправитель", from.Name);
        Assert.Equal("", from.Address);
        Assert.Equal("Текст письма", message.TextBody);
        Assert.Null(message.Date);
    }

    [Fact]
    public void Sent_on_behalf_writes_from_and_sender()
    {
        var msg = new MsgObject()
            .String(0x0C1A, "Assistant").String(0x5D01, "assistant@example.com")
            .String(0x0042, "Manager").String(0x5D02, "manager@example.com")
            .String(0x1000, "x").ToMsg();
        var message = EmailMessage.Parse(msg);
        Assert.Equal("manager@example.com", Assert.Single(message.From).Address);
        Assert.Equal("assistant@example.com", Assert.Single(message.Sender).Address);
    }

    [Fact]
    public void Header_values_cannot_inject_headers_or_parts()
    {
        var msg = new MsgObject()
            .String(0x0037, "Hi\r\nBcc: victim@example.com\r\n\r\n--=_pdfpecker-msg-0m\r\nContent-Type: text/html")
            .String(0x0C1A, "Name\r\nX: y").String(0x5D01, "a@example.com>\r\nTo: <b@example.com")
            .String(0x1000, "body")
            .Attach("a\r\nContent-Type: text/html\r\n.txt", "x"u8.ToArray(), "text/plain\r\nX-Evil: 1", "id>\r\nX: 1")
            .ToMsg();
        var message = EmailMessage.Parse(msg);
        Assert.Empty(message.Bcc);
        Assert.Null(message.GetHeader("X"));
        Assert.StartsWith("Hi  Bcc: victim@example.com", message.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', message.Subject!);
        Assert.Equal("body", message.TextBody);
        Assert.Equal("", Assert.Single(message.From).Address);
        Assert.Single(message.Attachments);
    }

    [Fact]
    public void Original_msg_is_embedded_as_msg()
    {
        var msg = MsgFixtures.Get("msg-rtf-plain");
        var pdf = EmailToPdf.Convert(msg, new EmailPdfOptions { EmbedOriginalMessage = true });
        var embedded = Assert.Single(PdfAttachments.List(pdf));
        Assert.Equal("message.msg", embedded.FileName);
        Assert.Equal(msg, embedded.Data);
    }

    [Fact]
    public void Msg_converts_to_pdf_a3_with_attachments()
    {
        var pdf = EmailToPdf.Convert(MsgFixtures.Get("msg-html"), new EmailPdfOptions { Pdf = new PdfOptions { Conformance = PdfConformance.PdfA3b } });
        Assert.Equal("report.pdf", Assert.Single(PdfAttachments.List(pdf)).FileName);
        Assert.Contains("Hello Ann", PdfText.Extract(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Size_limit_applies_to_the_msg_file()
    {
        var msg = MsgFixtures.Get("msg-html");
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(msg, new EmailLimits { MaxMessageBytes = msg.Length - 1 }));
        EmailMessage.Parse(msg, new EmailLimits { MaxMessageBytes = msg.Length });
    }

    [Fact]
    public void Parts_limit_applies_to_attachments()
    {
        var builder = new MsgObject().String(0x1000, "x");
        for (var i = 0; i < 20; i++) builder.Attach($"f{i}.txt", [1]);
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(builder.ToMsg(), new EmailLimits { MaxParts = 10 }));
    }

    [Fact]
    public void Nesting_limit_applies_to_attached_messages()
    {
        var message = new MsgObject().String(0x1000, "innermost");
        for (var i = 0; i < 10; i++) message = new MsgObject().String(0x1000, "level").AttachMessage("m", message);
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(message.ToMsg(), new EmailLimits { MaxNestingDepth = 5 }));
        EmailMessage.Parse(message.ToMsg());
    }

    [Fact]
    public void Large_streams_use_regular_sectors()
    {
        var data = new byte[100_000];
        new Random(3).NextBytes(data);
        var message = EmailMessage.Parse(new MsgObject().String(0x1000, new string('x', 5000)).Attach("big.bin", data).ToMsg());
        Assert.Equal(5000, message.TextBody!.Length);
        Assert.Equal(data, Assert.Single(message.Attachments).Data.ToArray());
    }

    [Fact]
    public void Files_larger_than_the_header_fat_list_read_through_the_difat()
    {
        // 109 FAT sectors in the header cover about 7 MB; this file needs DIFAT sectors.
        var data = new byte[9_000_000];
        new Random(5).NextBytes(data);
        var msg = new MsgObject().String(0x1000, "big").Attach("big.bin", data).ToMsg();
        Assert.NotEqual(-2, BitConverter.ToInt32(msg, 0x44));
        Assert.Equal(data, Assert.Single(EmailMessage.Parse(msg).Attachments).Data.ToArray());
    }

    [Theory]
    [InlineData("2d0000002b0000004c5a4675f1c5c7a703000a007263706731323542320af32068656c09002062770 5b06c647d0a800fa0", "{\\rtf1\\ansi\\ansicpg1252\\pard hello world}\r\n")]
    [InlineData("1a0000001c0000004c5a4675e2d44b51410004205758595a0d6e7d010eb0", "{\\rtf1 WXYZWXYZWXYZWXYZWXYZ}")]
    public void Compressed_rtf_decodes_the_specification_examples(string hex, string expected)
        => Assert.Equal(expected, Encoding.ASCII.GetString(CompressedRtf.Decompress(Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal)))));

    [Fact]
    public void Compressed_rtf_output_is_bounded_by_its_input()
    {
        // A header that claims 2 GB, and tokens that each repeat 17 bytes.
        var data = new byte[16 + 9 * 1000];
        BitConverter.GetBytes(int.MaxValue).CopyTo(data, 4);
        BitConverter.GetBytes(0x75465A4C).CopyTo(data, 8);
        for (var i = 16; i + 9 <= data.Length; i += 9)
        {
            data[i] = 0xFF;
            for (var k = 0; k < 8; k++) data[i + 1 + k * 1] = (byte)(k % 2 == 0 ? 0x00 : 0x0F);
        }
        Assert.True(CompressedRtf.Decompress(data).Length <= 9 * data.Length);
    }

    private static (string?, string) Pair(EmailAddress address) => (address.Name, address.Address);

    [Fact]
    public void Not_a_compound_file_is_read_as_mime()
    {
        var message = Fixture.FromText("Subject: plain\r\n\r\nbody");
        Assert.Equal("plain", message.Subject);
    }
}
