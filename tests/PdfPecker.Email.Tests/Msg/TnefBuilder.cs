using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Email.Tests.Msg;

/// <summary>Writes winmail.dat (TNEF) streams for the tests and the fuzzer, with MAPI property lists as Outlook writes them.</summary>
internal sealed class TnefBuilder
{
    private readonly List<byte> _data = [];

    internal TnefBuilder()
    {
        Add(BitConverter.GetBytes(0x223E9F78u));
        Add(BitConverter.GetBytes((ushort)0x0001));
        // The TNEF version and the OEM code page, as Outlook writes them first.
        Attribute(1, 0x9006, 0x0008, BitConverter.GetBytes(0x00010000));
        Attribute(1, 0x9007, 0x0006, [.. BitConverter.GetBytes(1252), .. new byte[4]]);
    }

    internal TnefBuilder Attribute(int level, int id, int type, byte[] value)
    {
        _data.Add((byte)level);
        Add(BitConverter.GetBytes((uint)(type << 16 | id)));
        Add(BitConverter.GetBytes(value.Length));
        Add(value);
        var checksum = 0;
        foreach (var b in value) checksum += b;
        Add(BitConverter.GetBytes((ushort)checksum));
        return this;
    }

    internal TnefBuilder Subject(string subject) => Attribute(1, 0x8004, 0x0001, Encoding.Latin1.GetBytes(subject + "\0"));

    internal TnefBuilder MessageProperties(Properties properties) => Attribute(1, 0x9003, 0x0006, properties.ToArray());

    /// <summary>An attachment: the start marker, the 8.3 title and data of the old format, then its MAPI properties.</summary>
    internal TnefBuilder Attachment(string title, byte[]? data, Properties properties)
    {
        Attribute(2, 0x9002, 0x0006, new byte[14]);
        Attribute(2, 0x8010, 0x0001, Encoding.Latin1.GetBytes(title + "\0"));
        if (data is not null) Attribute(2, 0x800F, 0x0006, data);
        return Attribute(2, 0x9005, 0x0006, properties.ToArray());
    }

    internal byte[] ToArray() => [.. _data];

    private void Add(byte[] bytes) => _data.AddRange(bytes);

    /// <summary>A MAPI property list: a count, then each property's tag and value, padded to four bytes.</summary>
    internal sealed class Properties
    {
        private readonly List<byte[]> _items = [];

        internal Properties Unicode(int id, string value) => Variable(id, 0x001F, Encoding.Unicode.GetBytes(value + "\0"));

        internal Properties Binary(int id, byte[] value) => Variable(id, 0x0102, value);

        /// <summary>An attached message: PT_OBJECT holding IID_IMessage and a TNEF stream.</summary>
        internal Properties Message(int id, byte[] tnef) => Variable(id, 0x000D, [.. Guid.Parse("00020307-0000-0000-c000-000000000046").ToByteArray(), .. tnef]);

        internal Properties Int(int id, int value)
        {
            _items.Add([.. Tag(id, 0x0003), .. BitConverter.GetBytes(value)]);
            return this;
        }

        internal Properties Time(int id, DateTimeOffset value)
        {
            _items.Add([.. Tag(id, 0x0040), .. BitConverter.GetBytes(value.ToFileTime())]);
            return this;
        }

        /// <summary>A named property, which the reader must read past.</summary>
        internal Properties Named(string name, string value)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
            var bytes = new List<byte>(Tag(0x8001, 0x001F));
            bytes.AddRange(Guid.NewGuid().ToByteArray());
            bytes.AddRange(BitConverter.GetBytes(1));
            bytes.AddRange(BitConverter.GetBytes(nameBytes.Length));
            bytes.AddRange(Padded(nameBytes));
            var data = Encoding.Unicode.GetBytes(value + "\0");
            bytes.AddRange(BitConverter.GetBytes(1));
            bytes.AddRange(BitConverter.GetBytes(data.Length));
            bytes.AddRange(Padded(data));
            _items.Add([.. bytes]);
            return this;
        }

        internal byte[] ToArray()
        {
            var result = new List<byte>(BitConverter.GetBytes(_items.Count));
            foreach (var item in _items) result.AddRange(item);
            return [.. result];
        }

        private Properties Variable(int id, int type, byte[] value)
        {
            _items.Add([.. Tag(id, type), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(value.Length), .. Padded(value)]);
            return this;
        }

        private static byte[] Tag(int id, int type)
        {
            var tag = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(tag, (ushort)type);
            BinaryPrimitives.WriteUInt16LittleEndian(tag.AsSpan(2), (ushort)id);
            return tag;
        }

        private static byte[] Padded(byte[] value) => [.. value, .. new byte[-value.Length & 3]];
    }
}
