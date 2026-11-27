using System.Buffers.Binary;
using System.Text;
using VMCrate.Core;

namespace VMCrate.Windows;

internal sealed class Iso9660
{
    private const int Sector = 2048;
    private readonly Stream image;
    private readonly Record root;
    private readonly Encoding encoding;
    private readonly uint bootCatalog;

    internal Iso9660(Stream image)
    {
        this.image = image;
        Record? primary = null;
        Record? joliet = null;
        for (int sector = 16; sector < 64; sector++)
        {
            var bytes = Read((long)sector * Sector, Sector);
            if (!bytes.AsSpan(1, 5).SequenceEqual("CD001"u8) || bytes[6] != 1)
                throw Invalid();
            if (bytes[0] == 255)
                break;
            if (bytes[0] == 1)
                primary = ReadRecord(bytes.AsSpan(156));
            else if (bytes[0] == 2 && bytes.AsSpan(88, 2).SequenceEqual("%/"u8) && bytes[90] is 64 or 67 or 69)
                joliet = ReadRecord(bytes.AsSpan(156));
            else if (bytes[0] == 0 && bytes.AsSpan(7, 23).SequenceEqual("EL TORITO SPECIFICATION"u8))
                bootCatalog = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(71));
        }
        encoding = joliet is null ? Encoding.ASCII : Encoding.BigEndianUnicode;
        root = joliet ?? primary ?? throw Invalid();
    }

    internal void RequireFile(string path) => _ = ResolveFile(path);

    internal byte[] ReadFile(string path, int limit)
    {
        var file = ResolveFile(path);
        if (file.Length > limit)
            throw Invalid();
        return Read(file.Offset, file.Length);
    }

    internal bool HasEfiDefaultEntry()
    {
        if (bootCatalog == 0)
            return false;
        var catalog = Read((long)bootCatalog * Sector, Sector);
        var bootOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(catalog.AsSpan(40)) * Sector;
        var bootLength = BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(38)) * 512L;
        return catalog[0] == 1 && catalog[1] == 0xef && catalog[30] == 0x55 && catalog[31] == 0xaa &&
            catalog[32] == 0x88 && catalog[33] == 0 && bootLength > 0 && bootOffset <= image.Length - bootLength;
    }

    private Record ResolveFile(string path)
    {
        var entry = root;
        foreach (var part in path.Split('/'))
            entry = FindInDirectory(entry, part);
        if (entry.Directory || entry.Length == 0 || entry.Offset > image.Length - entry.Length)
            throw Invalid();
        return entry;
    }

    private Record FindInDirectory(Record folder, string part)
    {
        if (!folder.Directory || folder.Length > 8 * 1024 * 1024)
            throw Invalid();
        var bytes = Read(folder.Offset, folder.Length);
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = bytes[offset];
            if (length == 0)
            {
                offset = (offset / Sector + 1) * Sector;
                continue;
            }
            if (length < 34 || length > bytes.Length - offset || length > Sector - offset % Sector)
                throw Invalid();
            var row = bytes.AsSpan(offset, length);
            var size = row[32];
            if (size == 0 || size > length - 33)
                throw Invalid();
            if (size > 1 || row[33] > 1)
            {
                var name = encoding.GetString(row.Slice(33, size)).Split(';', 2)[0].TrimEnd('.');
                if (name.Equals(part, StringComparison.OrdinalIgnoreCase))
                    return ReadRecord(row);
            }
            offset += length;
        }
        throw Invalid();
    }

    private static Record ReadRecord(ReadOnlySpan<byte> row)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(row[10..]);
        if (length > int.MaxValue || row[1] != 0 || (row[25] & 128) != 0)
            throw Invalid();
        return new((long)BinaryPrimitives.ReadUInt32LittleEndian(row[2..]) * Sector, (int)length, (row[25] & 2) != 0);
    }

    private byte[] Read(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > image.Length - length)
            throw Invalid();
        var bytes = new byte[length];
        image.Position = offset;
        image.ReadExactly(bytes);
        return bytes;
    }

    private static LabException Invalid() => new("error.kaliLayout");
    private sealed record Record(long Offset, int Length, bool Directory);
}
