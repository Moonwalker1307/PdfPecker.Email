using System.Text;
using PdfPecker.Email.Html;

namespace PdfPecker.Email.Tests;

public sealed class SanitizerTests
{
    [Theory]
    [InlineData("<script>alert(1)</script><p>x</p>", "<p>x</p>")]
    [InlineData("<SCRIPT type=x>a</script >b", "b")]
    [InlineData("<p onclick=\"x()\" class=a>t</p>", "<p class=\"a\">t</p>")]
    [InlineData("<iframe src=x><p>in</p></iframe>after", "after")]
    [InlineData("<object><object></object><p>in</p></object>after", "after")]
    [InlineData("<svg><g><svg></svg></g><text>t</text></svg>after", "after")]
    [InlineData("<form><input value=secret><button>Send</button></form>", "Send")]
    [InlineData("<o:p>outlook</o:p>", "outlook")]
    [InlineData("<!-- c --><!--> <!---->x<![CDATA[y]]>z<?php ?>", " xz")]
    [InlineData("a < b && c > d", "a &lt; b && c > d")]
    [InlineData("<b>x<i>y</b>z", "<b>x<i>y</i></b>z")]
    [InlineData("</div></body></html><p>t", "<p>t</p>")]
    [InlineData("<p>a<p>b<div>c</div>", "<p>a</p><p>b</p><div>c</div>")]
    [InlineData("<ul><li>a<li>b</ul>", "<ul><li>a</li><li>b</li></ul>")]
    [InlineData("<table><tr><td>a<td>b<tr><td>c</table>", "<table><tr><td>a</td><td>b</td></tr><tr><td>c</td></tr></table>")]
    [InlineData("x</br>y", "x<br>y")]
    [InlineData("<p title='say \"hi\"'>t</p>", "<p title=\"say &quot;hi&quot;\">t</p>")]
    [InlineData("<p title=\"a &amp; b &eacute;\">t</p>", "<p title=\"a &amp; b &eacute;\">t</p>")]
    [InlineData("<img src=x onerror=alert(1)>", "<img>")]
    [InlineData("<p\nclass=a\n>t</p>", "<p class=\"a\">t</p>")]
    [InlineData("<b>cut off <i", "<b>cut off </b>")]
    [InlineData("<p title=\"unterminated>t", "")]
    [InlineData("<textarea>hidden</textarea>shown", "shown")]
    [InlineData("<style>p{color:red}</style>t", "t")]
    public void Markup_is_cleaned(string html, string expected) => Assert.Equal(expected, Fixture.Sanitize(html));

    [Theory]
    [InlineData("https://example.com/a?b=1&amp;c=2", "https://example.com/a?b=1&amp;c=2")]
    [InlineData("HTTP://example.com", "HTTP://example.com")]
    [InlineData("mailto:a@example.com", "mailto:a@example.com")]
    [InlineData("tel:+4512345678", "tel:+4512345678")]
    [InlineData("#section", "#section")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData(" JaVaScRiPt:alert(1)", null)]
    [InlineData("java\tscript:alert(1)", null)]
    [InlineData("java&#x09;script:alert(1)", null)]
    [InlineData("&#106;avascript:alert(1)", null)]
    [InlineData("javascript&colon;alert(1)", null)]
    [InlineData("data:text/html,<script>", null)]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("relative/page.html", null)]
    [InlineData("vbscript:x", null)]
    public void Links_keep_only_safe_schemes(string href, string? expected)
    {
        var html = Fixture.Sanitize($"<a href=\"{href}\">x</a>");
        Assert.Equal(expected is null ? "<a>x</a>" : $"<a href=\"{expected}\">x</a>", html);
    }

    [Theory]
    [InlineData("cid:logo", true, false)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", true, false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false, false)]
    [InlineData("https://tracker.example/pixel.gif", false, false)]
    [InlineData("https://tracker.example/pixel.gif", true, true)]
    [InlineData("/etc/passwd", false, true)]
    [InlineData("file:///etc/passwd", false, true)]
    public void Image_sources_are_filtered(string src, bool kept, bool remote)
    {
        var html = Fixture.Sanitize($"<img src=\"{src}\">", remote && src.StartsWith("https", StringComparison.Ordinal));
        Assert.Equal(kept, html.Contains("src=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("color:red; position: fixed; top: 0", "color:red;top:0")]
    [InlineData("position:absolute", "")]
    [InlineData("position: relative", "position:relative")]
    [InlineData("po&#115;ition: fixed", "")]
    [InlineData("width: expression(alert(1))", "")]
    [InlineData("behavior: url(x.htc); color: blue", "color:blue")]
    [InlineData("color: red /* comment; */; font: 'a;b' 10pt", "color:red;font:'a;b' 10pt")]
    [InlineData("color: red; & .x { color: blue } margin: 0", "color:red;margin:0")]
    [InlineData("pos\\69tion: fixed", "")]
    [InlineData("width: expre\0ssion(alert(1))", "width:expre\uFFFDssion(alert(1))")]
    public void Inline_styles_are_filtered(string style, string expected)
    {
        var html = Fixture.Sanitize($"<p style=\"{style}\">t</p>");
        Assert.Equal(expected.Length == 0 ? "<p>t</p>" : $"<p style=\"{expected.Replace("'", "'", StringComparison.Ordinal)}\">t</p>", html);
    }

    [Fact]
    public void Style_sheets_are_scoped_to_the_body()
    {
        var css = new StringBuilder();
        CssFilter.AppendScoped(css, """
            @charset "utf-8";
            @import url(https://evil.example/x.css);
            @page { size: A3 }
            @page :first { margin: 0 }
            html, body { background: #eee } body.dark > p { color: white }
            html > body td, :root a { color: red }
            p, .a > b, a[title="x,y"] { margin: 0; position: fixed }
            @media print { body { margin: 0 } @media (min-width: 1px) { p { color: red } } }
            @font-face { font-family: X; src: url(cid:font) }
            @unknown foo { p { color: red } }
            <!-- li { color: blue } -->
            div { } }} stray { color: red }
            """, "s");
        Assert.Equal(
            ".s{background:#eee}.s.dark > p{color:white}.s td,.s a{color:red}.s p,.s .a > b,.s a[title=\"x,y\"]{margin:0}"
            + "@media print {.s{margin:0}@media (min-width: 1px) {.s p{color:red}}}@font-face{font-family:X;src:url(cid:font)}"
            + ".s li{color:blue}.s div{}.s stray{color:red}",
            css.ToString());
    }

    [Fact]
    public void Style_sheets_cannot_close_the_style_element()
    {
        var css = new StringBuilder();
        CssFilter.AppendScoped(css, "p { font-family: '</style><script>' }", "s");
        Assert.DoesNotContain("</", css.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_nesting_is_flattened()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 5000)) + "deep" + string.Concat(Enumerable.Repeat("</div>", 5000));
        var output = Fixture.Sanitize(html);
        Assert.Contains("deep", output);
        Assert.True(output.Split("<div>").Length - 1 <= 200);
        Assert.Equal(output.Split("<div>").Length, output.Split("</div>").Length);
    }

    [Fact]
    public void Body_attributes_go_to_the_wrapper()
    {
        var html = Fixture.Html(Fixture.FromText("Content-Type: text/html\n\n<html><head><style>body{background:#eee}</style></head><body bgcolor=\"#fff\" text=red class=\"x\" style=\"margin:0;position:fixed\" onload=\"x()\">t</body></html>"));
        // The bgcolor hint comes before the message's own CSS, which overrides it as in a browser.
        Assert.Matches(".pdfpecker-email-body\\{background-color:#fff;color:red;\\}[\\s\\S]*\\.pdfpecker-email-body\\{background:#eee\\}", html);
        Assert.Contains("<div class=\"pdfpecker-email-body x\" style=\"margin:0\">", html);
        Assert.DoesNotContain("onload", html);
    }
}
