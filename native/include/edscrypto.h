/*
 * edscrypto.h - Clean C-ABI shim over the EDS Lite native crypto cores.
 *
 * This header replaces the original JNI wrappers (Java_com_sovworks_...).
 * Every function uses a plain C ABI so it can be called from .NET via
 * [LibraryImport]/[DllImport]. No JNIEnv, no jobject, no jarray.
 *
 * Contexts are opaque pointers (void*), surfaced to C# as nint/IntPtr and
 * wrapped in SafeHandle subclasses on the managed side.
 *
 * Buffers are raw uint8_t*; the managed marshaller pins byte[]/Span<byte>
 * for the duration of the call (equivalent to JNI GetPrimitiveArrayCritical).
 *
 * The underlying algorithm cores (aescrypt.c, serpent.c, twofish.c, gost89.c,
 * ripemd160.c, whirlpool.c and the XTS mode logic) are copied verbatim from
 * the original project so that the ciphertext/digests remain byte-for-byte
 * identical - a hard requirement for opening existing TrueCrypt/VeraCrypt/LUKS
 * containers (see port guide section 9.4).
 *
 * License: GPLv2+ (inherited from edslite).
 */
#ifndef EDSCRYPTO_H
#define EDSCRYPTO_H

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
#  define EDS_API __declspec(dllexport)
#  define EDS_CALL __cdecl
#else
#  define EDS_API __attribute__((visibility("default")))
#  define EDS_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ----------------------------------------------------------------------- */
/* Block ciphers                                                           */
/* Each *_init returns a block_cipher_interface* (as void*) or NULL.        */
/* encrypt/decrypt operate in-place on exactly one block.                   */
/* Block sizes: AES/Serpent/Twofish = 16, GOST = 8.                         */
/* ----------------------------------------------------------------------- */

EDS_API void* EDS_CALL eds_aes_init(const uint8_t* key, int32_t key_len);
EDS_API void  EDS_CALL eds_aes_encrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_aes_decrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_aes_close(void* ctx);

EDS_API void* EDS_CALL eds_serpent_init(const uint8_t* key, int32_t key_len);
EDS_API void  EDS_CALL eds_serpent_encrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_serpent_decrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_serpent_close(void* ctx);

EDS_API void* EDS_CALL eds_twofish_init(const uint8_t* key, int32_t key_len);
EDS_API void  EDS_CALL eds_twofish_encrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_twofish_decrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_twofish_close(void* ctx);

/* use_test_sbox != 0 selects GostR3411_94_TestParamSet (matches the original
 * _useTestSubstMask field). Standard containers use 0. */
EDS_API void* EDS_CALL eds_gost_init(const uint8_t* key, int32_t key_len, int32_t use_test_sbox);
EDS_API void  EDS_CALL eds_gost_encrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_gost_decrypt(void* ctx, uint8_t* block);
EDS_API void  EDS_CALL eds_gost_close(void* ctx);

/* ----------------------------------------------------------------------- */
/* XTS mode (xts-plain64). Sector size 512, crypto block 16.               */
/* Attach one or more cipher pairs (data cipher A, tweak cipher B).         */
/* encrypt/decrypt process the buffer sector-by-sector starting at `sector`.*/
/* Return 0 on success, non-zero on error.                                  */
/* ----------------------------------------------------------------------- */

EDS_API void*   EDS_CALL eds_xts_init(void);
EDS_API void    EDS_CALL eds_xts_attach(void* ctx, void* cipher_a, void* cipher_b);
EDS_API int32_t EDS_CALL eds_xts_encrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint64_t sector);
EDS_API int32_t EDS_CALL eds_xts_decrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint64_t sector);
EDS_API void    EDS_CALL eds_xts_close(void* ctx);

/* ----------------------------------------------------------------------- */
/* CBC mode. Crypto block 16. Attach one or more ciphers (cascade).         */
/* `iv` is a 16-byte in/out buffer. When increment_iv != 0 the IV advances  */
/* as a little-endian counter every file_block_size bytes (LUKS plain/plain64).*/
/* file_block_size is fixed per context at init. Return 0 on success.       */
/* ----------------------------------------------------------------------- */

EDS_API void*   EDS_CALL eds_cbc_init(int32_t file_block_size);
EDS_API void    EDS_CALL eds_cbc_attach(void* ctx, void* cipher);
EDS_API int32_t EDS_CALL eds_cbc_encrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16, int32_t increment_iv);
EDS_API int32_t EDS_CALL eds_cbc_decrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16, int32_t increment_iv);
EDS_API void    EDS_CALL eds_cbc_close(void* ctx);

/* ----------------------------------------------------------------------- */
/* CFB-128 mode (full-block feedback, segment size 128 bits). Crypto block  */
/* 16. Attach one or more ciphers (cascade). `iv` is a 16-byte in/out buffer */
/* holding the running feedback. Length need not be block-aligned (the final */
/* partial segment is handled). Return 0 on success. Used by EncFS           */
/* AESCFBStreamCipher (file-name and stream-tail encryption).                */
/* ----------------------------------------------------------------------- */

EDS_API void*   EDS_CALL eds_cfb_init(void);
EDS_API void    EDS_CALL eds_cfb_attach(void* ctx, void* cipher);
EDS_API int32_t EDS_CALL eds_cfb_encrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16);
EDS_API int32_t EDS_CALL eds_cfb_decrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16);
EDS_API void    EDS_CALL eds_cfb_close(void* ctx);

/* ----------------------------------------------------------------------- */
/* Hash functions not present in the .NET BCL (RIPEMD-160, Whirlpool).      */
/* Streaming API mirrors java.security.MessageDigest.                       */
/* Digest sizes: RIPEMD160 = 20, Whirlpool = 64.                            */
/* ----------------------------------------------------------------------- */

EDS_API void* EDS_CALL eds_ripemd160_init(void);
EDS_API void  EDS_CALL eds_ripemd160_reset(void* ctx);
EDS_API void  EDS_CALL eds_ripemd160_update(void* ctx, const uint8_t* data, int32_t offset, int32_t len);
EDS_API void  EDS_CALL eds_ripemd160_final(void* ctx, uint8_t* out20);
EDS_API void  EDS_CALL eds_ripemd160_free(void* ctx);

EDS_API void* EDS_CALL eds_whirlpool_init(void);
EDS_API void  EDS_CALL eds_whirlpool_reset(void* ctx);
EDS_API void  EDS_CALL eds_whirlpool_update(void* ctx, const uint8_t* data, int32_t offset, int32_t len);
EDS_API void  EDS_CALL eds_whirlpool_final(void* ctx, uint8_t* out64);
EDS_API void  EDS_CALL eds_whirlpool_free(void* ctx);

/* Library version for compatibility checks from managed code. */
EDS_API int32_t EDS_CALL eds_crypto_version(void);
#define EDS_CRYPTO_VERSION 3

#ifdef __cplusplus
}
#endif

#endif /* EDSCRYPTO_H */
