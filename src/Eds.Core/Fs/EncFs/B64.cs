namespace Eds.Core.Fs.EncFs;

/// <summary>
/// EncFS-specific base-N re-coding used for filename encoding. Faithful port of
/// <c>fs.encfs.B64</c>. Note this is NOT standard base64: the alphabet is
/// ",-0123456789A-Za-z" (it deliberately avoids '/' — a path separator — and
/// '.', reserved for special files), so encrypted names are filesystem-safe.
///
/// Byte-for-byte compatibility with desktop EncFS is required, so the bit-level
/// <see cref="ChangeBase2Inline"/> transform and the alphabet mappings are ported
/// exactly. The reverse ASCII→value table is built programmatically from the
/// forward table to avoid transcription errors in the sparse literal.
/// </summary>
public static class B64
{
    public static int B256ToB64Bytes(int numB256Bytes) => (numB256Bytes * 8 + 5) / 6; // round up
    public static int B256ToB32Bytes(int numB256Bytes) => (numB256Bytes * 8 + 4) / 5; // round up
    public static int B64ToB256Bytes(int numB64Bytes) => numB64Bytes * 6 / 8;         // round down
    public static int B32ToB256Bytes(int numB32Bytes) => numB32Bytes * 5 / 8;         // round down

    public static void ChangeBase2Inline(byte[] src, int offset, int srcLen, int src2Pow, int dst2Pow,
        bool outputPartialLastByte)
        => ChangeBase2Inline(src, offset, srcLen, src2Pow, dst2Pow, outputPartialLastByte, 0, 0, null, 0);

    /// <summary>
    /// Re-codes a bit stream from base 2^src2Pow to base 2^dst2Pow in place (or to
    /// <paramref name="outLoc"/>). Recursive, exactly like the original.
    /// </summary>
    public static void ChangeBase2Inline(
        byte[] src, int offset, int srcLen, int src2Pow, int dst2Pow,
        bool outputPartialLastByte, long work, int workBits, byte[]? outLoc, int outOffset)
    {
        int mask = (1 << dst2Pow) - 1;
        if (outLoc == null)
        {
            outLoc = src;
            outOffset = offset;
        }

        // Copy new bits onto the high end; bits that fall off the low end are output.
        while (srcLen > 0 && workBits < dst2Pow)
        {
            work |= (src[offset++] & 0xFFL) << workBits;
            workBits += src2Pow;
            --srcLen;
        }

        byte outVal = (byte)(work & mask);
        work >>= dst2Pow;
        workBits -= dst2Pow;

        if (srcLen > 0)
        {
            ChangeBase2Inline(src, offset, srcLen, src2Pow, dst2Pow, outputPartialLastByte,
                work, workBits, outLoc, outOffset + 1);
            outLoc[outOffset] = outVal;
        }
        else
        {
            outLoc[outOffset++] = outVal;
            if (outputPartialLastByte)
            {
                while (workBits > 0)
                {
                    outLoc[outOffset++] = (byte)(work & mask);
                    work >>= dst2Pow;
                    workBits -= dst2Pow;
                }
            }
        }
    }

    // Forward value->char alphabet for the ",-0-9A-Za-z" ascii-b64.
    private static readonly char[] B642AsciiTable = ",-0123456789".ToCharArray();

    // Reverse char->value table for chars below 'A', built from the forward one.
    private static readonly byte[] Ascii2B64 = BuildAscii2B64();

    private static byte[] BuildAscii2B64()
    {
        var t = new byte[128];
        for (int v = 0; v < B642AsciiTable.Length; v++)
            t[B642AsciiTable[v]] = (byte)v;
        return t;
    }

    public static string B64ToString(byte[] input, int offset, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int cnt = 0; cnt < count; ++cnt)
        {
            int ch = input[offset + cnt];
            if (ch > 11)
            {
                if (ch > 37) ch += 'a' - 38;
                else ch += 'A' - 12;
            }
            else
                ch = B642AsciiTable[ch];
            sb.Append((char)ch);
        }
        return sb.ToString();
    }

    public static string B32ToString(byte[] buf, int offset, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int cnt = 0; cnt < count; ++cnt)
        {
            int ch = buf[offset + cnt];
            if (ch >= 0 && ch < 26) ch += 'A';
            else ch += '2' - 26;
            sb.Append((char)ch);
        }
        return sb.ToString();
    }

    public static byte[] StringToB32(string s)
    {
        var res = new byte[s.Length];
        int i = 0;
        foreach (char c in s)
        {
            int lch = char.ToUpperInvariant(c);
            if (lch >= 'A') lch -= 'A';
            else lch += 26 - '2';
            res[i++] = (byte)(lch & 0xFF);
        }
        return res;
    }

    public static byte[] StringToB64(string s)
    {
        var res = new byte[s.Length];
        int i = 0;
        foreach (char c in s)
        {
            int ch = c;
            if (ch >= 'A')
            {
                if (ch >= 'a') ch += 38 - 'a';
                else ch += 12 - 'A';
            }
            else
                ch = Ascii2B64[ch]; // equals original's (Ascii2B64Table[ch] - '0')
            res[i++] = (byte)(ch & 0xFF);
        }
        return res;
    }
}
