using System.Text;

namespace PdfPecker.Email.Tests;

public sealed class MessageTests
{
    [Fact]
    public void Headers_are_decoded()
    {
        var m = Fixture.Message("plain-flowed.eml");
        Assert.Equal("Møde om budget for næste år", m.Subject);
        Assert.Equal("Søren Æblegaard", m.From[0].Name);
        Assert.Equal(2, m.To.Count);
        Assert.Empty(m.Cc);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 18, 4, 34, TimeSpan.FromHours(1)), m.Date);
        Assert.Equal("plain-1@example.dk", m.MessageId);
        Assert.Equal("da", m.Language);
        Assert.Equal("1.0", m.GetHeader("mime-version"));
        Assert.Null(m.HtmlBody);
        Assert.StartsWith("Hej Ann,", m.TextBody);
    }

    [Fact]
    public void Newsletter_body_images_and_attachments_are_sorted()
    {
        var m = Fixture.Message("newsletter.eml");
        Assert.Equal("Plain version of the newsletter.\r\n", m.TextBody);
        Assert.Contains("Caf\u00e9 opening", m.HtmlBody);
        // The logo and dot.png are shown by the HTML; the unused related image and the PDF are attachments.
        Assert.Equal(["Programme.pdf", "unused.png"], m.Attachments.Select(a => a.FileName));
        var pdf = m.Attachments[0];
        Assert.Equal("application/pdf", pdf.MediaType);
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(pdf.Data));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.FromHours(1)), pdf.CreationDate);
        Assert.Equal("unused@example.org", m.Attachments[1].ContentId);
    }

    [Fact]
    public void Attached_messages_and_names_are_handled()
    {
        var m = Fixture.Message("forwarded.eml");
        Assert.Equal("See the forwarded message \u201cbelow\u201d \u2013 thanks.\r\n", m.TextBody);
        Assert.Equal(["Contract \u2013 signed.eml", "\u00c5rsrapport 2026 - final.txt", "n\u00f8ter.txt", "attachment-4.pdf"], m.Attachments.Select(a => a.FileName));
        Assert.Equal("message/rfc822", m.Attachments[0].MediaType);
    }

    [Fact]
    public void Inline_images_between_text_parts_are_body()
    {
        var m = Fixture.Message("mixed-apple.eml");
        Assert.Empty(m.Attachments);
        var html = Fixture.Html(m);
        Assert.Contains("Here is the first photo:", html);
        Assert.Contains("<img src=\"pdfpecker-part:0\"", html);
        Assert.Contains("And that&#39;s all.".Replace("&#39;", "'"), html);
    }

    [Fact]
    public void Plain_text_preference_shows_the_text_alternative()
    {
        var m = Fixture.Message("newsletter.eml");
        var html = Fixture.Html(m, new EmailPdfOptions { PreferPlainText = true });
        Assert.Contains("Plain version of the newsletter.", html);
        Assert.DoesNotContain("Our new caf", html);
    }

    [Fact]
    public void File_names_are_made_safe_and_unique()
    {
        var m = Fixture.FromText("""
            Content-Type: multipart/mixed; boundary=x

            --x
            Content-Disposition: attachment; filename=C:\Users\x\re:port?.pdf

            a
            --x
            Content-Disposition: attachment; filename="report_.pdf"

            b
            --x
            Content-Disposition: attachment; filename="REPORT_.pdf"

            c
            --x
            Content-Disposition: attachment; filename="..."

            d
            --x--
            """);
        Assert.Equal(["re_port_.pdf", "report_.pdf", "REPORT_ (2).pdf", "attachment-4.txt"], m.Attachments.Select(a => a.FileName));
    }

    [Fact]
    public void Signed_messages_show_the_content_and_keep_the_signature()
    {
        var m = Fixture.FromText("""
            Content-Type: multipart/signed; protocol="application/pkcs7-signature"; boundary=s

            --s
            Content-Type: text/plain

            signed text
            --s
            Content-Type: application/pkcs7-signature; name=smime.p7s

            MIIB
            --s--
            """);
        Assert.Equal("signed text", m.TextBody);
        Assert.Equal("smime.p7s", Assert.Single(m.Attachments).FileName);
    }

    [Fact]
    public void Calendar_alternatives_are_kept_as_files()
    {
        var m = Fixture.FromText("""
            Content-Type: multipart/alternative; boundary=a

            --a
            Content-Type: text/plain

            Meeting
            --a
            Content-Type: text/calendar; method=REQUEST

            BEGIN:VCALENDAR
            --a
            Content-Type: text/html

            <p>Meeting</p>
            --a--
            """);
        Assert.Equal("attachment-1.ics", Assert.Single(m.Attachments).FileName);
    }

    [Fact]
    public void Bidi_overrides_cannot_disguise_file_names_or_headers()
    {
        // U+202E turns the rest of a line around: "invoice\u202Efdp.exe" shows as "invoiceexe.pdf".
        var message = Fixture.FromText(
            "From: =?utf-8?B?" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Bank \u202Eknab")) + "?= <a@example.com\u202E>\r\n" +
            "Subject: =?utf-8?B?" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Pay \u2066now\u2069 \u05E9\u05DC\u05D5\u05DD")) + "?=\r\n" +
            "Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nx\r\n" +
            "--b\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename*=utf-8''invoice%E2%80%AEfdp.exe\r\n\r\nMZ\r\n--b--\r\n");
        Assert.Equal("invoicefdp.exe", Assert.Single(message.Attachments).FileName);
        Assert.Equal("Bank knab", message.From[0].Name);
        Assert.Equal("a@example.com", message.From[0].Address);
        // Hebrew keeps its letters; only the isolate controls go.
        Assert.Equal("Pay now \u05E9\u05DC\u05D5\u05DD", message.Subject);
        Assert.DoesNotContain('\u202E', Fixture.Html(message));
    }

    [Fact]
    public void Direction_marks_stay_in_headers_but_not_in_file_names()
    {
        Assert.Equal("a\u200Fb", PdfPecker.Email.Mime.BidiControls.Remove("a\u200Fb"));
        Assert.Equal("ab.pdf", FileNames.Clean("a\u200Fb.pdf"));
        Assert.Equal("plain", PdfPecker.Email.Mime.BidiControls.Remove("plain"));
    }
}
