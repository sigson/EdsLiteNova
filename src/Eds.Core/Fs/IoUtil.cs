namespace Eds.Core.Fs;

/// <summary>
/// Little-endian read/write helpers over <see cref="IRandomAccessIO"/>, mirroring
/// the subset of <c>fs.util.Util</c> used by the FAT driver. FAT structures are
/// little-endian on disk.
/// </summary>
public static class IoUtil
{
    public static int ReadUnsignedByte(IRandomAccessIO io)
    {
        int b = io.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return b & 0xff;
    }

    public static int ReadWordLE(IRandomAccessIO io)
    {
        int b0 = ReadUnsignedByte(io);
        int b1 = ReadUnsignedByte(io);
        return b0 | (b1 << 8);
    }

    public static long ReadDoubleWordLE(IRandomAccessIO io)
    {
        long b0 = ReadUnsignedByte(io);
        long b1 = ReadUnsignedByte(io);
        long b2 = ReadUnsignedByte(io);
        long b3 = ReadUnsignedByte(io);
        return (b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)) & 0xFFFFFFFFL;
    }

    public static void WriteWordLE(IRandomAccessIO io, int value)
    {
        io.WriteByte(value & 0xff);
        io.WriteByte((value >> 8) & 0xff);
    }

    public static void WriteDoubleWordLE(IRandomAccessIO io, long value)
    {
        io.WriteByte((int)(value & 0xff));
        io.WriteByte((int)((value >> 8) & 0xff));
        io.WriteByte((int)((value >> 16) & 0xff));
        io.WriteByte((int)((value >> 24) & 0xff));
    }

    public static int ReadBytes(IRandomAccessIO io, byte[] buffer, int count)
        => ReadBytes(io, buffer, 0, count);

    public static int ReadBytes(IRandomAccessIO io, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = io.Read(buffer, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public static ushort ReadWordLE(ReadOnlySpan<byte> b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

    public static uint ReadDoubleWordLE(ReadOnlySpan<byte> b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}
