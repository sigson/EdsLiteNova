import struct, hashlib, sys
from edsnative import AesXts

SECTOR = 512

def af_merge(src, key_len, stripes, hash_name):
    ds = hashlib.new(hash_name).digest_size
    block = bytearray(key_len)
    def diffuse(buf):
        out = bytearray(len(buf))
        blocks = len(buf) // ds
        pad = len(buf) % ds
        def hashbuf(off, length, iv):
            h = hashlib.new(hash_name)
            h.update(struct.pack(">i", iv))
            h.update(bytes(buf[off:off+length]))
            r = h.digest()
            out[off:off+min(len(r), len(buf)-off)] = r[:min(len(r), len(buf)-off)]
        for i in range(blocks): hashbuf(ds*i, ds, i)
        if pad > 0: hashbuf(ds*blocks, pad, blocks)
        return out
    for i in range(stripes - 1):
        for j in range(key_len): block[j] ^= src[i*key_len + j]
        block = diffuse(block)
    mk = bytearray(key_len)
    base = (stripes - 1) * key_len
    for j in range(key_len): mk[j] = src[base + j] ^ block[j]
    return bytes(mk)

def open_luks(path, password):
    hdr = open(path, "rb").read(1024)
    assert hdr[:6] == b"LUKS\xba\xbe", "not LUKS1"
    ver = struct.unpack(">h", hdr[6:8])[0]
    def cstr(off, n):
        b = hdr[off:off+n]; z = b.find(0)
        return b[:z if z >= 0 else n].decode("ascii").strip()
    cipher = cstr(8, 32); mode = cstr(40, 32); hspec = cstr(72, 32)
    payload_sec = struct.unpack(">i", hdr[104:108])[0]
    key_len = struct.unpack(">i", hdr[108:112])[0]
    mk_digest = hdr[112:132]
    mk_salt = hdr[132:164]
    mk_iter = struct.unpack(">i", hdr[164:168])[0]
    # hash name for pbkdf2/af: normalize
    hn = {"sha1":"sha1","sha256":"sha256","sha512":"sha512","ripemd160":"ripemd160","whirlpool":"whirlpool"}[hspec.lower()]
    print(f"cipher={cipher}-{mode} hash={hspec} payload_sec={payload_sec} keyLen={key_len} mkIter={mk_iter}")

    slots = []
    base = 208
    for i in range(8):
        o = base + i*48
        active = struct.unpack(">i", hdr[o:o+4])[0]
        iters = struct.unpack(">i", hdr[o+4:o+8])[0]
        salt = hdr[o+8:o+40]
        kmo = struct.unpack(">i", hdr[o+40:o+44])[0]
        stripes = struct.unpack(">i", hdr[o+44:o+48])[0]
        slots.append((active, iters, salt, kmo, stripes))

    with open(path, "rb") as f:
        for i,(active,iters,salt,kmo,stripes) in enumerate(slots):
            if active != 0x00AC71F3: continue
            derived = hashlib.pbkdf2_hmac(hn, password, salt, iters, key_len)
            af_sectors = (key_len*stripes + SECTOR - 1)//SECTOR
            af_size = af_sectors * SECTOR
            f.seek(kmo * SECTOR)
            af = bytearray(f.read(af_size))
            if mode.startswith("xts"):
                x = AesXts(derived)
                x.decrypt(af, 0, af_size, 0)   # increment IV from sector 0
                x.close()
            else:
                raise NotImplementedError("only xts tested here")
            mk = af_merge(af, key_len, stripes, hn)
            d = hashlib.pbkdf2_hmac(hn, mk, mk_salt, mk_iter, 20)
            if d == mk_digest:
                return i, mk
    return None, None

if __name__ == "__main__":
    path, pw, expected_mk_hex = sys.argv[1], sys.argv[2].encode(), sys.argv[3].replace(" ", "")
    slot, mk = open_luks(path, pw)
    if mk is None:
        print("FAILED to open"); sys.exit(1)
    got = mk.hex()
    print(f"opened slot {slot}")
    print(f"recovered MK: {got}")
    print(f"cryptsetup MK: {expected_mk_hex}")
    ok = got == expected_mk_hex
    print("RESULT:", "MASTER KEY MATCHES cryptsetup" if ok else "MISMATCH")
    sys.exit(0 if ok else 1)
