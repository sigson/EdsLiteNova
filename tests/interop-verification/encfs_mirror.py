"""Faithful Python mirror of the EdsLite C# EncFS decode path, exercised against
a REAL desktop-encfs volume. Uses the same libedscrypto primitives via ctypes.
If this decodes real encfs names+data correctly, the C# port's algorithm is
proven interop-correct (this code is a line-by-line transcription of it)."""
import hashlib, hmac, struct
from edsnative import AesCbc, AesCfb

# ---------------- MAC (Sha1MacCalculator) ----------------
def mac_checksum(hmac_key: bytes, data: bytes, chained_iv=None):
    """CalcChecksum: HMAC-SHA1 over (data [+ reversed chained_iv]), fold first 19 bytes -> 8."""
    if chained_iv is not None:
        data = data + bytes(chained_iv[7 - i] for i in range(8))
    mac = hmac.new(hmac_key, data, hashlib.sha1).digest()  # 20 bytes
    cut = bytearray(8)
    for i in range(len(mac) - 1):          # first 19 bytes only
        cut[i % 8] ^= mac[i]
    new_chained = bytes(cut) if chained_iv is not None else None
    return bytes(cut), new_chained

def mac_calc32(hmac_key, data, chained_iv=None):
    cs, nc = mac_checksum(hmac_key, data, chained_iv)
    cs = bytearray(cs)
    for i in range(4): cs[i] ^= cs[i + 4]
    return struct.unpack(">i", bytes(cs[:4]))[0], nc

def mac_calc16(hmac_key, data, chained_iv=None):
    cs, nc = mac_checksum(hmac_key, data, chained_iv)
    cs = bytearray(cs)
    for i in range(4): cs[i] ^= cs[i + 4]
    for i in range(2): cs[i] ^= cs[i + 2]
    return struct.unpack(">h", bytes(cs[:2]))[0], nc

def mac_calc64(hmac_key, data, chained_iv=None):
    cs, nc = mac_checksum(hmac_key, data, chained_iv)
    return struct.unpack(">q", cs)[0], nc


# ---------------- CipherBase IV derivation ----------------
def derive_base_iv(hmac_key_part: bytes, iv_part: bytes, file_iv8: bytes, base_iv_size: int) -> bytes:
    """CipherBase.SetIV: HMAC-SHA1(keyPart)(ivPart || reversed(file_iv8))[:baseIvSize]."""
    buf = bytes(iv_part) + bytes(file_iv8[7 - i] for i in range(8))
    h = hmac.new(hmac_key_part, buf, hashlib.sha1).digest()
    return h[:base_iv_size]


class CbcCipher:
    """AesCbcFileCipher wrapped in CipherBase: key = base_key(ks) || iv_part(16)."""
    def __init__(self, key: bytes, key_size: int, file_block_size: int):
        self.kp = key[:key_size]
        self.ivp = key[key_size:]           # length = IVSize = 16
        self.enc = AesCbc(self.kp, file_block_size)
    def decrypt_block(self, data: bytearray, offset, length, file_iv8):
        base_iv = derive_base_iv(self.kp, self.ivp, file_iv8, 16)
        self.enc.decrypt(data, offset, length, base_iv)
    def close(self): self.enc.close()


class StreamCipher:
    """AesCfbStreamCipher wrapped in StreamCipherBase over CipherBase."""
    def __init__(self, key: bytes, key_size: int):
        self.kp = key[:key_size]
        self.ivp = key[key_size:]
        self.enc = AesCfb(self.kp)
    def _set_base_iv(self, file_iv8):
        return derive_base_iv(self.kp, self.ivp, file_iv8, 16)
    def decrypt(self, data: bytearray, offset, length, iv_long):
        # inverse of StreamCipherBase.Encrypt
        self.enc.decrypt(data, offset, length, self._set_base_iv(struct.pack(">q", _s64(iv_long + 1))))
        _unshuffle(data, offset, length)
        _flip(data, offset, length)
        self.enc.decrypt(data, offset, length, self._set_base_iv(struct.pack(">q", _s64(iv_long))))
        _unshuffle(data, offset, length)
    def encrypt(self, data: bytearray, offset, length, iv_long):
        _shuffle(data, offset, length)
        self.enc.encrypt(data, offset, length, self._set_base_iv(struct.pack(">q", _s64(iv_long))))
        _flip(data, offset, length)
        _shuffle(data, offset, length)
        self.enc.encrypt(data, offset, length, self._set_base_iv(struct.pack(">q", _s64(iv_long + 1))))
    def close(self): self.enc.close()


def _s64(v):  # wrap to signed 64-bit like Java/C# long
    v &= (1 << 64) - 1
    return v - (1 << 64) if v >= (1 << 63) else v

def _shuffle(buf, off, n):
    for i in range(n - 1): buf[i + off + 1] ^= buf[i + off]
def _unshuffle(buf, off, n):
    for i in range(n - 1, 0, -1): buf[i + off] ^= buf[i + off - 1]
def _flip(buf, off, n):
    rev = bytearray(64); left = n; o = off
    while left > 0:
        k = min(64, left)
        for i in range(k): rev[i] = buf[k + o - (i + 1)]
        buf[o:o + k] = rev[:k]
        left -= k; o += k
