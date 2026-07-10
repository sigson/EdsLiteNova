/*
 * edscrypto.c - Implementation of the clean C-ABI shim.
 *
 * Includes the verbatim algorithm cores from vendor/ and exposes the
 * plain-C entry points declared in edscrypto.h. The block-cipher and mode
 * logic is unchanged from the original edslite native code; only the JNI
 * envelope has been removed.
 */

#include "edscrypto.h"

#include <stdlib.h>
#include <string.h>

/* Silence casts that exist in the original vendor sources. */
#if defined(__GNUC__)
#  pragma GCC diagnostic ignored "-Wint-to-pointer-cast"
#  pragma GCC diagnostic ignored "-Wpointer-to-int-cast"
#endif

/* --- common crypto headers (block_cipher_interface, endian helpers) ----- */
#include "block_cipher.h"
#include "xts.h"

/* --- vendored algorithm cores ------------------------------------------- */
#include "aes.h"
#include "serpent.h"
#include "twofish.h"
#include "gost89.h"
#include "ripemd160.h"
#include "whirlpool.h"

/* ======================================================================= */
/* AES                                                                     */
/* ======================================================================= */

typedef struct {
    aes_encrypt_ctx encrypt_context;
    aes_decrypt_ctx decrypt_context;
} aes_context;

static int aes_encrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    return aes_encrypt(in, out, &((aes_context*)context)->encrypt_context);
}
static int aes_decrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    return aes_decrypt(in, out, &((aes_context*)context)->decrypt_context);
}

EDS_API void* EDS_CALL eds_aes_init(const uint8_t* key, int32_t key_len) {
    block_cipher_interface* bci = malloc(sizeof(block_cipher_interface));
    if (!bci) return NULL;
    aes_context* ctx = malloc(sizeof(aes_context));
    if (!ctx) { free(bci); return NULL; }
    bci->encrypt = aes_encrypt_block_fn;
    bci->decrypt = aes_decrypt_block_fn;
    bci->context = ctx;
    aes_encrypt_key(key, key_len, &ctx->encrypt_context);
    aes_decrypt_key(key, key_len, &ctx->decrypt_context);
    return bci;
}
EDS_API void EDS_CALL eds_aes_encrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    aes_encrypt(block, block, &((aes_context*)bci->context)->encrypt_context);
}
EDS_API void EDS_CALL eds_aes_decrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    aes_decrypt(block, block, &((aes_context*)bci->context)->decrypt_context);
}
EDS_API void EDS_CALL eds_aes_close(void* ctx) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    if (bci) {
        if (bci->context) { memset(bci->context, 0, sizeof(aes_context)); free(bci->context); }
        free(bci);
    }
}

/* ======================================================================= */
/* Serpent                                                                 */
/* ======================================================================= */

#define SERPENT_SHEDULED_KEY_SIZE (140 * 4)

static int serpent_encrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    serpent_encrypt(in, out, (uint8_t*)context);
    return 0;
}
static int serpent_decrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    serpent_decrypt(in, out, (uint8_t*)context);
    return 0;
}

EDS_API void* EDS_CALL eds_serpent_init(const uint8_t* key, int32_t key_len) {
    block_cipher_interface* bci = malloc(sizeof(block_cipher_interface));
    if (!bci) return NULL;
    uint8_t* ctx = malloc(SERPENT_SHEDULED_KEY_SIZE);
    if (!ctx) { free(bci); return NULL; }
    bci->encrypt = serpent_encrypt_block_fn;
    bci->decrypt = serpent_decrypt_block_fn;
    bci->context = ctx;
    /* Original always schedules a 32-byte key. */
    (void)key_len;
    serpent_set_key(key, 32, ctx);
    return bci;
}
EDS_API void EDS_CALL eds_serpent_encrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    serpent_encrypt(block, block, (uint8_t*)bci->context);
}
EDS_API void EDS_CALL eds_serpent_decrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    serpent_decrypt(block, block, (uint8_t*)bci->context);
}
EDS_API void EDS_CALL eds_serpent_close(void* ctx) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    if (bci) {
        if (bci->context) { memset(bci->context, 0, SERPENT_SHEDULED_KEY_SIZE); free(bci->context); }
        free(bci);
    }
}

/* ======================================================================= */
/* Twofish                                                                 */
/* ======================================================================= */

static int twofish_encrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    twofish_encrypt((TwofishInstance*)context, (const u4byte*)in, (u4byte*)out);
    return 0;
}
static int twofish_decrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    twofish_decrypt((TwofishInstance*)context, (const u4byte*)in, (u4byte*)out);
    return 0;
}

EDS_API void* EDS_CALL eds_twofish_init(const uint8_t* key, int32_t key_len) {
    block_cipher_interface* bci = malloc(sizeof(block_cipher_interface));
    if (!bci) return NULL;
    TwofishInstance* ctx = malloc(sizeof(TwofishInstance));
    if (!ctx) { free(bci); return NULL; }
    bci->encrypt = twofish_encrypt_block_fn;
    bci->decrypt = twofish_decrypt_block_fn;
    bci->context = ctx;
    /* key_len is expressed in bits by the twofish core (256 for a 32-byte key). */
    twofish_set_key(ctx, (const u4byte*)key, key_len * 8);
    return bci;
}
EDS_API void EDS_CALL eds_twofish_encrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    twofish_encrypt_block_fn(block, block, bci->context);
}
EDS_API void EDS_CALL eds_twofish_decrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    twofish_decrypt_block_fn(block, block, bci->context);
}
EDS_API void EDS_CALL eds_twofish_close(void* ctx) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    if (bci) {
        if (bci->context) { memset(bci->context, 0, sizeof(TwofishInstance)); free(bci->context); }
        free(bci);
    }
}

/* ======================================================================= */
/* GOST 28147-89 (block size 8)                                            */
/* ======================================================================= */

static int gost_encrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    gostcrypt((gost_ctx*)context, in, out);
    return 0;
}
static int gost_decrypt_block_fn(const uint8_t* in, uint8_t* out, void* context) {
    gostdecrypt((gost_ctx*)context, in, out);
    return 0;
}

EDS_API void* EDS_CALL eds_gost_init(const uint8_t* key, int32_t key_len, int32_t use_test_sbox) {
    block_cipher_interface* bci = malloc(sizeof(block_cipher_interface));
    if (!bci) return NULL;
    gost_ctx* ctx = malloc(sizeof(gost_ctx));
    if (!ctx) { free(bci); return NULL; }
    (void)key_len;
    gost_init(ctx, use_test_sbox ? &GostR3411_94_TestParamSet : NULL);
    bci->encrypt = gost_encrypt_block_fn;
    bci->decrypt = gost_decrypt_block_fn;
    bci->context = ctx;
    gost_key(ctx, (const byte*)key);
    return bci;
}
EDS_API void EDS_CALL eds_gost_encrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    gost_encrypt_block_fn(block, block, bci->context);
}
EDS_API void EDS_CALL eds_gost_decrypt(void* ctx, uint8_t* block) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    gost_decrypt_block_fn(block, block, bci->context);
}
EDS_API void EDS_CALL eds_gost_close(void* ctx) {
    block_cipher_interface* bci = (block_cipher_interface*)ctx;
    if (bci) {
        if (bci->context) {
            gost_destroy((gost_ctx*)bci->context);
            memset(bci->context, 0, sizeof(gost_ctx));
            free(bci->context);
        }
        free(bci);
    }
}

/* ======================================================================= */
/* XTS mode. The sector logic (gf_mulx, xts_encrypt/decrypt) is included    */
/* verbatim from the original edsxts.c below.                               */
/* ======================================================================= */

/* xts_context, cipher_pair and XTS_SECTOR_SIZE come from xts.h. The .inc    */
/* file provides the static sector routines plus xts_encrypt / xts_decrypt. */
#include "xts_mode_impl.inc"

/* free_cipher_list() and attach_ciphers_to_tail() are provided (static) by the
 * included xts_mode_impl.inc, so we simply reuse them here. */

EDS_API void* EDS_CALL eds_xts_init(void) {
    xts_context* ctx = malloc(sizeof(xts_context));
    if (!ctx) return NULL;
    memset(ctx, 0, sizeof(xts_context));
    return ctx;
}
EDS_API void EDS_CALL eds_xts_attach(void* ctx, void* cipher_a, void* cipher_b) {
    attach_ciphers_to_tail((xts_context*)ctx,
                           (block_cipher_interface*)cipher_a,
                           (block_cipher_interface*)cipher_b);
}
EDS_API int32_t EDS_CALL eds_xts_encrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint64_t sector) {
    if (!ctx || !data) return -1;
    xts_encrypt((xts_context*)ctx, data, offset, len, sector);
    return 0;
}
EDS_API int32_t EDS_CALL eds_xts_decrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint64_t sector) {
    if (!ctx || !data) return -1;
    xts_decrypt((xts_context*)ctx, data, offset, len, sector);
    return 0;
}
EDS_API void EDS_CALL eds_xts_close(void* ctx) {
    if (ctx) {
        free_cipher_list(((xts_context*)ctx)->ciphers_head);
        free(ctx);
    }
}

/* ======================================================================= */
/* CBC mode (cascade of ciphers). Logic from the original edscbc.c.        */
/* ======================================================================= */

#include "cbc_mode_impl.inc"

EDS_API void* EDS_CALL eds_cbc_init(int32_t file_block_size) {
    cbc_context* ctx = malloc(sizeof(cbc_context));
    if (!ctx) return NULL;
    memset(ctx, 0, sizeof(cbc_context));
    ctx->file_block_size = (size_t)file_block_size;
    return ctx;
}
EDS_API void EDS_CALL eds_cbc_attach(void* ctx, void* cipher) {
    cbc_attach_ciphers_to_tail((cbc_context*)ctx, (block_cipher_interface*)cipher);
}
EDS_API int32_t EDS_CALL eds_cbc_encrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16, int32_t increment_iv) {
    if (!ctx) return -1;
    cbc_encrypt((cbc_context*)ctx, data, offset, len, iv16, (unsigned char)increment_iv);
    return 0;
}
EDS_API int32_t EDS_CALL eds_cbc_decrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16, int32_t increment_iv) {
    if (!ctx) return -1;
    cbc_decrypt((cbc_context*)ctx, data, offset, len, iv16, (unsigned char)increment_iv);
    return 0;
}
EDS_API void EDS_CALL eds_cbc_close(void* ctx) {
    if (ctx) {
        cbc_free_cipher_list(((cbc_context*)ctx)->ciphers_head);
        free(ctx);
    }
}

/* ======================================================================= */
/* CFB-128 mode (cascade of ciphers). Logic from the original edscfb.c.    */
/* ======================================================================= */

#include "cfb_mode_impl.inc"

EDS_API void* EDS_CALL eds_cfb_init(void) {
    cfb_context* ctx = malloc(sizeof(cfb_context));
    if (!ctx) return NULL;
    memset(ctx, 0, sizeof(cfb_context));
    return ctx;
}
EDS_API void EDS_CALL eds_cfb_attach(void* ctx, void* cipher) {
    cfb_attach_ciphers_to_tail((cfb_context*)ctx, (block_cipher_interface*)cipher);
}
EDS_API int32_t EDS_CALL eds_cfb_encrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16) {
    if (!ctx) return -1;
    cfb_encrypt((cfb_context*)ctx, data, offset, len, iv16);
    return 0;
}
EDS_API int32_t EDS_CALL eds_cfb_decrypt(void* ctx, uint8_t* data, int32_t offset, int32_t len, uint8_t* iv16) {
    if (!ctx) return -1;
    cfb_decrypt((cfb_context*)ctx, data, offset, len, iv16);
    return 0;
}
EDS_API void EDS_CALL eds_cfb_close(void* ctx) {
    if (ctx) {
        cfb_free_cipher_list(((cfb_context*)ctx)->ciphers_head);
        free(ctx);
    }
}

/* ======================================================================= */
/* RIPEMD-160                                                              */
/* ======================================================================= */

EDS_API void* EDS_CALL eds_ripemd160_init(void) {
    RMD160_CTX* ctx = malloc(sizeof(RMD160_CTX));
    if (ctx) rmd160_init(ctx);
    return ctx;
}
EDS_API void EDS_CALL eds_ripemd160_reset(void* ctx) {
    memset((RMD160_CTX*)ctx, 0, sizeof(RMD160_CTX));
    rmd160_init((RMD160_CTX*)ctx);
}
EDS_API void EDS_CALL eds_ripemd160_update(void* ctx, const uint8_t* data, int32_t offset, int32_t len) {
    rmd160_update((RMD160_CTX*)ctx, data + offset, (uint32_t)len);
}
EDS_API void EDS_CALL eds_ripemd160_final(void* ctx, uint8_t* out20) {
    rmd160_final(out20, (RMD160_CTX*)ctx);
}
EDS_API void EDS_CALL eds_ripemd160_free(void* ctx) {
    if (ctx) free(ctx);
}

/* ======================================================================= */
/* Whirlpool                                                               */
/* ======================================================================= */

EDS_API void* EDS_CALL eds_whirlpool_init(void) {
    WHIRLPOOL_CTX* ctx = malloc(sizeof(WHIRLPOOL_CTX));
    if (ctx) WHIRLPOOL_init(ctx);
    return ctx;
}
EDS_API void EDS_CALL eds_whirlpool_reset(void* ctx) {
    memset((WHIRLPOOL_CTX*)ctx, 0, sizeof(WHIRLPOOL_CTX));
    WHIRLPOOL_init((WHIRLPOOL_CTX*)ctx);
}
EDS_API void EDS_CALL eds_whirlpool_update(void* ctx, const uint8_t* data, int32_t offset, int32_t len) {
    WHIRLPOOL_add(data + offset, (uint32_t)len * 8, (WHIRLPOOL_CTX*)ctx);
}
EDS_API void EDS_CALL eds_whirlpool_final(void* ctx, uint8_t* out64) {
    WHIRLPOOL_finalize((WHIRLPOOL_CTX*)ctx, out64);
}
EDS_API void EDS_CALL eds_whirlpool_free(void* ctx) {
    if (ctx) free(ctx);
}

EDS_API int32_t EDS_CALL eds_crypto_version(void) {
    return EDS_CRYPTO_VERSION;
}

