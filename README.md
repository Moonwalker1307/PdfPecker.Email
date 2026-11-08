![PdfPecker](https://raw.githubusercontent.com/Moonwalker1307/PdfPecker/main/assets/logo.png)

# PdfPecker.Email

E-mail to PDF for .NET: convert `.eml` messages (MIME) and Outlook `.msg` files to PDF and PDF/A-3, with the header
block, the body as a mail client shows it, its inline images, and the attachments embedded in the PDF. It uses
[PdfPecker](https://www.nuget.org/packages/PdfPecker) for the PDF and has no other dependency: its own MIME parser,
Outlook reader, header decoders, HTML sanitiser and CSS filter, on the .NET base class library.

```csharp
using PdfPecker;
using PdfPecker.Email;

// An .eml file to a PDF, with the attachments embedded
EmailToPdf.ConvertFile("message.eml", "message.pdf");

// An Outlook .msg file is read the same way: the format is recognised from the content
EmailToPdf.ConvertFile("message.msg", "message.pdf");

// PDF/A-3u for archiving, keeping the original message inside the PDF as well
byte[] pdf = EmailToPdf.Convert(File.ReadAllBytes("message.eml"), new EmailPdfOptions
{
    EmbedOriginalMessage = true,
    Pdf = new PdfOptions { Conformance = PdfConformance.PdfA3u },
});
```

## Install

```
dotnet add package PdfPecker.Email
```

The package targets `net8.0`, `net9.0` and `net10.0`, is trimmable and Native AOT compatible, and depends only on
PdfPecker.

## What it does

- **MIME, as mail clients read it.** Multipart mixed, alternative, related, signed, digest and report; base64 and
  quoted-printable; RFC 2047 encoded words (joined across words in one charset), RFC 2231 parameters with
  continuations, address lists with groups and comments, RFC 5322 dates with the obsolete forms; charsets by name with
  the usual aliases and misspellings (ISO-8859-x, Windows-125x, KOI8, Shift_JIS, GB18030, Big5, EUC-KR, ...), and
  UTF-8 or Windows-1252 when a part has none. Damaged mail (a missing closing boundary, broken base64, a part without
  headers, an mbox `From ` line) is shown as far as it can be read.
- **Outlook `.msg` files.** Every method that takes a message also takes the bytes of an `.msg` file (recognised by
  its content, not its name): its own compound file reader takes out the subject, sender (and "on behalf of"),
  recipients, date, Reply-To, the HTML body (also when Outlook kept it only as compressed RTF), the plain text or rich
  text body, inline images, attachments and attached Outlook messages, which become `.eml` attachments. 8-bit
  strings of older Outlook versions are read in the message's code page, and Exchange addresses without an SMTP
  address are left out.
- **The body a mail client shows.** The HTML alternative (or the plain text one, with `PreferPlainText`), the root
  of a multipart/related, inline images by `cid:` and `Content-Location`, images placed between text parts (Apple
  Mail), and plain text with format=flowed paragraphs joined and links made clickable.
- **A header block.** Subject, From, Sent, To, Cc, Bcc, Reply-To and the attachments with their sizes. The labels
  (`EmailLabels`) and the date format (`DateFormatter`) can be changed, and the block can be left out.
- **Attachments in the PDF.** Every attachment, attached message (as an `.eml` file), calendar invitation and
  unreferenced inline image is embedded with its MIME type and dates and listed in the viewer's attachments panel.
  With PDF/A-3 they become associated files (relationship Supplement), and `EmbedOriginalMessage` adds the message
  itself (relationship Source), as the `.msg` file when it was one.
- **PDF metadata.** The title, author and language default to the subject, the sender and Content-Language; all of
  PdfPecker's options work: page size, margins, fonts, PDF/A, PDF/UA, encryption and signing.
- **`EmailMessage`** reads a message without converting it: subject, addresses, date, message ID, the plain text and
  HTML bodies, and the attachments with their decoded content.

```csharp
var message = EmailMessage.Load("message.eml");
Console.WriteLine($"{message.Subject} from {message.From[0]} on {message.Date}");
foreach (var attachment in message.Attachments)
    File.WriteAllBytes(attachment.FileName, attachment.Data);

// Danish labels and the date in Copenhagen time
var danish = new EmailPdfOptions
{
    Labels = new EmailLabels { From = "Fra", Date = "Sendt", To = "Til", Attachments = "Vedhæftede filer", NoSubject = "(intet emne)" },
    DateFormatter = d => TimeZoneInfo.ConvertTime(d, TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"))
        .ToString("d. MMMM yyyy HH:mm", new System.Globalization.CultureInfo("da-DK")),
};
byte[] pdf = EmailToPdf.Convert(message, danish);
```

## Mail is untrusted input

A message comes from a stranger, so the conversion treats it as hostile:

- **HTML is sanitised.** Only presentational elements and attributes are kept. Scripts, frames, objects, embeds,
  forms, SVG and MathML are dropped with their content; event handlers and unknown attributes go; links keep only
  http, https, mailto and tel (also when the scheme is hidden behind character references or tabs).
- **CSS stays inside the body.** Every selector of the message's style sheets is scoped to the body, so the message
  cannot restyle or hide the header block. `@page`, `@import`, unknown at-rules and named pages (`page`, which
  Outlook's HTML always has) are dropped, and fixed, absolute and sticky positioning is removed, so nothing paints over
  the header.
- **Nothing is fetched and no files are read.** Images load only from the message's own parts and `data:` URIs.
  Remote images (and tracking pixels) are left out unless you set `PdfOptions.ResourceResolver`, which is then asked
  for http and https URLs only; `PdfOptions.BaseDirectory` and `AllowFileAccess` are ignored.
- **Bounded time and memory.** Parsing is linear in the size of the message, with `EmailLimits` on the message size
  (100 MB), the number of parts (10,000) and the nesting depth (64); over a limit the conversion throws
  `PdfLimitException`. An `.msg` file's size limit applies to the file, its attachments count as parts and its attached
  messages as nesting; a damaged compound file (broken or cyclic sector chains, entries that share a stream) is read as
  far as it goes, within a read budget of a few times the file's size. HTML nesting deeper than 200 elements is flattened. PdfPecker's own limits
  (`PdfOptions.MaxPages`) apply to the PDF.
- **Documented exceptions only.** Damaged mail never fails the conversion; it fails only with `PdfLimitException`,
  `ArgumentException` for invalid options (such as attachments with PDF/A-2), `OperationCanceledException`, or
  `PdfPeckerException` from PdfPecker.

## Limitations

- `winmail.dat` (TNEF) is not read yet; it is planned. A `winmail.dat` is embedded as an attachment.
- Outlook items that are not mail (appointments, contacts, tasks) are shown as the message part they carry, usually
  the subject and body only. Attachments that are embedded OLE objects or links to files have no content to keep and
  are listed empty. Rich text bodies (not HTML) become plain text without their formatting.
- Attached messages are embedded as `.eml` files, not drawn in the PDF.
- S/MIME and PGP: signed messages show their content and keep the signature as an attachment; encrypted ones show
  only the encrypted part as an attachment. Signatures are not verified.
- Fonts are PdfPecker's: text in scripts the bundled fonts lack (Chinese, Japanese, Korean, Arabic, ...) needs fonts
  in `PdfOptions.Fonts` or `PdfOptions.UseSystemFonts = true`.
- Layout is PdfPecker's HTML and CSS support, which covers what mail uses (tables, inline styles, web fonts from the
  message); JavaScript never runs.

## Building

```
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run -c Release --project bench/PdfPecker.Email.Benchmarks
dotnet run -c Release --project fuzz/PdfPecker.Email.Fuzz -- --duration 5m
```

`tests/PdfPecker.Email.Tests/Golden/` holds the HTML each fixture message becomes; `PDFPECKER_UPDATE_GOLDEN=1`
rewrites it. The fuzzer is described in `fuzz/README.md`.

## License

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
