# PdfPecker.Email fuzzer

A dependency-free mutation fuzzer, built like PdfPecker's own. There is no coverage feedback, so it relies on the seed
messages (the test fixtures, two built-in ones, and for the Outlook targets the .msg files the tests write) and broad
mutations: bit flips, inserted MIME and HTML tokens,
spliced seeds, cut-off input, and tokens repeated thousands of times to find work that grows faster than the input.
It finds hangs, huge allocations, crashes and exception types the package does not document; it will not find logic
bugs.

```
dotnet build -c Release fuzz/PdfPecker.Email.Fuzz
dotnet fuzz/PdfPecker.Email.Fuzz/bin/Release/net10.0/PdfPecker.Email.Fuzz.dll --target all --duration 5m --jobs 4
```

| Option | Meaning |
| --- | --- |
| `--target <name\|all>` | `eml` (parse and convert, with the original embedded), `parse` (read the message and write the HTML, no PDF), `html` (the input as an HTML body, converted), `sanitize` (the sanitiser alone; its output sanitised again must not change), `msg` (an Outlook .msg file read and converted; most mutations change bytes in place so the compound file keeps its offsets), `rtf` (compressed RTF decompressed and read, and the input read as RTF), `tnef` (a winmail.dat on its own and inside a message, read and converted). `--list` prints them. |
| `--duration 90s\|10m\|1h` | How long to run each target (default 1m). |
| `--jobs <n>` | Worker processes per target (default 1); use the number of cores. |
| `--seed <n>` | Seed of the random generator; each worker derives its own from it. |
| `--timeout <s>` | Limit per input (default 10). The token is cancelled at the limit; an input still running five seconds later is a hang. |
| `--max-allocated-mb <n>` | Allocation ceiling per input (default 2048). |
| `--out <dir>` | Findings folder (default `fuzz-findings`). |
| `--repro <file> --target <name>` | Run one saved input; exits with 10 when it fails. |

A finding is an exception other than `PdfLimitException` and `OperationCanceledException`, a timeout (confirmed by a
second run), an input over the allocation ceiling, a hang, or a crash of the worker process, which the supervisor
notices and restarts. Each finding is saved once per signature as `<target>/<kind>-<hash>.eml` with a `.txt` holding
the details. The process exits with 1 when the findings folder has any finding, which fails the nightly job.

An exception from inside PdfPecker is a library bug: reproduce it with plain `HtmlToPdf` and fix it in the PdfPecker
repository.
