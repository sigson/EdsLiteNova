"""Mirror of C# Eds.Core.Fs.EncFs.B64 (EncFS filename base-N recoding)."""

def b64_to_b256_bytes(n): return n * 6 // 8
def b256_to_b64_bytes(n): return (n * 8 + 5) // 6

def change_base2_inline(src, offset, srclen, src2pow, dst2pow, output_partial, work=0, workbits=0, out=None, outoff=0):
    mask = (1 << dst2pow) - 1
    if out is None:
        out = src; outoff = offset
    while srclen > 0 and workbits < dst2pow:
        work |= (src[offset] & 0xFF) << workbits
        offset += 1; workbits += src2pow; srclen -= 1
    outval = work & mask
    work >>= dst2pow; workbits -= dst2pow
    if srclen > 0:
        change_base2_inline(src, offset, srclen, src2pow, dst2pow, output_partial, work, workbits, out, outoff + 1)
        out[outoff] = outval
    else:
        out[outoff] = outval; outoff += 1
        if output_partial:
            while workbits > 0:
                out[outoff] = work & mask; outoff += 1
                work >>= dst2pow; workbits -= dst2pow

_B642ASCII = ",-0123456789"

def string_to_b64(s):
    res = bytearray(len(s))
    for i, c in enumerate(s):
        ch = ord(c)
        if ch >= ord('A'):
            if ch >= ord('a'): ch += 38 - ord('a')
            else: ch += 12 - ord('A')
        else:
            ch = _B642ASCII.index(c)
        res[i] = ch & 0xFF
    return res

def b64_to_string(buf, offset, count):
    out = []
    for cnt in range(count):
        ch = buf[offset + cnt]
        if ch > 11:
            if ch > 37: ch += ord('a') - 38
            else: ch += ord('A') - 12
        else:
            ch = ord(_B642ASCII[ch])
        out.append(chr(ch))
    return "".join(out)
