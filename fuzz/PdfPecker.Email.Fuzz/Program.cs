using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfPecker;
using PdfPecker.Email;
using PdfPecker.Email.Html;

namespace PdfPecker.Email.Fuzz;

/// <summary>
/// A dependency-free mutation fuzzer for PdfPecker.Email, built like PdfPecker's own: a supervisor process starts one
/// worker process per job and restarts it when an input crashes it or hangs past cancellation; workers mutate the seed
/// messages and report exceptions other than the documented ones, inputs slower than the timeout, and inputs that
/// allocate more than the memory ceiling. See fuzz/README.md.
/// </summary>
internal static class Program
{
    private static readonly string[] Targets = ["eml", "parse", "html", "sanitize"];

    private static int Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o.List)
        {
            Console.WriteLine(string.Join(' ', Targets));
            return 0;
        }
        if (o.Repro is not null) return Repro(o);
        if (o.Worker) return new Worker(o).Run();
        var targets = o.Target == "all" ? Targets : [o.Target];
        foreach (var target in targets)
        {
            if (Array.IndexOf(Targets, target) < 0)
            {
                Console.Error.WriteLine($"Unknown target '{target}'; use one of: {string.Join(' ', Targets)}, or all");
                return 2;
            }
            Supervise(o, target, args);
        }
        var findings = Directory.Exists(o.Out) ? Directory.GetFiles(o.Out, "*.txt", SearchOption.AllDirectories).Length : 0;
        Console.WriteLine($"{findings} finding(s) in {o.Out}");
        return findings > 0 ? 1 : 0;
    }

    private static void Supervise(Options o, string target, string[] args)
    {
        var clock = Stopwatch.StartNew();
        var lanes = Enumerable.Range(0, o.Jobs).Select(lane => Task.Run(() =>
        {
            var restarts = 0;
            while (clock.Elapsed < o.Duration && restarts < 50)
            {
                var remaining = o.Duration - clock.Elapsed;
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                if (IsDotnetHost()) psi.ArgumentList.Add(typeof(Program).Assembly.Location);
                // The worker gets the supervisor's arguments; the ones added after them win.
                foreach (var a in args) psi.ArgumentList.Add(a);
                foreach (var a in (string[])["--worker", "--target", target, "--lane", lane.ToString(CultureInfo.InvariantCulture),
                    "--seconds", ((int)Math.Max(1, remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
                    "--seed", (o.Seed * 1000 + lane * 100 + restarts).ToString(CultureInfo.InvariantCulture)])
                    psi.ArgumentList.Add(a);
                using var process = Process.Start(psi)!;
                process.WaitForExit();
                if (process.ExitCode is 0) return;
                // The worker died or stopped on a hang: the input it was running is in its .current file.
                var current = Worker.CurrentPath(o, target, lane);
                if (process.ExitCode != 3 && File.Exists(current))
                {
                    Findings.Save(o, target, "crash", File.ReadAllBytes(current), $"process exited with {process.ExitCode}");
                }
                restarts++;
            }
        })).ToArray();
        Task.WaitAll(lanes);
    }

    private static bool IsDotnetHost()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static int Repro(Options o)
    {
        var input = File.ReadAllBytes(o.Repro!);
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(o.Timeout);
            Run(o.Target, input, cts.Token);
            Console.WriteLine($"ok in {watch.ElapsedMilliseconds} ms");
            return 0;
        }
        catch (Exception e) when (Allowed(e))
        {
            Console.WriteLine($"allowed {e.GetType().Name} in {watch.ElapsedMilliseconds} ms: {e.Message}");
            return watch.Elapsed > o.Timeout ? 10 : 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return 10;
        }
    }

    internal static bool Allowed(Exception e) => e is PdfLimitException or OperationCanceledException;

    private static readonly EmailPdfOptions FuzzOptions = new() { Pdf = new PdfOptions { MaxPages = 50 }, EmbedOriginalMessage = true };

    internal static void Run(string target, byte[] input, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case "eml":
                EmailToPdf.Convert(input, FuzzOptions, cancellationToken);
                break;
            case "parse":
            {
                var m = EmailMessage.Parse(input, null, cancellationToken);
                _ = m.TextBody;
                _ = m.HtmlBody;
                foreach (var a in m.Attachments) _ = a.Data.Length + a.FileName.Length;
                _ = EmailToPdf.Prepare(m, FuzzOptions).Html;
                break;
            }
            case "html":
            {
                var eml = Encoding.UTF8.GetBytes("Content-Type: text/html; charset=utf-8\r\n\r\n").Concat(input).ToArray();
                EmailToPdf.Convert(eml, FuzzOptions, cancellationToken);
                break;
            }
            case "sanitize":
            {
                var html = Encoding.UTF8.GetString(input);
                var output = new StringBuilder();
                var css = new StringBuilder();
                HtmlSanitizer.Sanitize(html, output, css, _ => true, "s");
                // The fragment must come out balanced: sanitising it again changes nothing.
                var again = new StringBuilder();
                HtmlSanitizer.Sanitize(output.ToString(), again, new StringBuilder(), _ => true, "s");
                if (again.ToString() != output.ToString()) throw new InvalidOperationException("Sanitised HTML changed when sanitised again.");
                break;
            }
        }
    }
}

internal sealed class Options
{
    public string Target = "all";
    public TimeSpan Duration = TimeSpan.FromMinutes(1);
    public long Seed = 1;
    public int Jobs = 1;
    public string Out = "fuzz-findings";
    public TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public long MaxAllocatedMb = 2048;
    public bool Worker;
    public int Lane;
    public bool List;
    public string? Repro;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--target": o.Target = Value(); break;
                case "--duration": o.Duration = ParseDuration(Value()); break;
                case "--seconds": o.Duration = TimeSpan.FromSeconds(int.Parse(Value(), CultureInfo.InvariantCulture)); break;
                case "--seed": o.Seed = long.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--jobs": o.Jobs = Math.Max(1, int.Parse(Value(), CultureInfo.InvariantCulture)); break;
                case "--out": o.Out = Value(); break;
                case "--timeout": o.Timeout = TimeSpan.FromSeconds(double.Parse(Value(), CultureInfo.InvariantCulture)); break;
                case "--max-allocated-mb": o.MaxAllocatedMb = long.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--worker": o.Worker = true; break;
                case "--lane": o.Lane = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--list": o.List = true; break;
                case "--repro": o.Repro = Value(); break;
            }
        }
        return o;
    }

    private static TimeSpan ParseDuration(string s)
    {
        var unit = s[^1];
        var n = double.Parse(char.IsAsciiLetter(unit) ? s[..^1] : s, CultureInfo.InvariantCulture);
        return unit switch { 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => TimeSpan.FromSeconds(n) };
    }
}

internal sealed class Worker(Options o)
{
    private readonly Random _random = new((int)(o.Seed % int.MaxValue));

    public static string CurrentPath(Options o, string target, int lane) => Path.Combine(o.Out, target, $".current-{lane}");

    public int Run()
    {
        var seeds = Seeds.Load();
        Directory.CreateDirectory(Path.Combine(o.Out, o.Target));
        var current = CurrentPath(o, o.Target, o.Lane);
        var clock = Stopwatch.StartNew();
        long executions = 0, findings = 0;
        var nextReport = TimeSpan.FromSeconds(20);
        while (clock.Elapsed < o.Duration)
        {
            var input = Mutator.Mutate(seeds, _random);
            File.WriteAllBytes(current, input);
            var verdict = Execute(input);
            executions++;
            if (verdict is not null)
            {
                // A slow input is run again before it counts, because a busy machine can make an innocent one slow.
                if (verdict.StartsWith("timeout", StringComparison.Ordinal) && Execute(input) is null) verdict = null;
                if (verdict is not null && Findings.Save(o, o.Target, verdict.Split(':')[0], input, verdict)) findings++;
            }
            if (clock.Elapsed > nextReport)
            {
                Console.WriteLine($"[{o.Target}/{o.Lane}] {executions} executions ({executions / clock.Elapsed.TotalSeconds:0.#}/s), {findings} finding(s)");
                nextReport += TimeSpan.FromSeconds(20);
            }
        }
        File.Delete(current);
        Console.WriteLine($"[{o.Target}/{o.Lane}] finished: {executions} executions, {findings} finding(s)");
        return 0;
    }

    /// <summary>Runs one input; null when it passed, else the kind of finding and its details.</summary>
    private string? Execute(byte[] input)
    {
        string? verdict = null;
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(o.Timeout);
        var thread = new Thread(() =>
        {
            try
            {
                Program.Run(o.Target, input, cts.Token);
            }
            catch (Exception e) when (Program.Allowed(e))
            {
            }
            catch (Exception e)
            {
                verdict = "exception: " + e;
            }
        }, 64 * 1024 * 1024);
        thread.Start();
        if (!thread.Join(o.Timeout + TimeSpan.FromSeconds(5)))
        {
            // Cancellation did not stop it: save the input and let the supervisor start a new worker.
            Findings.Save(o, o.Target, "hang", input, $"still running {watch.Elapsed} after cancellation");
            Environment.Exit(3);
        }
        if (verdict is not null) return verdict;
        if (watch.Elapsed > o.Timeout) return $"timeout: {watch.Elapsed.TotalSeconds:0.0} s for {input.Length} bytes";
        var allocated = (GC.GetTotalAllocatedBytes() - allocatedBefore) / (1024 * 1024);
        if (allocated > o.MaxAllocatedMb) return $"memory: {allocated} MB allocated for {input.Length} bytes";
        return null;
    }
}

internal static class Findings
{
    private static readonly object Gate = new();

    /// <summary>Saves a finding once per signature; false when one with the same signature was already saved.</summary>
    public static bool Save(Options o, string target, string kind, byte[] input, string details)
    {
        var signature = kind + ":" + Signature(details);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..12].ToLowerInvariant();
        var dir = Path.Combine(o.Out, target);
        lock (Gate)
        {
            Directory.CreateDirectory(dir);
            var txt = Path.Combine(dir, $"{kind}-{hash}.txt");
            if (File.Exists(txt)) return false;
            var name = $"{kind}-{hash}.eml";
            File.WriteAllBytes(Path.Combine(dir, name), input);
            File.WriteAllText(txt, $"input: {name} ({input.Length} bytes)\nseed: {o.Seed}\nsignature: {signature}\n\n{details}\n");
        }
        Console.WriteLine($"FINDING {target}/{kind}-{hash}: {details.Split('\n')[0]}");
        return true;
    }

    // The exception type and the first frame in PdfPecker code, so one bug is reported once.
    private static string Signature(string details)
    {
        var lines = details.Split('\n');
        var frame = lines.FirstOrDefault(l => l.TrimStart().StartsWith("at PdfPecker", StringComparison.Ordinal))?.Trim() ?? "";
        var paren = frame.IndexOf('(');
        if (paren > 0) frame = frame[..paren];
        var head = lines[0];
        var colon = head.IndexOf(':', head.IndexOf(':') + 1);
        return (colon > 0 ? head[..colon] : head) + " " + frame;
    }
}

internal static class Seeds
{
    public static List<byte[]> Load()
    {
        var list = new List<byte[]>();
        var dir = Path.Combine(AppContext.BaseDirectory, "seeds");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir).Order(StringComparer.Ordinal)) list.Add(File.ReadAllBytes(file));
        }
        list.Add("Content-Type: text/html\r\n\r\n<table><tr><td style=\"color:red\"><a href=\"https://a\">x</a><img src=\"cid:a\"></td></tr></table>"u8.ToArray());
        list.Add("Subject: =?utf-8?B?w6Y=?= =?iso-8859-1?q?=E6?=\r\nTo: \"a, b\" <a@b>, c@d (e), g: h@i;\r\nContent-Type: text/plain; format=flowed\r\n\r\nline \r\n> quoted \r\nhttp://example.com\r\n"u8.ToArray());
        return list;
    }
}

internal static class Mutator
{
    private static readonly byte[][] Tokens =
    [
        "\r\n"u8.ToArray(), "\r\n\r\n"u8.ToArray(), "--"u8.ToArray(), "=?utf-8?B?"u8.ToArray(), "=?utf-8?Q?"u8.ToArray(), "?="u8.ToArray(),
        "Content-Type: multipart/mixed; boundary=b\r\n"u8.ToArray(), "Content-Type: message/rfc822\r\n"u8.ToArray(),
        "Content-Transfer-Encoding: base64\r\n"u8.ToArray(), "Content-Transfer-Encoding: quoted-printable\r\n"u8.ToArray(),
        "Content-Disposition: attachment; filename*0*=utf-8''%C3%A6; filename*1=x\r\n"u8.ToArray(), "\r\n--b\r\n"u8.ToArray(),
        "\r\n--b--\r\n"u8.ToArray(), "charset=\"x-unknown\""u8.ToArray(), "<"u8.ToArray(), ">"u8.ToArray(), "</"u8.ToArray(),
        "<!--"u8.ToArray(), "<style>"u8.ToArray(), "</style>"u8.ToArray(), "<script>"u8.ToArray(), "<svg>"u8.ToArray(),
        "<img src=\"cid:"u8.ToArray(), "<a href=\""u8.ToArray(), "\""u8.ToArray(), "'"u8.ToArray(), "&#"u8.ToArray(), "@media x{"u8.ToArray(),
        "{"u8.ToArray(), "}"u8.ToArray(), "position:fixed"u8.ToArray(), "=\r\n"u8.ToArray(), "=C3=A6"u8.ToArray(), "("u8.ToArray(),
        "<table><tr><td>"u8.ToArray(), "http://"u8.ToArray(), " \r\n"u8.ToArray(), "\0"u8.ToArray(), [0xC3], [0xFF, 0xFE],
    ];

    public static byte[] Mutate(List<byte[]> seeds, Random random)
    {
        var data = new List<byte>(seeds[random.Next(seeds.Count)]);
        var count = 1 + random.Next(random.Next(8) == 0 ? 40 : 6);
        for (var k = 0; k < count; k++)
        {
            var at = data.Count == 0 ? 0 : random.Next(data.Count + 1);
            switch (random.Next(9))
            {
                case 0 when data.Count > 0 && at < data.Count:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Count > 0 && at < data.Count:
                    data[at] = (byte)random.Next(256);
                    break;
                case 2 when data.Count > 0:
                {
                    var length = Math.Min(data.Count - Math.Min(at, data.Count), 1 + random.Next(64));
                    if (at < data.Count) data.RemoveRange(at, length);
                    break;
                }
                case 3 when data.Count > 0 && at < data.Count:
                {
                    var length = Math.Min(data.Count - at, 1 + random.Next(256));
                    data.InsertRange(at, data.GetRange(at, length));
                    break;
                }
                case 4:
                case 5:
                    data.InsertRange(at, Tokens[random.Next(Tokens.Length)]);
                    break;
                case 6:
                {
                    var other = seeds[random.Next(seeds.Count)];
                    if (other.Length == 0) break;
                    var start = random.Next(other.Length);
                    var length = Math.Min(other.Length - start, 1 + random.Next(512));
                    data.InsertRange(at, other.AsSpan(start, length).ToArray());
                    break;
                }
                case 7:
                {
                    // A token repeated many times finds work that grows faster than the input.
                    var token = Tokens[random.Next(Tokens.Length)];
                    var times = 100 + random.Next(20_000);
                    var block = new byte[token.Length * times];
                    for (var t = 0; t < times; t++) token.CopyTo(block, t * token.Length);
                    data.InsertRange(at, block);
                    break;
                }
                default:
                    if (data.Count > 0 && at < data.Count) data.RemoveRange(at, data.Count - at);
                    break;
            }
        }
        return data.ToArray();
    }
}
