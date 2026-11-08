# AGENTS.md

Guidance for coding agents (and people) working in this repository. `CLAUDE.md` imports this file, so Claude Code and
other agents read the same rules.

PdfPecker.Email is the `PdfPecker.Email` NuGet package: e-mail (.eml, MIME, Outlook .msg and winmail.dat) to PDF and
PDF/A-3 with the attachments embedded. It has its own MIME parser, Outlook reader, header decoders, HTML sanitiser and CSS filter,
and hands the HTML it writes to
the [PdfPecker](https://www.nuget.org/packages/PdfPecker) package, which lives in
[Moonwalker1307/PdfPecker](https://github.com/Moonwalker1307/PdfPecker). Outlook-only items
(appointments, contacts) as more than a message are not planned.

## Core rules

- **PdfPecker comes from NuGet, and nothing else does.** The package's only dependency is the released PdfPecker
  version in `Directory.Packages.props`. Never add another `PackageReference` to `src/PdfPecker.Email`, never copy
  library code here, and never build against an unreleased library. When this package needs something PdfPecker
  cannot do, fix the library in its own repository and pull request first, and bump the version here after the
  release. Test projects may use test packages (xunit); nothing else uses a third-party package.
- **Public API of PdfPecker only.** The package uses only what PdfPecker makes public, so a library refactor never
  breaks it.
- **Mail is untrusted input.** Every change keeps these: HTML is sanitised by an allow-list (`Html/HtmlSanitizer.cs`),
  CSS is scoped to the body and cannot place anything over the header (`Html/CssFilter.cs`), nothing is fetched and no
  local file is read (only the message's own parts, `data:` URIs and, for http and https only, a caller's
  `ResourceResolver`), and links keep only http, https, mailto and tel.
- **Damaged mail converts; only limits fail.** Parsing never throws on bad structure. The documented failures are
  `PdfLimitException` (`EmailLimits` or PdfPecker's limits), `ArgumentException` for invalid options,
  `OperationCanceledException` and `PdfPeckerException`. Every parser stays linear in its input; a new loop over input
  needs a bound or a test in `HostileInputTests`.
- **No breaking API changes before 2.0.** Add overloads and options; package validation checks the API against the
  last release once there is one (see `RELEASING.md`).
- **Trimmable and AOT-compatible.** No reflection, `dynamic` or runtime code generation.
- **Sealed, static and span-based.** Classes are `sealed` (or `static`), and `src/PdfPecker.Email/.globalconfig` turns
  the .NET performance analyzers into build errors, as in PdfPecker.
- **Culture-independent output.** Format and parse with `CultureInfo.InvariantCulture` and compare strings ordinally;
  the only culture-specific text is what a caller's `DateFormatter` writes.
- **Never name a user's private application or employer** in code, tests, fixtures, commits, the README or PR
  descriptions. Fixtures use example.com, example.org and example.net addresses and neutral names.

## Repo map

- `src/PdfPecker.Email/` – the package. Public API at the top level: `EmailToPdf`, `EmailMessage`, `EmailAddress`,
  `EmailAttachment`, `EmailPdfOptions`, `EmailLabels`, `EmailLimits`. `FileNames.cs` makes attachment names safe.
- `src/PdfPecker.Email/Mime/` – `MimeParser` (the entity tree, as ranges into the message bytes), `MimeEntity`,
  `HeaderValues` (RFC 2047 words, RFC 2231 parameters, addresses, dates), `TransferEncoding` (base64,
  quoted-printable), `Charsets` (charset names and the UTF-8 or Windows-1252 guess).
- `src/PdfPecker.Email/Html/` – `EmailHtmlWriter` (the document PdfPecker converts: header block and body),
  `HtmlSanitizer`, `CssFilter`, `PlainText` (format=flowed and links), `Markup` (escaping).
- `src/PdfPecker.Email/Msg/` – Outlook .msg and winmail.dat: `MapiObject` (MAPI properties, wherever they are
  stored), `CompoundFile` (the MS-CFB container of .msg files), `TnefObject` (winmail.dat, MS-OXTNEF), `OutlookMessage`
  (MAPI properties to the MIME message they stand for, which `EmailMessage.Parse` then reads like any .eml),
  `CompressedRtf` (MS-OXRTFCP) and `RtfText` (HTML encapsulated in RTF, or the plain text of other RTF).
- `tests/PdfPecker.Email.Tests/` – xUnit. `Fixtures/*.eml` are the sample messages (kept with CRLF line ends by
  `.gitattributes`), `Golden/*.html` the HTML each becomes. `Msg/MsgBuilder.cs` writes .msg files, `Msg/TnefBuilder.cs` winmail.dat streams, and `Msg/MsgFixtures.cs` the Outlook
  fixtures with it (real .msg files carry real people's addresses, so none are checked in); the fuzzer links both.
- `fuzz/PdfPecker.Email.Fuzz/` – the mutation fuzzer; `fuzz/README.md` describes it.
- `bench/PdfPecker.Email.Benchmarks/` – times parsing and conversion of the fixtures and generated messages.
- `.github/workflows/` – `build.yml` (build, tests on .NET 8, 9 and 10 and on Windows, pack, publish on a release),
  `fuzz.yml` (nightly).

## Build and verify

```
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run -c Release --project bench/PdfPecker.Email.Benchmarks
dotnet run -c Release --project fuzz/PdfPecker.Email.Fuzz -- --target all --duration 2m --jobs 4
```

Before pushing, the build must be warning-free (warnings are errors) and the tests must pass. If only one runtime is
installed, pass `-f net10.0`. A change to the HTML this package writes changes `Golden/*.html`: regenerate them with
`PDFPECKER_UPDATE_GOLDEN=1 dotnet test -f net10.0`, look at the diff and at the PDFs, and say in the commit and PR which
baselines changed and why. A refactor leaves every baseline unchanged.

## Change-specific guidance

- **A new header, MIME structure or encoding**: a fixture or an inline message in `MimeTests` or `MessageTests`, and a
  line in the README when users see it.
- **Outlook .msg**: the MIME written for a .msg file is base64 throughout and its header values have no control
  characters, so no property can add a header or a part; keep it so. A new property needs a `MsgTests` case built
  with `MsgObject`, and a check against a real Outlook file where one is at hand (not checked in).
- **Sanitiser and CSS filter changes**: a `SanitizerTests` case for what is kept and for what is dropped; anything
  that loosens the allow-list needs a reason in the PR.
- **A fuzz finding**: fix the cause, add the input (minimised) as a test in `HostileInputTests` or `SanitizerTests`,
  and run the failing target again until it passes, then all targets.
- **Performance-sensitive code**: run the benchmark before and after on one machine; cloud runners are too noisy to
  compare with earlier runs.
- **A PdfPecker bump**: the goldens are HTML and do not move; look at a few PDFs anyway, and use any new library
  option this package should pass through.

## Commit guidance

- Branch from the latest `origin/main`, one branch per piece of work, named after the work in lowercase words joined by
  hyphens, with a `claude/` prefix when an agent creates it (e.g. `claude/outlook-msg`). Never use a generic or
  generated name, and never reuse a branch name after its pull request is merged or closed.
- Commit subjects are short imperative or descriptive sentences without prefixes; the body explains why.
- Fill in `.github/pull_request_template.md`, and update `README.md` in the same pull request when a feature, option or
  limitation changes.
- Releases are the owner's step (`RELEASING.md`); never publish, tag or create a release.

## Wisdom

- Mail lies about its charset. Latin-1 and ASCII labels are read as Windows-1252, as browsers do, and an ASCII or
  Latin-1 label on valid multi-byte UTF-8 is read as UTF-8.
- PdfPecker repeats a link's URL in the annotation of every line the link covers, so a huge URL across many lines
  makes the PDF grow with the square of its length; links over 4,096 characters are written as text.
- `PdfAttachments.List` returns embedded files sorted by name (the PDF's name tree), not in the order they were added.
- Outlook keeps most HTML mail only as compressed RTF with the HTML encapsulated (`\fromhtml1`); the HTML property
  (0x1013) is often missing, so `RtfText` is the usual path for HTML from Outlook, not a fallback.
- A winmail.dat comes next to a plain text copy of the body. `EmailMessage.Tnef.cs` replaces that body only when the
  TNEF one has HTML (or the message has none), and always puts the TNEF attachments in the winmail.dat's place.
- Word's HTML, which Outlook sends, puts the body in `div.WordSection1 { page: WordSection1 }`; PdfPecker starts a new
  page for a named page, which is why `CssFilter` drops `page`.
- PdfPecker deflates every embedded file, so a large incompressible attachment (a zip, a JPEG) costs most of the
  conversion time.
