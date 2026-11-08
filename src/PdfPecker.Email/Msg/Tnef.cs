using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Email.Msg;

/// <summary>
/// Reads a winmail.dat (TNEF, MS-OXTNEF): the stream Outlook and Exchange attach in place of a message's rich body and
/// attachments when they send in their own format. It holds the same MAPI properties as an .msg file, so the result is
/// a <see cref="MapiObject"/> that <see cref="OutlookMessage"/> writes as MIME. Damaged streams are read as far as
/// they go; every length is checked against what is left, so the work is linear in the stream's size, and attached
/// messages are read only when they are written, which bounds their nesting.
/// </summary>
internal sealed class TnefObject : MapiObject
{
    private const uint Signature = 0x223E9F78;

    private const int AttributeFrom = 0x8000;
    private const int AttributeSubject = 0x8004;
    private const int AttributeDateSent = 0x8005;
    private const int AttributeDateReceived = 0x8006;
    private const int AttributeBody = 0x800C;
    private const int AttributeAttachData = 0x800F;
    private const int AttributeAttachTitle = 0x8010;
    private const int AttributeAttachCreateDate = 0x8012;
    private const int AttributeAttachModifyDate = 0x8013;
    private const int AttributeAttachRenderData = 0x9002;
    private const int AttributeMessageProperties = 0x9003;
    private const int AttributeRecipientTable = 0x9004;
    private const int AttributeAttachment = 0x9005;
    private const int AttributeOemCodePage = 0x9007;

    private readonly Dictionary<int, (int Type, ReadOnlyMemory<byte> Data, long Value)> _properties = [];
    private readonly List<MapiObject> _recipients = [];
    private readonly List<MapiObject> _attachments = [];
    private int _codePage;

    private TnefObject(int codePage) => _codePage = codePage;

    /// <summary>Whether the data starts with the TNEF signature.</summary>
    internal static bool IsTnef(ReadOnlySpan<byte> data) => data.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Signature;

    /// <summary>The message in a TNEF stream, or null when the data is not one.</summary>
    internal static TnefObject? Read(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Read(data, 1252, cancellationToken);

    private static TnefObject? Read(ReadOnlyMemory<byte> memory, int codePage, CancellationToken cancellationToken)
    {
        var data = memory.Span;
        if (!IsTnef(data)) return null;
        var message = new TnefObject(codePage);
        TnefObject? attachment = null;
        var pos = 6;
        var count = 0;
        while (pos + 9 <= data.Length)
        {
            if ((++count & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var level = data[pos];
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 1)..]);
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 5)..]), (uint)(data.Length - pos - 9));
            pos += 9;
            var value = memory.Slice(pos, length);
            // Each attribute ends in a two-byte checksum, which damaged streams get wrong anyway; it is not checked.
            pos += length + 2;

            var attribute = (int)(id & 0xFFFF);
            if (attribute == AttributeAttachRenderData || (level == 2 && attachment is null))
            {
                attachment = new TnefObject(message._codePage);
                message._attachments.Add(attachment);
                if (attribute == AttributeAttachRenderData) continue;
            }
            var target = level == 2 ? attachment! : message;
            switch (attribute)
            {
                case AttributeOemCodePage when length >= 4:
                    message._codePage = (int)BinaryPrimitives.ReadUInt32LittleEndian(value.Span);
                    break;
                case AttributeSubject:
                    target.Legacy(0x0037, 0x001E, value);
                    break;
                case AttributeBody:
                    target.Legacy(0x1000, 0x001E, value);
                    break;
                case AttributeDateSent:
                    target.LegacyTime(0x0039, value.Span);
                    break;
                case AttributeDateReceived:
                    target.LegacyTime(0x0E06, value.Span);
                    break;
                case AttributeAttachTitle:
                    target.Legacy(0x3704, 0x001E, value);
                    break;
                case AttributeAttachData:
                    target.Legacy(0x3701, 0x0102, value);
                    break;
                case AttributeAttachCreateDate:
                    target.LegacyTime(0x3007, value.Span);
                    break;
                case AttributeAttachModifyDate:
                    target.LegacyTime(0x3008, value.Span);
                    break;
                case AttributeMessageProperties or AttributeAttachment:
                    ReadProperties(value, 0, target);
                    break;
                case AttributeRecipientTable:
                    ReadRecipients(value, message);
                    break;
                case AttributeFrom:
                    // A sender triple in an old format; the message properties carry the same sender, readably.
                    break;
            }
        }
        foreach (var a in message._attachments) ((TnefObject)a)._codePage = message.CodePage;
        return message;
    }

    private int CodePage => Int(0x3FDE) ?? Int(0x3FFD) ?? _codePage;

    internal override Encoding Encoding => RtfText.CodePage(CodePage);

    internal override IReadOnlyList<MapiObject> Recipients => _recipients;

    internal override IReadOnlyList<MapiObject> Attachments => _attachments;

    internal override MapiObject? EmbeddedMessage
    {
        get
        {
            // An attached message is a PT_OBJECT holding an interface ID and a TNEF stream of its own.
            if (!_properties.TryGetValue(0x3701, out var p) || p.Type != 0x000D || p.Data.Length <= 16) return null;
            return Read(p.Data[16..], CodePage, CancellationToken.None);
        }
    }

    internal override string? String(int id)
    {
        if (!_properties.TryGetValue(id, out var p)) return null;
        var text = p.Type switch
        {
            0x001F => Encoding.Unicode.GetString(p.Data.Span),
            0x001E => Encoding.GetString(p.Data.Span),
            _ => null,
        };
        if (text is null) return null;
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    internal override byte[]? Binary(int id) => _properties.TryGetValue(id, out var p) && p.Type == 0x0102 ? p.Data.ToArray() : null;

    internal override int? Int(int id) => _properties.TryGetValue(id, out var p) && p.Type == 0x0003 ? (int)p.Value : null;

    internal override bool? Bool(int id) => _properties.TryGetValue(id, out var p) && p.Type == 0x000B ? (p.Value & 0xFFFF) != 0 : null;

    internal override DateTimeOffset? Time(int id) => _properties.TryGetValue(id, out var p) && p.Type == 0x0040 ? FromFileTime(p.Value) : null;

    // The attributes of the old format fill in what the MAPI properties leave out; the properties win.
    private void Legacy(int id, int type, ReadOnlyMemory<byte> data) => _properties.TryAdd(id, (type, data, 0));

    private void LegacyTime(int id, ReadOnlySpan<byte> data)
    {
        // A DTR: year, month, day, hour, minute, second and day of week, in 16-bit numbers. The time zone is the
        // sender's and unknown, so it is read as UTC.
        if (data.Length < 12) return;
        int year = BinaryPrimitives.ReadUInt16LittleEndian(data), month = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            day = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]), hour = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            minute = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]), second = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        if (year is < 1980 or > 4000 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) return;
        _properties.TryAdd(id, (0x0040, default, new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToFileTime()));
    }

    private static void ReadRecipients(ReadOnlyMemory<byte> memory, TnefObject message)
    {
        var data = memory.Span;
        if (data.Length < 4) return;
        var rows = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var pos = 4;
        for (var r = 0u; r < rows && pos < data.Length; r++)
        {
            var recipient = new TnefObject(message._codePage);
            var next = ReadProperties(memory, pos, recipient);
            if (next <= pos) break;
            pos = next;
            message._recipients.Add(recipient);
        }
    }

    /// <summary>Reads a property list (a count, then the properties) at <paramref name="start"/>; returns where it ends.</summary>
    private static int ReadProperties(ReadOnlyMemory<byte> memory, int start, TnefObject target)
    {
        var data = memory.Span;
        if (start + 4 > data.Length) return data.Length;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[start..]);
        var pos = start + 4;
        for (var i = 0u; i < count; i++)
        {
            if (pos + 4 > data.Length) return data.Length;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            pos += 4;
            var named = id >= 0x8000;
            if (named)
            {
                // A named property: a GUID, then a number or a name. Named properties are not used, but read past.
                if (pos + 20 > data.Length) return data.Length;
                var kind = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 16)..]);
                pos += 20;
                if (kind == 0)
                {
                    pos += 4;
                }
                else
                {
                    if (pos + 4 > data.Length) return data.Length;
                    var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                    if (nameLength > data.Length - pos - 4) return data.Length;
                    pos += 4 + Pad((int)nameLength);
                }
            }
            var multi = (type & 0x1000) != 0;
            var baseType = type & 0x0FFF;
            var variable = baseType is 0x001E or 0x001F or 0x0102 or 0x000D;
            var values = 1u;
            if (multi || variable)
            {
                if (pos + 4 > data.Length) return data.Length;
                values = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                pos += 4;
            }
            for (var v = 0u; v < values; v++)
            {
                if (pos > data.Length) return data.Length;
                ReadOnlyMemory<byte> value;
                long number = 0;
                if (variable)
                {
                    if (pos + 4 > data.Length) return data.Length;
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                    if (length > data.Length - pos - 4) return data.Length;
                    value = memory.Slice(pos + 4, (int)length);
                    pos += 4 + Pad((int)length);
                }
                else
                {
                    var size = baseType switch
                    {
                        0x0002 or 0x0003 or 0x0004 or 0x000A or 0x000B => 4,
                        0x0005 or 0x0006 or 0x0007 or 0x0014 or 0x0040 => 8,
                        0x0048 => 16,
                        _ => -1,
                    };
                    // An unknown type has an unknown size: nothing after it can be found.
                    if (size < 0 || pos + size > data.Length) return data.Length;
                    value = memory.Slice(pos, size);
                    number = size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(data[pos..]) : size == 8 ? BinaryPrimitives.ReadInt64LittleEndian(data[pos..]) : 0;
                    pos += size;
                }
                if (v == 0 && !named && !multi) target._properties[id] = (baseType, value, number);
            }
        }
        return pos;
    }

    private static int Pad(int length) => length + (-length & 3);
}
