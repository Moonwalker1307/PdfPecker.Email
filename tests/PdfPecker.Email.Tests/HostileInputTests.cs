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

    [Fact]
    public void Link_starts_inside_words_stay_linear()
    {
        // Found by the parse fuzz target: every "http://" after a letter or a slash is not a link start, and each one
        // searched the rest of the text for the other prefixes.
        Bounded(Ascii("Content-Type: text/plain\r\n\r\nx" + string.Concat(Enumerable.Repeat("http://", 200_000))));
        Bounded(Ascii("Content-Type: text/plain\r\n\r\nhttp://example.com/" + new string(')', 1_000_000)));
    }

    [Fact]
    public void Rtf_ending_in_an_optional_destination_mark_converts()
    {
        // Found by the msg fuzz target: "{\*" at the very end of the RTF read past it.
        Assert.Equal("", PdfPecker.Email.Msg.RtfText.Read("{\\*"u8).Text);
        Assert.Equal("", PdfPecker.Email.Msg.RtfText.Read("{\\*\\"u8).Text);
        Bounded(new Msg.MsgObject().Binary(0x1009, Msg.MsgObject.RtfContainer("{\\rtf1 x{\\*", compressed: true)).ToMsg());
    }

    [Fact]
    public void Damaged_compound_files_convert()
    {
        var msg = Msg.MsgFixtures.Get("msg-html");
        // Every header field and directory entry byte set to 0xFF in turn, and the file cut at every sector.
        for (var at = 0x18; at < 0x200; at += 4) Bounded(Patch(msg, at, 0xFF, 4));
        for (var length = 512; length < msg.Length; length += 512) Bounded(msg[..length]);
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var copy = (byte[])msg.Clone();
            for (var k = 0; k < 16; k++) copy[512 + random.Next(copy.Length - 512)] = (byte)random.Next(256);
            Bounded(copy);
        }
    }

    [Fact]
    public void Compound_file_with_cycles_and_shared_chains_stays_bounded()
    {
        // A FAT that points every sector at sector 0, and every directory entry at the same huge stream.
        var msg = Msg.MsgFixtures.Get("msg-attached-message");
        var fatSector = BitConverter.ToInt32(msg, 0x4C);
        var cyclic = (byte[])msg.Clone();
        for (var k = 0; k < 128; k++) BitConverter.GetBytes(0).CopyTo(cyclic, 512 * (fatSector + 1) + 4 * k);
        Bounded(cyclic);
        var directory = BitConverter.ToInt32(msg, 0x30);
        var shared = (byte[])msg.Clone();
        for (var e = 1; e < 4; e++)
        {
            var at = 512 * (directory + 1) + 128 * e;
            BitConverter.GetBytes(0).CopyTo(shared, at + 0x74);
            BitConverter.GetBytes(long.MaxValue).CopyTo(shared, at + 0x78);
        }
        Bounded(shared);
    }

    [Fact]
    public void Deeply_attached_messages_stay_linear_in_size()
    {
        // Each level written as base64 would be a third larger than the one inside it: 1 MB under 40 levels would be
        // gigabytes. Written in place, the MIME stays about as large as the attachment.
        var data = new byte[1 << 20];
        new Random(9).NextBytes(data);
        var message = new Msg.MsgObject().String(0x1000, "bottom").Attach("data.bin", data);
        for (var i = 0; i < 40; i++) message = new Msg.MsgObject().String(0x1000, "level").AttachMessage("m", message);
        var (_, length) = PdfPecker.Email.Msg.OutlookMessage.ToMime(message.ToMsg(), EmailLimits.Default, CancellationToken.None);
        Assert.True(length < 2 * data.Length, $"{length} bytes of MIME.");
        Bounded(message.ToMsg());
    }

    private static byte[] Patch(byte[] data, int at, byte value, int length)
    {
        var copy = (byte[])data.Clone();
        copy.AsSpan(at, Math.Min(length, copy.Length - at)).Fill(value);
        return copy;
    }
}
