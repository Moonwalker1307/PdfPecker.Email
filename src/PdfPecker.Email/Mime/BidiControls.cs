using System.Buffers;

namespace PdfPecker.Email.Mime;

/// <summary>
/// Removes the Unicode characters that reorder text explicitly: overrides (U+202D, U+202E), embeddings (U+202A,
/// U+202B, U+202C) and isolates (U+2066 to U+2069). A sender can use them to make "invoice‮fdp.exe" read as
/// "invoiceexe.pdf", or to make an address read as another one. Right-to-left text needs none of them to show
/// correctly, so headers and file names lose them; the body keeps them, as mail clients show it.
/// </summary>
internal static class BidiControls
{
    private static readonly SearchValues<char> Reordering = SearchValues.Create("‪‫‬‭‮⁦⁧⁨⁩");

    // File names lose the direction marks as well (U+200E, U+200F, U+061C): a name needs no direction of its own,
    // and a mark can hide where the extension really starts.
    private static readonly SearchValues<char> InFileNames = SearchValues.Create("‪‫‬‭‮⁦⁧⁨⁩‎‏؜");

    internal static string Remove(string text) => Remove(text, Reordering);

    internal static string RemoveFromFileName(string name) => Remove(name, InFileNames);

    private static string Remove(string text, SearchValues<char> characters)
    {
        var first = text.AsSpan().IndexOfAny(characters);
        if (first < 0) return text;
        return string.Create(text.Length - Count(text.AsSpan(first), characters), (text, characters), static (span, state) =>
        {
            var written = 0;
            foreach (var c in state.text)
            {
                if (!state.characters.Contains(c)) span[written++] = c;
            }
        });
    }

    private static int Count(ReadOnlySpan<char> text, SearchValues<char> characters)
    {
        var count = 0;
        int at;
        while ((at = text.IndexOfAny(characters)) >= 0)
        {
            count++;
            text = text[(at + 1)..];
        }
        return count;
    }
}
