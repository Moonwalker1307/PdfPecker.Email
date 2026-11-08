using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Email.Tests.Msg;

/// <summary>
/// Writes Outlook .msg files for the tests and the fuzzer: MAPI properties in a version 3 compound file, with small
/// streams in the mini stream as Outlook writes them. It writes only what the reader needs, so the files are small and
/// every byte of them is known.
/// </summary>
internal sealed class MsgObject
{
    internal const int Unicode = 0x001F;
    internal const int Ansi = 0x001E;

    private readonly List<(int Id, int Type, long Value)> _fixed = [];

    internal SortedDictionary<string, byte[]> Streams { get; } = new(StringComparer.Ordinal);

    internal List<MsgObject> Recipients { get; } = [];

    internal List<MsgObject> Attachments { get; } = [];

    /// <summary>The message of an attachment whose method is 5 (an attached message).</summary>
    internal MsgObject? Embedded { get; set; }

    internal MsgObject String(int id, string value, int type = Unicode, Encoding? ansi = null)
    {
        var bytes = type == Unicode ? Encoding.Unicode.GetBytes(value + "\0") : (ansi ?? Encoding.Latin1).GetBytes(value + "\0");
        Streams[$"__substg1.0_{id:X4}{type:X4}"] = bytes;
        return this;
    }

    internal MsgObject Binary(int id, byte[] value)
    {
        Streams[$"__substg1.0_{id:X4}0102"] = value;
        return this;
    }

    internal MsgObject Int(int id, int value)
    {
        _fixed.Add((id, 0x0003, value));
        return this;
    }

    internal MsgObject Bool(int id, bool value)
    {
        _fixed.Add((id, 0x000B, value ? 1 : 0));
        return this;
    }

    internal MsgObject Time(int id, DateTimeOffset value)
    {
        _fixed.Add((id, 0x0040, value.ToFileTime()));
        return this;
    }

    internal MsgObject Recipient(string name, string address, int type = 1)
    {
        Recipients.Add(new MsgObject().String(0x3001, name).String(0x39FE, address).Int(0x0C15, type));
        return this;
    }

    internal MsgObject Attach(string name, byte[] data, string? mediaType = null, string? contentId = null)
    {
        var attachment = new MsgObject().Int(0x3705, 1).String(0x3707, name).Binary(0x3701, data);
        if (mediaType is not null) attachment.String(0x370E, mediaType);
        if (contentId is not null) attachment.String(0x3712, contentId);
        Attachments.Add(attachment);
        return this;
    }

    internal MsgObject AttachMessage(string name, MsgObject message)
    {
        Attachments.Add(new MsgObject { Embedded = message }.Int(0x3705, 5).String(0x3001, name));
        return this;
    }

    /// <summary>The .msg file of this object as the top-level message.</summary>
    internal byte[] ToMsg() => CompoundFileWriter.Write(ToStorage("Root Entry", 32));

    private CompoundFileWriter.Node ToStorage(string name, int headerSize)
    {
        var node = new CompoundFileWriter.Node(name, null);
        var properties = new byte[headerSize + 16 * _fixed.Count];
        if (headerSize >= 24)
        {
            BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(8), Recipients.Count);
            BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(12), Attachments.Count);
            BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(16), Recipients.Count);
            BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(20), Attachments.Count);
        }
        for (var i = 0; i < _fixed.Count; i++)
        {
            var (id, type, value) = _fixed[i];
            var at = headerSize + 16 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(at), (uint)(id << 16 | type));
            BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(at + 4), 6);
            BinaryPrimitives.WriteInt64LittleEndian(properties.AsSpan(at + 8), value);
        }
        node.Children.Add(new CompoundFileWriter.Node("__properties_version1.0", properties));
        foreach (var (stream, data) in Streams) node.Children.Add(new CompoundFileWriter.Node(stream, data));
        for (var i = 0; i < Recipients.Count; i++)
            node.Children.Add(Recipients[i].ToStorage($"__recip_version1.0_#{i:X8}", 8));
        for (var i = 0; i < Attachments.Count; i++)
        {
            var attachment = Attachments[i].ToStorage($"__attach_version1.0_#{i:X8}", 8);
            if (Attachments[i].Embedded is { } embedded) attachment.Children.Add(embedded.ToStorage("__substg1.0_3701000D", 24));
            node.Children.Add(attachment);
        }
        return node;
    }

    /// <summary>RTF in the compressed RTF container: "MELA" (stored) or "LZFu" made only of literals.</summary>
    internal static byte[] RtfContainer(string rtf, bool compressed)
    {
        var data = Encoding.Latin1.GetBytes(rtf);
        var body = new List<byte>();
        if (!compressed)
        {
            body.AddRange(data);
        }
        else
        {
            // Groups of eight items under a control byte: the literals, then a reference to the write position, which
            // ends the data.
            var items = data.Length + 1;
            for (var i = 0; i < items; i += 8)
            {
                var count = Math.Min(8, items - i);
                var control = i + count == items ? 1 << (count - 1) : 0;
                body.Add((byte)control);
                for (var k = i; k < i + count; k++)
                {
                    if (k < data.Length)
                    {
                        body.Add(data[k]);
                        continue;
                    }
                    var token = ((207 + data.Length) & 4095) << 4;
                    body.Add((byte)(token >> 8));
                    body.Add((byte)token);
                }
            }
        }
        var result = new byte[16 + body.Count];
        BinaryPrimitives.WriteInt32LittleEndian(result, body.Count + 12);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), Encoding.Latin1.GetByteCount(rtf));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), compressed ? 0x75465A4Cu : 0x414C454Du);
        body.CopyTo(result, 16);
        return result;
    }
}

/// <summary>Writes a version 3 compound file (512-byte sectors, 64-byte mini sectors below 4096 bytes).</summary>
internal static class CompoundFileWriter
{
    private const uint Free = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint DifatSector = 0xFFFFFFFC;

    internal sealed class Node(string name, byte[]? data)
    {
        internal string Name { get; } = name;
        internal byte[]? Data { get; } = data;
        internal List<Node> Children { get; } = [];
    }

    internal static byte[] Write(Node root)
    {
        var entries = new List<Node>();
        var ids = new Dictionary<Node, int>();
        void Add(Node node)
        {
            ids[node] = entries.Count;
            entries.Add(node);
            foreach (var child in node.Children) Add(child);
        }
        Add(root);

        // The mini stream holds every stream under 4096 bytes, each in whole 64-byte mini sectors.
        var mini = new List<byte>();
        var miniFat = new List<uint>();
        var starts = new uint[entries.Count];
        var big = new List<(int Entry, byte[] Data)>();
        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].Data is not { } data) continue;
            if (data.Length == 0)
            {
                starts[i] = EndOfChain;
            }
            else if (data.Length < 4096)
            {
                starts[i] = (uint)miniFat.Count;
                var count = (data.Length + 63) / 64;
                for (var k = 0; k < count; k++) miniFat.Add(k == count - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
                mini.AddRange(data);
                mini.AddRange(new byte[count * 64 - data.Length]);
            }
            else
            {
                big.Add((i, data));
            }
        }

        var sectors = new List<byte[]>();
        var fat = new List<uint>();
        uint Chain(byte[] data)
        {
            if (data.Length == 0) return EndOfChain;
            var start = (uint)sectors.Count;
            var count = (data.Length + 511) / 512;
            for (var k = 0; k < count; k++)
            {
                var sector = new byte[512];
                data.AsSpan(k * 512, Math.Min(512, data.Length - k * 512)).CopyTo(sector);
                sectors.Add(sector);
                fat.Add(k == count - 1 ? EndOfChain : (uint)sectors.Count);
            }
            return start;
        }

        var miniStart = Chain(mini.ToArray());
        foreach (var (entry, data) in big) starts[entry] = Chain(data);
        var miniFatBytes = new byte[miniFat.Count * 4];
        for (var i = 0; i < miniFat.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), miniFat[i]);
        var miniFatStart = Chain(miniFatBytes);

        var directory = new byte[(entries.Count + 3) / 4 * 4 * 128];
        for (var i = 0; i < directory.Length / 128; i++)
        {
            var at = directory.AsSpan(i * 128, 128);
            BinaryPrimitives.WriteUInt32LittleEndian(at[0x44..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(at[0x48..], Free);
            BinaryPrimitives.WriteUInt32LittleEndian(at[0x4C..], Free);
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var at = directory.AsSpan(i * 128, 128);
            var node = entries[i];
            var name = Encoding.Unicode.GetBytes(node.Name);
            name.AsSpan(0, Math.Min(name.Length, 62)).CopyTo(at);
            BinaryPrimitives.WriteUInt16LittleEndian(at[0x40..], (ushort)(Math.Min(name.Length, 62) + 2));
            at[0x42] = (byte)(i == 0 ? 5 : node.Data is null ? 1 : 2);
            at[0x43] = 1;
            // Children are linked as a chain of right siblings: a valid, if unbalanced, tree.
            if (node.Children.Count > 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(at[0x4C..], (uint)ids[node.Children[0]]);
                for (var k = 0; k + 1 < node.Children.Count; k++)
                    BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(ids[node.Children[k]] * 128 + 0x48), (uint)ids[node.Children[k + 1]]);
            }
            var start = i == 0 ? miniStart : node.Data is null ? 0 : starts[i];
            BinaryPrimitives.WriteUInt32LittleEndian(at[0x74..], start);
            BinaryPrimitives.WriteInt64LittleEndian(at[0x78..], i == 0 ? mini.Count : node.Data?.Length ?? 0);
        }
        var directoryStart = Chain(directory);

        // The FAT covers its own sectors and the DIFAT sectors, which list the FAT sectors beyond the header's 109.
        int fatSectors = 0, difatSectors = 0;
        while (sectors.Count + fatSectors + difatSectors > fatSectors * 128)
        {
            fatSectors++;
            difatSectors = Math.Max(0, (fatSectors - 109 + 126) / 127);
        }
        var firstFat = sectors.Count;
        var firstDifat = firstFat + fatSectors;
        for (var k = 0; k < fatSectors; k++) fat.Add(FatSector);
        for (var k = 0; k < difatSectors; k++) fat.Add(DifatSector);
        while (fat.Count < fatSectors * 128) fat.Add(Free);
        for (var k = 0; k < fatSectors; k++)
        {
            var sector = new byte[512];
            for (var j = 0; j < 128; j++) BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(j * 4), fat[k * 128 + j]);
            sectors.Add(sector);
        }
        for (var d = 0; d < difatSectors; d++)
        {
            var sector = new byte[512];
            for (var j = 0; j < 127; j++)
            {
                var index = 109 + d * 127 + j;
                BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(j * 4), index < fatSectors ? (uint)(firstFat + index) : Free);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(508), d + 1 < difatSectors ? (uint)(firstDifat + d + 1) : EndOfChain);
            sectors.Add(sector);
        }

        var header = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x2C), fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFatStart);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x40), (miniFatBytes.Length + 511) / 512);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), difatSectors > 0 ? (uint)firstDifat : EndOfChain);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x48), difatSectors);
        for (var k = 0; k < 109; k++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + k * 4), k < fatSectors ? (uint)(firstFat + k) : Free);

        var file = new byte[512 * (1 + sectors.Count)];
        header.CopyTo(file, 0);
        for (var k = 0; k < sectors.Count; k++) sectors[k].CopyTo(file, 512 * (k + 1));
        return file;
    }
}
