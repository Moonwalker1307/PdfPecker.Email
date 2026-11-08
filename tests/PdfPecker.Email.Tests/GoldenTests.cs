namespace PdfPecker.Email.Tests;

/// <summary>
/// The HTML each fixture becomes, compared with Golden/*.html. The HTML is this package's own output, so it does not
/// move when PdfPecker changes how it draws a page. PDFPECKER_UPDATE_GOLDEN=1 rewrites the baselines.
/// </summary>
public sealed class GoldenTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.eml").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(file));
        foreach (var name in Msg.MsgFixtures.Names) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Html_matches_the_baseline(string name)
    {
        var html = Fixture.Html(EmailMessage.Parse(Bytes(name))).Replace("\r\n", "\n", StringComparison.Ordinal);
        var golden = Path.Combine(SourceDirectory(), "Golden", name + ".html");
        if (Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(golden, html);
            return;
        }
        Assert.True(File.Exists(golden), $"No baseline {golden}; run with PDFPECKER_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n", StringComparison.Ordinal), html);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_fixture_converts(string name)
    {
        var pdf = EmailToPdf.Convert(Bytes(name));
        Assert.True(PdfToImage.GetPageCount(pdf) >= 1);
    }

    // The Outlook and winmail.dat fixtures are written by the tests, so no real person's .msg file is checked in.
    private static byte[] Bytes(string name) => name.StartsWith("msg-", StringComparison.Ordinal) || name.StartsWith("tnef-", StringComparison.Ordinal) ? Msg.MsgFixtures.Get(name) : Fixture.Bytes(name + ".eml");

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
