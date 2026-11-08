using PdfPecker;
using PdfPecker.Email;

// eml2pdf message.eml message.pdf [--pdfa] [--original] [--text]
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: eml2pdf <message.eml> <message.pdf> [--pdfa] [--original] [--text]");
    return 2;
}
var options = new EmailPdfOptions
{
    EmbedOriginalMessage = args.Contains("--original"),
    PreferPlainText = args.Contains("--text"),
    Pdf = new PdfOptions { Conformance = args.Contains("--pdfa") ? PdfConformance.PdfA3u : PdfConformance.None },
};
try
{
    var message = EmailMessage.Load(args[0], options.Limits);
    File.WriteAllBytes(args[1], EmailToPdf.Convert(message, options));
    Console.WriteLine($"{args[1]}: \"{message.Subject}\", {message.Attachments.Count} attachment(s)");
    return 0;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfPeckerException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
