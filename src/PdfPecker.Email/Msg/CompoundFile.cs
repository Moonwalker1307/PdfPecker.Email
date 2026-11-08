using System.Buffers.Binary;
using System.Text;

namespace PdfPecker.Email.Msg;

/// <summary>
/// Reads the Compound File Binary format (MS-CFB) that Outlook .msg files are stored in: a small file system of
/// storages and streams inside one file. Damaged files are read as far as they go: a broken sector chain ends the
/// stream, a cycle is cut, and an entry that points outside the file is empty. Every chain walk is bounded by the
/// number of sectors in the file, so the work is linear in its size.
/// </summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint MaxRegular = 0xFFFFFFFA;
    private const int DirectoryEntrySize = 128;

    internal static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly int _sectorCount;
    private readonly uint[] _fat;
    private uint[] _miniFat = [];
    private byte[] _miniStream = [];

    // Damaged directories can point many entries at the same long chain; the bytes read over all streams are capped at
    // a multiple of the file's size, so such a file cannot make reading quadratic.
    private long _budget;

    private CompoundFile(byte[] data)
    {
        _data = data;
        _budget = 8L * data.Length + (16 << 20);
        var header = data.AsSpan(0, 512);
        var shift = BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..]);
        _sectorSize = shift == 12 ? 4096 : 512;
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]);
        _miniSectorSize = miniShift is > 0 and < 12 ? 1 << miniShift : 64;
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[0x38..]);
        if (_miniCutoff is 0 or > 1 << 20) _miniCutoff = 4096;
        _sectorCount = Math.Max(0, (data.Length - _sectorSize) / _sectorSize + ((data.Length - _sectorSize) % _sectorSize > 0 ? 1 : 0));
        _fat = ReadFat(header);
        Entries = ReadDirectory(BinaryPrimitives.ReadUInt32LittleEndian(header[0x30..]));
        if (Entries.Count > 0)
        {
            var root = Entries[0];
            _miniStream = ReadChain(root.Start, root.Size, _fat, regular: true);
            var miniFatBytes = ReadChain(BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]), long.MaxValue, _fat, regular: true);
            _miniFat = ToUInts(miniFatBytes);
        }
    }

    /// <summary>The directory: entry 0 is the root storage.</summary>
    internal List<DirectoryEntry> Entries { get; }

    internal static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.Length >= 512 && data.StartsWith(Signature);

    internal static CompoundFile Open(byte[] data) => new(data);

    private uint[] ReadFat(ReadOnlySpan<byte> header)
    {
        // The DIFAT lists the sectors that hold the FAT: 109 entries in the header, the rest in a chain of DIFAT
        // sectors whose last entry points to the next one.
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
        {
            var s = BinaryPrimitives.ReadUInt32LittleEndian(header[(0x4C + i * 4)..]);
            if (s <= MaxRegular) fatSectors.Add(s);
        }
        var difat = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var perSector = _sectorSize / 4 - 1;
        var guard = 0;
        while (difat <= MaxRegular && difat < _sectorCount && guard++ < _sectorCount)
        {
            var sector = Sector(difat);
            for (var i = 0; i < perSector; i++)
            {
                var s = BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]);
                if (s <= MaxRegular) fatSectors.Add(s);
            }
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[(perSector * 4)..]);
        }
        // A FAT longer than the file has sectors describes nothing more.
        var entriesPerSector = _sectorSize / 4;
        var count = Math.Min(fatSectors.Count, _sectorCount / entriesPerSector + 1);
        var fat = new uint[count * entriesPerSector];
        for (var k = 0; k < count; k++)
        {
            if (fatSectors[k] >= _sectorCount)
            {
                Array.Fill(fat, EndOfChain, k * entriesPerSector, entriesPerSector);
                continue;
            }
            var sector = Sector(fatSectors[k]);
            for (var i = 0; i < entriesPerSector; i++) fat[k * entriesPerSector + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]);
        }
        return fat;
    }

    private ReadOnlySpan<byte> Sector(uint index)
    {
        var offset = (long)(index + 1) * _sectorSize;
        if (offset >= _data.Length) return default;
        var length = (int)Math.Min(_sectorSize, _data.Length - offset);
        // A last sector cut short by a truncated file is padded with zeros.
        if (length < _sectorSize)
        {
            var padded = new byte[_sectorSize];
            _data.AsSpan((int)offset, length).CopyTo(padded);
            return padded;
        }
        return _data.AsSpan((int)offset, _sectorSize);
    }

    /// <summary>Reads a chain of regular sectors (or mini sectors) up to <paramref name="size"/> bytes.</summary>
    private byte[] ReadChain(uint start, long size, uint[] fat, bool regular)
    {
        var unit = regular ? _sectorSize : _miniSectorSize;
        var limit = regular ? _sectorCount : _miniStream.Length / _miniSectorSize;
        var output = new MemoryStream(size is > 0 and < int.MaxValue ? (int)Math.Min(size, (long)limit * unit) : 0);
        var sector = start;
        var steps = 0;
        while (sector <= MaxRegular && sector < limit && sector < fat.Length && steps++ <= limit && output.Length < size)
        {
            var remaining = (int)Math.Min(unit, size - output.Length);
            if ((_budget -= remaining) < 0)
                throw new PdfLimitException("The Outlook message's streams add up to far more than the file holds; the file is damaged.");
            if (regular)
            {
                output.Write(Sector(sector)[..remaining]);
            }
            else
            {
                var offset = (int)sector * _miniSectorSize;
                output.Write(_miniStream.AsSpan(offset, Math.Min(remaining, _miniStream.Length - offset)));
            }
            sector = fat[sector];
        }
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    private static uint[] ToUInts(byte[] bytes)
    {
        var values = new uint[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        return values;
    }

    private List<DirectoryEntry> ReadDirectory(uint start)
    {
        var bytes = ReadChain(start, long.MaxValue, _fat, regular: true);
        var entries = new List<DirectoryEntry>(bytes.Length / DirectoryEntrySize);
        for (var offset = 0; offset + DirectoryEntrySize <= bytes.Length; offset += DirectoryEntrySize)
        {
            var e = bytes.AsSpan(offset, DirectoryEntrySize);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[0x40..]), 2, 64);
            var name = Encoding.Unicode.GetString(e[..(nameLength - 2)]);
            entries.Add(new DirectoryEntry(
                name,
                e[0x42],
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x48..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x4C..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x74..]),
                // Version 3 files keep only the low 32 bits of the size; the high ones may hold garbage.
                _sectorSize == 512 ? BinaryPrimitives.ReadUInt32LittleEndian(e[0x78..]) : (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(e[0x78..]), int.MaxValue)));
        }
        return entries;
    }

    /// <summary>The children of a storage, by name (case-insensitive, as in CFB).</summary>
    internal Dictionary<string, DirectoryEntry> Children(DirectoryEntry storage)
    {
        var children = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        if (storage.Child >= Entries.Count) return children;
        // The children form a red-black tree through the sibling links; it is walked with a stack, and an entry seen
        // twice (a cycle in a damaged file) is not followed again.
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>();
        stack.Push(storage.Child);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id >= Entries.Count || !seen.Add(id)) continue;
            var entry = Entries[(int)id];
            if (entry.Type is 1 or 2) children.TryAdd(entry.Name, entry);
            stack.Push(entry.Left);
            stack.Push(entry.Right);
        }
        return children;
    }

    /// <summary>The content of a stream.</summary>
    internal byte[] Read(DirectoryEntry stream)
    {
        if (stream.Type != 2 || stream.Size <= 0) return [];
        return stream.Size < _miniCutoff
            ? ReadChain(stream.Start, stream.Size, _miniFat, regular: false)
            : ReadChain(stream.Start, stream.Size, _fat, regular: true);
    }
}

/// <summary>A directory entry: type 1 is a storage, 2 a stream, 5 the root.</summary>
internal sealed record DirectoryEntry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Size);
