using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Email.Msg;

/// <summary>
/// Decompresses the RTF body of an Outlook message (MS-OXRTFCP): an LZ77 variant with a 4 KB dictionary that starts
/// with a fixed RTF prologue. The output is bounded by what the input can produce, whatever size the header claims.
/// </summary>
internal static class CompressedRtf
{
    private const uint Compressed = 0x75465A4C; // "LZFu"
    private const uint Uncompressed = 0x414C454D; // "MELA"

    private static readonly byte[] Prologue = Encoding.ASCII.GetBytes(
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

    internal static int PrologueLength => Prologue.Length;

    /// <summary>The RTF, or an empty array when the data is not compressed RTF.</summary>
    internal static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return [];
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var input = data[16..];
        if (type == Uncompressed) return input[..(int)Math.Min(input.Length, size)].ToArray();
        if (type != Compressed) return [];

        var dictionary = new byte[4096];
        Prologue.CopyTo(dictionary, 0);
        var write = Prologue.Length;
        // Each two-byte token yields at most 17 bytes, so the output cannot be larger than this.
        var capacity = (int)Math.Min(size, Math.Min((long)input.Length * 9, 256L << 20));
        var output = new byte[capacity];
        var written = 0;
        var i = 0;
        while (i < input.Length && written < capacity)
        {
            var control = input[i++];
            for (var bit = 0; bit < 8 && written < capacity; bit++)
            {
                if ((control & 1 << bit) == 0)
                {
                    if (i >= input.Length) return output.AsSpan(0, written).ToArray();
                    var b = input[i++];
                    output[written++] = b;
                    dictionary[write] = b;
                    write = (write + 1) & 4095;
                    continue;
                }
                if (i + 1 >= input.Length) return output.AsSpan(0, written).ToArray();
                var token = input[i] << 8 | input[i + 1];
                i += 2;
                var offset = token >> 4;
                var length = (token & 15) + 2;
                // A reference to the current write position ends the data.
                if (offset == write) return output.AsSpan(0, written).ToArray();
                for (var k = 0; k < length && written < capacity; k++)
                {
                    var b = dictionary[(offset + k) & 4095];
                    output[written++] = b;
                    dictionary[write] = b;
                    write = (write + 1) & 4095;
                }
            }
        }
        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }
}
