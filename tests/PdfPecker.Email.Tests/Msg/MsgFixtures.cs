using System.Text;

namespace PdfPecker.Email.Tests.Msg;

/// <summary>
/// The Outlook messages the tests use, written by <see cref="MsgObject"/>: real .msg files from mail clients hold real
/// people's names and addresses, so these are made from neutral data instead. GoldenTests compares their HTML too.
/// </summary>
internal static class MsgFixtures
{
    internal static readonly DateTimeOffset Sent = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    internal static IReadOnlyList<string> Names { get; } = ["msg-html", "msg-rtf-html", "msg-rtf-plain", "msg-attached-message", "msg-ansi", "tnef-outlook", "tnef-plain"];

    internal static byte[] Get(string name) => name switch
    {
        "msg-html" => Html().ToMsg(),
        "msg-rtf-html" => RtfHtml().ToMsg(),
        "msg-rtf-plain" => RtfPlain().ToMsg(),
        "msg-attached-message" => AttachedMessage().ToMsg(),
        "msg-ansi" => Ansi().ToMsg(),
        "tnef-outlook" => WithWinmail("Plain text that Outlook sends next to the winmail.dat.", OutlookTnef()),
        "tnef-plain" => WithWinmail("The message's own text.", PlainTnef()),
        _ => throw new ArgumentException("No such fixture.", nameof(name)),
    };

    /// <summary>An HTML message with an inline image, a PDF attachment and three kinds of recipient.</summary>
    internal static MsgObject Html() => new MsgObject()
        .String(0x0037, "Quarterly report – März")
        .String(0x0C1A, "Example Sender").String(0x5D01, "sender@example.com")
        .String(0x0042, "Example Sender").String(0x5D02, "sender@example.com")
        .Recipient("Ann Example", "ann@example.org")
        .Recipient("Doe, John", "john@example.org", 2)
        .Recipient("Archive", "archive@example.net", 3)
        .Time(0x0039, Sent)
        .String(0x1035, "<report-2026-03@example.com>")
        .String(0x007D, "Received: from mail.example.com\r\nReply-To: Reports <reports@example.com>\r\nContent-Language: de-DE\r\n\r\n")
        .String(0x1000, "Hello Ann,\r\n\r\nthe report is attached.\r\n")
        .Binary(0x1013, Encoding.UTF8.GetBytes("<html><body><p>Hello Ann,</p><p>the report is <b>attached</b>.</p><img src=\"cid:logo@example\"><script>alert(1)</script></body></html>"))
        .Int(0x3FDE, 65001)
        .Attach("logo.png", Fixture.Png, "image/png", "logo@example")
        .Attach("report.pdf", "%PDF-1.4\n%%EOF\n"u8.ToArray());

    /// <summary>HTML that Outlook keeps only as compressed RTF, with Word's named page in its style sheet.</summary>
    internal static MsgObject RtfHtml() => new MsgObject()
        .String(0x0037, "Encapsulated HTML")
        .String(0x0C1A, "Example Sender").String(0x0C1F, "sender@example.com")
        .Recipient("Ann Example", "ann@example.org")
        .Time(0x0039, Sent)
        .Binary(0x1009, MsgObject.RtfContainer(
            "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 \\deff0{\\fonttbl{\\f0\\fswiss Arial;}}\r\n" +
            "{\\*\\htmltag19 <html>}{\\*\\htmltag34 <head>}{\\*\\htmltag241 <style>div.WordSection1 \\{page:WordSection1;\\}</style>}" +
            "{\\*\\htmltag41 </head>}{\\*\\htmltag50 <body>}{\\*\\htmltag96 <div class=WordSection1>}{\\*\\htmltag64 <p>}" +
            "\\htmlrtf {\\htmlrtf0 Caf\\'e9 \\u8364?5 \\{ok\\}\\htmlrtf }\\htmlrtf0 {\\*\\htmltag72 </p>}\\htmlrtf \\par \\htmlrtf0 " +
            "{\\*\\htmltag96 </div>}{\\*\\htmltag58 </body>}{\\*\\htmltag27 </html>}}", compressed: true));

    /// <summary>A rich text message without HTML or a plain text body.</summary>
    internal static MsgObject RtfPlain() => new MsgObject()
        .String(0x0037, "Rich text")
        .String(0x0C1A, "Example Sender").String(0x0C1F, "sender@example.com")
        .Recipient("Ann Example", "ann@example.org")
        .Time(0x0039, Sent)
        .Binary(0x1009, MsgObject.RtfContainer(
            "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0 Calibri;}}{\\colortbl;\\red0\\green0\\blue0;}" +
            "{\\*\\generator Example;}\\pard First line\\par Second\\tab line \\ldblquote quoted\\rdblquote\\par " +
            "{\\field{\\*\\fldinst HYPERLINK \"https://example.com\"}{\\fldrslt example.com}}\\par }", compressed: false));

    /// <summary>A forward: a short HTML message with the original message attached.</summary>
    internal static MsgObject AttachedMessage()
    {
        var original = new MsgObject()
            .String(0x0037, "Original question")
            .String(0x0C1A, "Ann Example").String(0x5D01, "ann@example.org")
            .Recipient("Example Sender", "sender@example.com")
            .Time(0x0039, Sent.AddDays(-1))
            .String(0x1000, "Could you send the figures?")
            .Attach("figures.csv", "a,b\r\n1,2\r\n"u8.ToArray(), "text/csv");
        return new MsgObject()
            .String(0x0037, "FW: Original question")
            .String(0x0C1A, "Example Sender").String(0x5D01, "sender@example.com")
            .Recipient("Doe, John", "john@example.org")
            .Time(0x0039, Sent)
            .String(0x1000, "See the message below.")
            .AttachMessage("Original question", original);
    }

    /// <summary>A message from an older Outlook with 8-bit strings in its code page.</summary>
    internal static MsgObject Ansi()
    {
        var cyrillic = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;
        return new MsgObject()
            .Int(0x3FFD, 1251)
            .String(0x0037, "Привет", MsgObject.Ansi, cyrillic)
            .String(0x0C1A, "Отправитель", MsgObject.Ansi, cyrillic).String(0x0C1F, "/O=EXAMPLE/OU=EXCHANGE/CN=RECIPIENTS/CN=SENDER", MsgObject.Ansi)
            .Recipient("Ann Example", "ann@example.org")
            .String(0x1000, "Текст письма", MsgObject.Ansi, cyrillic);
    }

    /// <summary>
    /// What Outlook sends in its own format: a plain text body and a winmail.dat holding the HTML body (in compressed
    /// RTF), an inline image, a PDF with a long name and an attached message.
    /// </summary>
    internal static byte[] OutlookTnef()
    {
        var attached = new TnefBuilder().Subject("Original question")
            .MessageProperties(new TnefBuilder.Properties().Unicode(0x0C1A, "Ann Example").Unicode(0x5D01, "ann@example.org").Unicode(0x1000, "Could you send the figures?").Time(0x0039, Sent.AddDays(-1)))
            .ToArray();
        return new TnefBuilder()
            .Subject("WINMAIL")
            .MessageProperties(new TnefBuilder.Properties()
                .Unicode(0x0037, "Quarterly report")
                .Named("Keywords", "ignored")
                .Int(0x3FDE, 1252)
                .Binary(0x1009, MsgObject.RtfContainer(
                    "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 {\\*\\htmltag19 <html><body>}{\\*\\htmltag64 <p>}Hello Ann, the report is attached.{\\*\\htmltag72 </p>}" +
                    "{\\*\\htmltag84 <img src=\"cid:logo@example\">}{\\*\\htmltag27 </body></html>}}", compressed: true)))
            .Attachment("LOGO.PNG", Fixture.Png, new TnefBuilder.Properties().Int(0x3705, 1).Unicode(0x3707, "logo.png").Unicode(0x370E, "image/png").Unicode(0x3712, "logo@example"))
            .Attachment("REPORT~1.PDF", "%PDF-1.4\n%%EOF\n"u8.ToArray(), new TnefBuilder.Properties().Int(0x3705, 1).Unicode(0x3707, "Quarterly report 2026.pdf").Time(0x3008, Sent))
            .Attachment("Original", null, new TnefBuilder.Properties().Int(0x3705, 5).Unicode(0x3001, "Original question").Message(0x3701, attached))
            .ToArray();
    }

    /// <summary>A winmail.dat with only a rich text body, which does not replace the message's own text.</summary>
    internal static byte[] PlainTnef() => new TnefBuilder()
        .MessageProperties(new TnefBuilder.Properties().Binary(0x1009, MsgObject.RtfContainer("{\\rtf1\\ansi Rich text copy\\par }", compressed: false)))
        .Attachment("NOTES.TXT", "notes"u8.ToArray(), new TnefBuilder.Properties().Int(0x3705, 1).Unicode(0x3707, "notes.txt"))
        .ToArray();

    /// <summary>An .eml with a plain text body and a winmail.dat attachment.</summary>
    internal static byte[] WithWinmail(string text, byte[] tnef) => Encoding.ASCII.GetBytes(
        "From: Example Sender <sender@example.com>\r\nTo: Ann Example <ann@example.org>\r\nSubject: Quarterly report\r\n" +
        "Date: Sat, 14 Mar 2026 09:26:53 +0000\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n" +
        "--b\r\nContent-Type: text/plain; charset=us-ascii\r\n\r\n" + text + "\r\n" +
        "--b\r\nContent-Type: application/ms-tnef; name=\"winmail.dat\"\r\nContent-Disposition: attachment; filename=\"winmail.dat\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(tnef, Base64FormattingOptions.InsertLineBreaks) + "\r\n--b--\r\n");
}
