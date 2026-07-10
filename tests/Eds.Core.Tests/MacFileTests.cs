using Eds.Core.Crypto;
using Eds.Core.Fs;
using Eds.Core.Fs.EncFs.Macs;
using Eds.Core.Fs.Util;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C: the buffered transforming IO layer (TransRandomAccessIO) and the
/// per-block MAC file (MacFile) used by EncFS. See gap guide §2.3, §4.2.
/// </summary>
public class MacFileTests
{
    private static byte[] MakeData(int n)
    {
        var d = new byte[n];
        for (int i = 0; i < n; i++) d[i] = (byte)(i * 97 + 13);
        return d;
    }

    private static int ReadFull(IRandomAccessIO io, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = io.Read(buf, total, buf.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    [Fact]
    public void Trans_Identity_WriteRead_RoundTrips()
    {
        using var baseIo = new MemoryRandomAccessIO(Array.Empty<byte>());
        using var trans = new TransRandomAccessIO(baseIo, 100);

        var data = MakeData(350); // spans multiple 100-byte buffers, last partial
        trans.Seek(0);
        trans.Write(data, 0, data.Length);

        trans.Seek(0);
        var read = new byte[350];
        Assert.Equal(350, ReadFull(trans, read));
        Assert.True(read.AsSpan().SequenceEqual(data));
    }

    private static Sha1MacCalculator NewMac()
    {
        var key = new byte[40];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 11 + 1);
        var mac = new Sha1MacCalculator(20);
        mac.Init(key);
        return mac;
    }

    [Fact]
    public void MacFile_Write_Then_Read_RoundTrips_AcrossBlocks()
    {
        var data = MakeData(150); // blockSize 64, overhead 8 -> 56-byte virtual blocks

        // write MAC-checked blocks into baseIo1 (keep it open)
        byte[] real;
        using (var baseIo1 = new MemoryRandomAccessIO(Array.Empty<byte>()))
        {
            var mf1 = new MacFile(baseIo1, NewMac(), blockSize: 64, macBytes: 8, randBytes: 0, forceDecode: false);
            mf1.Seek(0);
            mf1.Write(data, 0, data.Length);
            mf1.Close(closeBase: false); // flush blocks, keep base open, dispose its mac

            real = new byte[(int)baseIo1.Length()];
            baseIo1.Seek(0);
            ReadFull(baseIo1, real);
        }

        Assert.True(real.Length > data.Length); // MAC overhead present

        // read them back through a fresh MacFile and verify
        using var baseIo2 = new MemoryRandomAccessIO(real);
        using var mf2 = new MacFile(baseIo2, NewMac(), 64, 8, 0, false);
        Assert.Equal(data.Length, mf2.Length());
        var read = new byte[data.Length];
        mf2.Seek(0);
        Assert.Equal(data.Length, ReadFull(mf2, read));
        Assert.True(read.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void MacFile_DetectsTampering()
    {
        var data = MakeData(120);
        byte[] real;
        using (var baseIo1 = new MemoryRandomAccessIO(Array.Empty<byte>()))
        {
            var mf1 = new MacFile(baseIo1, NewMac(), 64, 8, 0, false);
            mf1.Seek(0);
            mf1.Write(data, 0, data.Length);
            mf1.Close(false);
            real = new byte[(int)baseIo1.Length()];
            baseIo1.Seek(0);
            ReadFull(baseIo1, real);
        }

        // flip a data byte in the first block (past its 8-byte MAC header)
        real[20] ^= 0xFF;

        using var baseIo2 = new MemoryRandomAccessIO(real);
        using var mf2 = new MacFile(baseIo2, NewMac(), 64, 8, 0, false);
        var read = new byte[data.Length];
        mf2.Seek(0);
        Assert.ThrowsAny<IOException>(() => ReadFull(mf2, read));
    }
}
