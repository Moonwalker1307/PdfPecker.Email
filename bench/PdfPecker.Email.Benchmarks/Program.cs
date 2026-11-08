using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfPecker;
using PdfPecker.Email;

// Times reading and converting messages: the test fixtures, and generated ones that stand for a newsletter, a long
// thread and a message with large attachments. Each case is warmed up, then run until it has taken about two seconds,
// and the median time and the bytes allocated per run are printed. Compare versions on one machine only.
var cases = new List<(string Name, byte[] Eml)>();
var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
foreach (var file in Directory.GetFiles(fixtures, "*.eml").Order(StringComparer.Ordinal))
    cases.Add((Path.GetFileNameWithoutExtension(file), File.ReadAllBytes(file)));
cases.Add(("generated-newsletter", Generated.Newsletter()));
cases.Add(("generated-thread", Generated.Thread()));
cases.Add(("generated-attachments-20mb", Generated.Attachments()));

var filter = args.Length > 0 ? args[0] : null;
Console.WriteLine($"{"case",-28} {"size",10} {"parse",10} {"convert",10} {"alloc/conv",12} {"pdf",10}");
foreach (var (name, eml) in cases)
{
    if (filter is not null && !name.Contains(filter, StringComparison.Ordinal)) continue;
    var parse = Measure(() => _ = EmailMessage.Parse(eml).Attachments.Count, out _);
    byte[] pdf = [];
    var convert = Measure(() => pdf = EmailToPdf.Convert(eml), out var allocated);
    Console.WriteLine($"{name,-28} {Size(eml.Length),10} {Ms(parse),10} {Ms(convert),10} {Size(allocated),12} {Size(pdf.Length),10}");
}

static double Measure(Action action, out long allocatedPerRun)
{
    for (var i = 0; i < 3; i++) action();
    var times = new List<double>();
    var total = Stopwatch.StartNew();
    var before = GC.GetAllocatedBytesForCurrentThread();
    while (total.Elapsed < TimeSpan.FromSeconds(2) || times.Count < 5)
    {
        var watch = Stopwatch.StartNew();
        action();
        times.Add(watch.Elapsed.TotalMilliseconds);
        if (times.Count >= 2000) break;
    }
    allocatedPerRun = (GC.GetAllocatedBytesForCurrentThread() - before) / times.Count;
    times.Sort();
    return times[times.Count / 2];
}

static string Ms(double ms) => ms.ToString(ms < 10 ? "0.000" : "0.0", CultureInfo.InvariantCulture) + " ms";

static string Size(long bytes) => bytes switch
{
    < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
    _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
};

internal static class Generated
{
    private static string Base64Lines(byte[] data)
    {
        var s = Convert.ToBase64String(data);
        var sb = new StringBuilder(s.Length + s.Length / 38);
        for (var i = 0; i < s.Length; i += 76) sb.Append(s, i, Math.Min(76, s.Length - i)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>A newsletter: HTML with a style sheet, 40 table rows of articles and quoted-printable encoding.</summary>
    public static byte[] Newsletter()
    {
        var html = new StringBuilder("<html><head><style>body{font-family:Arial} .a{color:#333} td{padding:8px} @media (max-width:600px){td{padding:2px}}</style></head><body><table width=\"600\">");
        for (var i = 0; i < 40; i++)
            html.Append(CultureInfo.InvariantCulture, $"<tr><td class=\"a\"><h2>Article {i}</h2><p style=\"margin:0 0 8px 0\">Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. <a href=\"https://example.com/a/{i}?utm_source=news\">Read more</a></p></td></tr>");
        html.Append("</table></body></html>");
        var qp = html.ToString().Replace("=", "=3D", StringComparison.Ordinal);
        return Encoding.ASCII.GetBytes("From: News <news@example.com>\r\nTo: a@example.com\r\nSubject: Newsletter\r\nDate: Sun, 8 Nov 2026 10:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=b\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nPlain\r\n--b\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" + qp + "\r\n--b--\r\n");
    }

    /// <summary>A long reply thread in plain text: 300 quoted paragraphs, about ten pages.</summary>
    public static byte[] Thread()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 300; i++)
            text.Append(new string('>', i % 4)).Append(" On day ").Append(i).Append(" someone wrote that the budget needs another look, see https://example.com/doc/").Append(i).Append("\r\n");
        return Encoding.UTF8.GetBytes("From: a@example.com\r\nSubject: Re: Budget\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + text);
    }

    /// <summary>A short message with four 5 MB attachments in base64.</summary>
    public static byte[] Attachments()
    {
        var random = new Random(3);
        var sb = new StringBuilder("From: a@example.com\r\nSubject: Files\r\nContent-Type: multipart/mixed; boundary=m\r\n\r\n--m\r\nContent-Type: text/plain\r\n\r\nSee attached.\r\n");
        for (var i = 0; i < 4; i++)
        {
            var data = new byte[5 * 1024 * 1024];
            random.NextBytes(data);
            sb.Append(CultureInfo.InvariantCulture, $"--m\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"file{i}.bin\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Base64Lines(data));
        }
        sb.Append("--m--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
