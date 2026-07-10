"""Thin ctypes bindings to the SAME libedscrypto the C# port P/Invokes.
Used to independently reproduce the port's EncFS/LUKS decode paths against
real desktop-tool artifacts (cryptsetup / encfs)."""
import ctypes, os

def _find_lib():
    env = os.environ.get("EDS_NATIVE_LIB")
    if env and os.path.exists(env):
        return env
    here = os.path.dirname(os.path.abspath(__file__))
    # committed layout: tests/interop-verification/ -> ../../native/build/libedscrypto.so
    for rel in ("../../native/build/libedscrypto.so",
                "../../native/build/libedscrypto.dylib",
                "native/build/libedscrypto.so"):
        p = os.path.normpath(os.path.join(here, rel))
        if os.path.exists(p):
            return p
    raise FileNotFoundError(
        "libedscrypto not built. Run scripts/build-native.sh (or set EDS_NATIVE_LIB).")

_LIB = ctypes.CDLL(_find_lib())

def _sig(name, restype, argtypes):
    f = getattr(_LIB, name); f.restype = restype; f.argtypes = argtypes; return f

_aes_init = _sig("eds_aes_init", ctypes.c_void_p, [ctypes.c_char_p, ctypes.c_int32])
_aes_enc  = _sig("eds_aes_encrypt", None, [ctypes.c_void_p, ctypes.c_char_p])
_aes_dec  = _sig("eds_aes_decrypt", None, [ctypes.c_void_p, ctypes.c_char_p])
_aes_close= _sig("eds_aes_close", None, [ctypes.c_void_p])

_cbc_init = _sig("eds_cbc_init", ctypes.c_void_p, [ctypes.c_int32])
_cbc_att  = _sig("eds_cbc_attach", None, [ctypes.c_void_p, ctypes.c_void_p])
_cbc_enc  = _sig("eds_cbc_encrypt", ctypes.c_int32, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_char_p, ctypes.c_int32])
_cbc_dec  = _sig("eds_cbc_decrypt", ctypes.c_int32, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_char_p, ctypes.c_int32])
_cbc_close= _sig("eds_cbc_close", None, [ctypes.c_void_p])

_cfb_init = _sig("eds_cfb_init", ctypes.c_void_p, [])
_cfb_att  = _sig("eds_cfb_attach", None, [ctypes.c_void_p, ctypes.c_void_p])
_cfb_enc  = _sig("eds_cfb_encrypt", ctypes.c_int32, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_char_p])
_cfb_dec  = _sig("eds_cfb_decrypt", ctypes.c_int32, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_char_p])
_cfb_close= _sig("eds_cfb_close", None, [ctypes.c_void_p])

_xts_init = _sig("eds_xts_init", ctypes.c_void_p, [])
_xts_att  = _sig("eds_xts_attach", None, [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p])
_xts_enc  = _sig("eds_xts_encrypt", ctypes.c_int32, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_uint64])
_xts_dec  = _sig("eds_xts_decrypt", ctypes.c_int32, [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_uint64])
_xts_close= _sig("eds_xts_close", None, [ctypes.c_void_p])


class AesCbc:
    """Mirror of managed AesCbc(keySize, fileBlockSize): one AES cipher in CBC."""
    def __init__(self, key: bytes, file_block_size: int):
        self.cip = _aes_init(key, len(key))
        self.ctx = _cbc_init(file_block_size)
        _cbc_att(self.ctx, self.cip)
    def decrypt(self, data: bytearray, offset: int, length: int, iv16: bytes):
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        ivb = ctypes.create_string_buffer(bytes(iv16), 16)
        rc = _cbc_dec(self.ctx, buf, offset, length, ivb, 0)
        if rc != 0: raise RuntimeError("cbc dec failed")
        data[:] = buf.raw[:len(data)]
    def encrypt(self, data: bytearray, offset: int, length: int, iv16: bytes):
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        ivb = ctypes.create_string_buffer(bytes(iv16), 16)
        rc = _cbc_enc(self.ctx, buf, offset, length, ivb, 0)
        if rc != 0: raise RuntimeError("cbc enc failed")
        data[:] = buf.raw[:len(data)]
    def close(self):
        _cbc_close(self.ctx); _aes_close(self.cip)


class AesCfb:
    def __init__(self, key: bytes):
        self.cip = _aes_init(key, len(key))
        self.ctx = _cfb_init()
        _cfb_att(self.ctx, self.cip)
    def decrypt(self, data: bytearray, offset: int, length: int, iv16: bytes):
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        ivb = ctypes.create_string_buffer(bytes(iv16), 16)
        rc = _cfb_dec(self.ctx, buf, offset, length, ivb)
        if rc != 0: raise RuntimeError("cfb dec failed")
        data[:] = buf.raw[:len(data)]
    def encrypt(self, data: bytearray, offset: int, length: int, iv16: bytes):
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        ivb = ctypes.create_string_buffer(bytes(iv16), 16)
        rc = _cfb_enc(self.ctx, buf, offset, length, ivb)
        if rc != 0: raise RuntimeError("cfb enc failed")
        data[:] = buf.raw[:len(data)]
    def close(self):
        _cfb_close(self.ctx); _aes_close(self.cip)


class AesXts:
    """key = data-key||tweak-key concatenation (each keySize/2)."""
    def __init__(self, key: bytes):
        half = len(key) // 2
        self.a = _aes_init(key[:half], half)
        self.b = _aes_init(key[half:], half)
        self.ctx = _xts_init()
        _xts_att(self.ctx, self.a, self.b)
    def decrypt(self, data: bytearray, offset: int, length: int, sector: int):
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        rc = _xts_dec(self.ctx, buf, offset, length, sector)
        if rc != 0: raise RuntimeError("xts dec failed")
        data[:] = buf.raw[:len(data)]
    def encrypt(self, data: bytearray, offset: int, length: int, sector: int):
        buf = ctypes.create_string_buffer(bytes(data), len(data))
        rc = _xts_enc(self.ctx, buf, offset, length, sector)
        if rc != 0: raise RuntimeError("xts enc failed")
        data[:] = buf.raw[:len(data)]
    def close(self):
        _xts_close(self.ctx); _aes_close(self.a); _aes_close(self.b)
