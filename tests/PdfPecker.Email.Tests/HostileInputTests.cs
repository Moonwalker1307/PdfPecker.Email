using System.Diagnostics;
using System.Text;

namespace PdfPecker.Email.Tests;

/// <summary>Damaged and hostile messages: each must convert or fail with a documented exception, within a time bound.</summary>
public sealed class HostileInputTests
{
    private static void Bounded(byte[] message, int seconds = 20)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            EmailToPdf.Convert(message, new EmailPdfOptions { Pdf = new PdfOptions { MaxPages = 200 } });
        }
        catch (PdfLimitException)
        {
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), $"Took {watch.Elapsed}.");
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Empty_and_binary_messages_convert()
    {
        Bounded([]);
        Bounded(new byte[] { 0, 0xFF, 0x0D, 0x0A, 0x0D, 0x0A, 0x80 });
        var random = new Random(1);
        var noise = new byte[200_000];
        random.NextBytes(noise);
        Bounded(noise);
    }

    [Fact]
    public void Many_encoded_word_starts_stay_linear()
        => Bounded(Ascii("Subject: " + string.Concat(Enumerable.Repeat("=?utf-8?q?", 200_000)) + "\r\n\r\nx"));

    [Fact]
    public void Long_header_lines_and_many_headers_stay_linear()
    {
        Bounded(Ascii("Subject: " + new string('a', 2_000_000) + "\r\n\r\nx"));
        Bounded(Ascii(string.Concat(Enumerable.Repeat("X-A: b\r\n", 200_000)) + "\r\nx"));
        Bounded(Ascii("To: " + string.Concat(Enumerable.Repeat("a@b, ", 100_000)) + "\r\n\r\nx"));
        Bounded(Ascii("To: " + new string('(', 100_000) + "\r\n\r\nx"));
        Bounded(Ascii("Content-Type: text/plain; " + string.Concat(Enumerable.Repeat("a*1*=%41; ", 100_000)) + "\r\n\r\nx"));
    }

    [Fact]
    public void Many_boundary_like_lines_stay_linear()
    {
        var sb = new StringBuilder("Content-Type: multipart/mixed; boundary=b\r\n\r\n");
        for (var i = 0; i < 200_000; i++) sb.Append("--bx\r\n");
        Bounded(Ascii(sb.ToString()));
        Bounded(Ascii("Content-Type: multipart/mixed; boundary=b\r\n\r\n" + string.Concat(Enumerable.Repeat("--b\r\n", 9_000))));
    }

    [Fact]
    public void Deep_nesting_is_limited()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 1000; i++) sb.Append("Content-Type: multipart/mixed; boundary=b").Append(i).Append("\r\n\r\n--b").Append(i).Append("\r\n");
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(Ascii(sb.ToString())));
        var messages = string.Concat(Enumerable.Repeat("Content-Type: message/rfc822\r\nContent-Transfer-Encoding: base64\r\n\r\n", 3));
        Bounded(Ascii(messages));
    }

    [Fact]
    public void Hostile_html_stays_bounded()
    {
        Bounded(Ascii("Content-Type: text/html\r\n\r\n" + string.Concat(Enumerable.Repeat("<div><table><tr><td>", 20_000))));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n" + string.Concat(Enumerable.Repeat("<a href='", 50_000))));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n<style>" + string.Concat(Enumerable.Repeat("@media x{", 50_000)) + "</style>x"));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n<p style=\"" + string.Concat(Enumerable.Repeat("a{", 50_000)) + "\">x"));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n" + string.Concat(Enumerable.Repeat("<!--", 100_000))));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n" + string.Concat(Enumerable.Repeat("<script>", 100_000))));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n" + string.Concat(Enumerable.Repeat("</", 100_000))));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n" + string.Concat(Enumerable.Repeat("<img src=cid:x>", 20_000))));
        Bounded(Ascii("Content-Type: text/html\r\n\r\n<a href=\"https://x.example/" + new string('a', 1_000_000) + "\">" + string.Concat(Enumerable.Repeat("link text ", 50_000)) + "</a>"));
    }

    [Fact]
    public void Hostile_text_stays_bounded()
    {
        Bounded(Ascii("Content-Type: text/plain\r\n\r\n" + string.Concat(Enumerable.Repeat("http://", 100_000))));
        Bounded(Ascii("Content-Type: text/plain; format=flowed\r\n\r\n" + string.Concat(Enumerable.Repeat("> > a \r\n", 50_000))));
    }

    [Fact]
    public void Broken_images_and_attachments_convert()
    {
        Bounded(Ascii("Content-Type: multipart/related; boundary=r\r\n\r\n--r\r\nContent-Type: text/html\r\n\r\n<img src=\"cid:a\">\r\n--r\r\nContent-Type: image/png\r\nContent-ID: <a>\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAf\r\n--r--"));
        Bounded(Ascii("Content-Type: multipart/mixed; boundary=m\r\n\r\n--m\r\nContent-Type: image/jpeg\r\nContent-Disposition: inline\r\n\r\n\xFF\xD8garbage\r\n--m--"));
    }
}
