using System.Text;
using PdfPecker.Email.Mime;

namespace PdfPecker.Email.Tests;

public sealed class MimeTests
{
    [Fact]
    public void Encoded_words_are_decoded_and_joined()
    {
        Assert.Equal("Møde om budget for næste år", HeaderValues.DecodeWords("=?UTF-8?B?TcO4ZGUgb20gYnVkZ2V0?= =?UTF-8?Q?_for_n=C3=A6ste_=C3=A5r?="));
        // A multi-byte character split across two words.
        Assert.Equal("ø", HeaderValues.DecodeWords("=?utf-8?Q?=C3?= =?utf-8?Q?=B8?="));
        Assert.Equal("caf\u00e9 opening", HeaderValues.DecodeWords("=?iso-8859-1?q?caf=E9?= opening"));
        Assert.Equal("a =?bad b", HeaderValues.DecodeWords("a =?bad b"));
        Assert.Equal("x =?utf-8?X?abc?= y", HeaderValues.DecodeWords("x =?utf-8?X?abc?= y"));
    }

    [Fact]
    public void Parameters_handle_quotes_rfc2231_and_encoded_words()
    {
        var p = HeaderValues.ParseParameters("; name=\"a \\\"b\\\".pdf\"; charset=UTF-8 ; x=unquoted value");
        Assert.Equal("a \"b\".pdf", p["name"]);
        Assert.Equal("UTF-8", p["charset"]);
        Assert.Equal("unquoted value", p["x"]);

        p = HeaderValues.ParseParameters("; filename*0*=utf-8''%C3%85rs; filename*1=\" rapport.pdf\"; filename=fallback.pdf");
        Assert.Equal("Års rapport.pdf", p["filename"]);

        p = HeaderValues.ParseParameters("; filename*=iso-8859-1'da'%E6bler.txt");
        Assert.Equal("æbler.txt", p["filename"]);

        p = HeaderValues.ParseParameters("; filename=\"=?utf-8?B?bsO4dGVyLnR4dA==?=\"");
        Assert.Equal("nøter.txt", p["filename"]);
    }

    [Fact]
    public void Addresses_are_read_with_names_comments_and_groups()
    {
        var list = HeaderValues.ParseAddresses("\"Example, Ann\" <ann@example.com>, bob@example.com (Bob Example), team: c@example.com, d@example.com;, =?utf-8?Q?S=C3=B8ren?= <s@example.dk>");
        Assert.Equal(5, list.Count);
        Assert.Equal("Example, Ann", list[0].Name);
        Assert.Equal("ann@example.com", list[0].Address);
        Assert.Equal("Bob Example", list[1].Name);
        Assert.Equal("bob@example.com", list[1].Address);
        Assert.Equal("c@example.com", list[2].Address);
        Assert.Null(list[2].Name);
        Assert.Equal("d@example.com", list[3].Address);
        Assert.Equal("Søren <s@example.dk>", list[4].ToString());
        Assert.Empty(HeaderValues.ParseAddresses("Undisclosed recipients:;"));
        Assert.Equal("c@d", HeaderValues.ParseAddresses("<@a.example,@b.example:c@d>")[0].Address);
    }

    [Theory]
    [InlineData("Sun, 8 Nov 2026 18:04:34 +0100 (CET)", "2026-11-08T18:04:34+01:00")]
    [InlineData("8 Nov 26 18:04 EST", "2026-11-08T18:04:00-05:00")]
    [InlineData("Tue,  3 Nov 2026 07:30:00 -0500", "2026-11-03T07:30:00-05:00")]
    [InlineData("Nov 8 2026 1:02:03 GMT", "2026-11-08T01:02:03+00:00")]
    public void Dates_are_read(string header, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), HeaderValues.ParseDate(header));

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("31 Feb 2026 10:00 +0000")]
    [InlineData("99 Nov 2026")]
    public void Unreadable_dates_are_null(string header) => Assert.Null(HeaderValues.ParseDate(header));

    [Fact]
    public void Base64_skips_noise_and_continues_after_padding()
    {
        Assert.Equal("hello", Encoding.ASCII.GetString(TransferEncoding.DecodeBase64("aGVs\r\nbG8=\r\n"u8)));
        Assert.Equal("hiyo", Encoding.ASCII.GetString(TransferEncoding.DecodeBase64("aGk=eW8="u8)));
        Assert.Equal("hello", Encoding.ASCII.GetString(TransferEncoding.DecodeBase64("a G**V\tsbG8"u8)));
        Assert.Empty(TransferEncoding.DecodeBase64("a"u8));
    }

    [Fact]
    public void Quoted_printable_handles_soft_breaks_and_broken_escapes()
    {
        Assert.Equal("caf\u00e9 au lait", Encoding.UTF8.GetString(TransferEncoding.DecodeQuotedPrintable("caf=C3=A9 au=  \r\n lait"u8, header: false)));
        Assert.Equal("a=zz", Encoding.ASCII.GetString(TransferEncoding.DecodeQuotedPrintable("a=zz="u8, header: false)));
        Assert.Equal("a b", Encoding.ASCII.GetString(TransferEncoding.DecodeQuotedPrintable("a_b"u8, header: true)));
    }

    [Theory]
    [InlineData("iso8859-15", 28605)]
    [InlineData("ISO_8859-2", 28592)]
    [InlineData("\"windows-1250\"", 1250)]
    [InlineData("cp1251", 1251)]
    [InlineData("ks_c_5601-1987", 949)]
    [InlineData("gb2312", 54936)]
    [InlineData("iso-8859-1", 1252)]
    [InlineData("utf-8", 65001)]
    public void Charset_names_are_normalised(string name, int codePage) => Assert.Equal(codePage, Charsets.Find(name)!.CodePage);

    [Fact]
    public void Unknown_charsets_fall_back_to_utf8_or_windows1252()
    {
        Assert.Null(Charsets.Find("x-unknown"));
        Assert.Equal("é", Charsets.Decode("\u00c3\u00a9"u8.Length == 4 ? new byte[] { 0xC3, 0xA9 } : [], "x-unknown"));
        Assert.Equal("\u201chi\u201d", Charsets.Decode([0x93, (byte)'h', (byte)'i', 0x94], null));
        // An ASCII label on UTF-8 text.
        Assert.Equal("æ", Charsets.Decode([0xC3, 0xA6], "us-ascii"));
    }

    [Fact]
    public void Multipart_boundaries_must_end_their_line()
    {
        // The inner boundary starts with the outer one; only whole delimiter lines split.
        var m = Fixture.FromText("""
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: multipart/alternative; boundary="bb"

            --bb
            Content-Type: text/plain

            inner text
            --bb--
            --b
            Content-Type: application/pdf
            Content-Disposition: attachment; filename=a.pdf

            %PDF
            --b--
            """);
        Assert.Equal("inner text", m.TextBody);
        Assert.Single(m.Attachments);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(m.Attachments[0].Data));
    }

    [Fact]
    public void Missing_closing_boundary_keeps_the_last_part()
    {
        var m = Fixture.Message("truncated.eml");
        Assert.Contains("Broken but readable", m.HtmlBody);
        Assert.Single(m.Attachments);
        Assert.Equal("cut.png", m.Attachments[0].FileName);
        Assert.Equal(18, m.Attachments[0].Size);
    }

    [Fact]
    public void A_part_without_headers_and_an_mbox_line_are_read()
    {
        var m = Fixture.FromText("""
            From sender@example.com Sun Nov  8 18:00:00 2026
            Subject: mbox
            Content-Type: multipart/mixed; boundary=x

            --x
            just text, no headers
            --x--
            """);
        Assert.Equal("mbox", m.Subject);
        Assert.Equal("just text, no headers", m.TextBody);
    }

    [Fact]
    public void Multipart_without_boundary_is_shown_as_text()
    {
        var m = Fixture.FromText("Content-Type: multipart/mixed\n\nsome text\n");
        Assert.Equal("some text\r\n", m.TextBody);
    }

    [Fact]
    public void Limits_fail_with_PdfLimitException()
    {
        var sb = new StringBuilder("Content-Type: multipart/mixed; boundary=x\r\n\r\n");
        for (var i = 0; i < 20; i++) sb.Append("--x\r\n\r\npart\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(bytes, new EmailLimits { MaxParts = 10 }));
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(bytes, new EmailLimits { MaxMessageBytes = 100 }));

        var nested = new StringBuilder();
        for (var i = 0; i < 30; i++) nested.Append("Content-Type: message/rfc822\r\n\r\n");
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(Encoding.ASCII.GetBytes(nested.ToString()), new EmailLimits { MaxNestingDepth = 20 }));
        Assert.Throws<PdfLimitException>(() => EmailMessage.Parse(new MemoryStream(bytes), new EmailLimits { MaxMessageBytes = 100 }));
    }
}
