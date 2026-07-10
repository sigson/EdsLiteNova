/*
 * kat_test.c - Known-Answer-Tests for the edscrypto native shim.
 *
 * Verifies that the C-ABI shim produces correct, byte-for-byte output:
 *   - AES-128 / AES-256 single-block encrypt+decrypt (FIPS-197 vectors)
 *   - RIPEMD-160 digests (RFC-quality reference vectors)
 *   - Whirlpool digests (ISO/NESSIE reference vectors)
 *   - Serpent / Twofish / GOST single-block round-trips
 *   - AES-XTS multi-sector round-trip + tweak activity check
 *
 * These prove the algorithm cores survived the JNI-removal intact. Full
 * container-level compatibility is verified separately on the managed side
 * with real reference containers.
 */
#include "edscrypto.h"
#include <stdio.h>
#include <string.h>

static int g_failures = 0;

static void hex(char* dst, const uint8_t* src, int n) {
    static const char* H = "0123456789abcdef";
    for (int i = 0; i < n; i++) { dst[i*2] = H[src[i]>>4]; dst[i*2+1] = H[src[i]&0xf]; }
    dst[n*2] = 0;
}

static int from_hex(uint8_t* dst, const char* s) {
    int n = 0;
    while (s[0] && s[1]) {
        int hi = (s[0]<='9')?s[0]-'0':(s[0]|0x20)-'a'+10;
        int lo = (s[1]<='9')?s[1]-'0':(s[1]|0x20)-'a'+10;
        dst[n++] = (uint8_t)((hi<<4)|lo);
        s += 2;
    }
    return n;
}

static void check(const char* name, const uint8_t* got, const char* expect_hex, int n) {
    char gh[256]; hex(gh, got, n);
    if (strcasecmp(gh, expect_hex) == 0) {
        printf("  [ OK ] %s\n", name);
    } else {
        printf("  [FAIL] %s\n         got: %s\n         exp: %s\n", name, gh, expect_hex);
        g_failures++;
    }
}

static void check_bool(const char* name, int ok) {
    if (ok) printf("  [ OK ] %s\n", name);
    else { printf("  [FAIL] %s\n", name); g_failures++; }
}

/* ------------------------------------------------------------------ AES */
static void test_aes(void) {
    printf("AES (FIPS-197):\n");
    /* AES-128 */
    uint8_t key128[16], pt[16], expect[16], blk[16];
    from_hex(key128, "000102030405060708090a0b0c0d0e0f");
    from_hex(pt,     "00112233445566778899aabbccddeeff");
    memcpy(blk, pt, 16);
    void* c = eds_aes_init(key128, 16);
    eds_aes_encrypt(c, blk);
    check("AES-128 encrypt", blk, "69c4e0d86a7b0430d8cdb78070b4c55a", 16);
    eds_aes_decrypt(c, blk);
    check("AES-128 decrypt round-trip", blk, "00112233445566778899aabbccddeeff", 16);
    eds_aes_close(c);

    /* AES-256 */
    uint8_t key256[32];
    from_hex(key256, "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    memcpy(blk, pt, 16);
    c = eds_aes_init(key256, 32);
    eds_aes_encrypt(c, blk);
    check("AES-256 encrypt", blk, "8ea2b7ca516745bfeafc49904b496089", 16);
    eds_aes_decrypt(c, blk);
    check("AES-256 decrypt round-trip", blk, "00112233445566778899aabbccddeeff", 16);
    eds_aes_close(c);
    (void)expect;
}

/* --------------------------------------------------------------- RIPEMD */
static void ripemd(const char* msg, char* out_hex) {
    uint8_t d[20];
    void* c = eds_ripemd160_init();
    eds_ripemd160_update(c, (const uint8_t*)msg, 0, (int)strlen(msg));
    eds_ripemd160_final(c, d);
    eds_ripemd160_free(c);
    hex(out_hex, d, 20);
}
static void test_ripemd160(void) {
    printf("RIPEMD-160:\n");
    char h[64];
    ripemd("", h);               check_bool("RIPEMD160(\"\")",              strcasecmp(h,"9c1185a5c5e9fc54612808977ee8f548b2258d31")==0);
    ripemd("abc", h);            check_bool("RIPEMD160(\"abc\")",           strcasecmp(h,"8eb208f7e05d987a9b044a8e98c6b087f15a0bfc")==0);
    ripemd("message digest", h); check_bool("RIPEMD160(\"message digest\")",strcasecmp(h,"5d0689ef49d2fae572b881b123a85ffa21595f36")==0);
}

/* ------------------------------------------------------------- Whirlpool */
static void whirl(const char* msg, char* out_hex) {
    uint8_t d[64];
    void* c = eds_whirlpool_init();
    eds_whirlpool_update(c, (const uint8_t*)msg, 0, (int)strlen(msg));
    eds_whirlpool_final(c, d);
    eds_whirlpool_free(c);
    hex(out_hex, d, 64);
}
static void test_whirlpool(void) {
    printf("Whirlpool:\n");
    char h[160];
    whirl("", h);
    check_bool("Whirlpool(\"\")",
        strcasecmp(h,"19fa61d75522a4669b44e39c1d2e1726c530232130d407f89afee0964997f7a73e83be698b288febcf88e3e03c4f0757ea8964e59b63d93708b138cc42a66eb3")==0);
    whirl("abc", h);
    check_bool("Whirlpool(\"abc\")",
        strcasecmp(h,"4e2448a4c6f486bb16b6562c73b4020bf3043e3a731bce721ae1b303d97e6d4c7181eebdb6c57e277d0e34957114cbd6c797fc9d95d8b582d225292076d4eef5")==0);
}

/* ------------------------------------------------ block-cipher roundtrips */
static void test_roundtrip(const char* name,
                           void* (*init)(const uint8_t*, int32_t),
                           void (*enc)(void*, uint8_t*),
                           void (*dec)(void*, uint8_t*),
                           void (*close)(void*),
                           int blocksize) {
    uint8_t key[32], blk[16], orig[16];
    for (int i = 0; i < 32; i++) key[i] = (uint8_t)(i*7+1);
    for (int i = 0; i < blocksize; i++) blk[i] = orig[i] = (uint8_t)(i*3+5);
    void* c = init(key, 32);
    enc(c, blk);
    int changed = memcmp(blk, orig, blocksize) != 0;
    dec(c, blk);
    int restored = memcmp(blk, orig, blocksize) == 0;
    close(c);
    check_bool(name, changed && restored);
}

/* GOST needs its extra sbox arg; wrap it. */
static void* gost_init_std(const uint8_t* k, int32_t n) { return eds_gost_init(k, n, 0); }

static void test_block_ciphers(void) {
    printf("Block-cipher round-trips:\n");
    test_roundtrip("Serpent enc/dec", eds_serpent_init, eds_serpent_encrypt, eds_serpent_decrypt, eds_serpent_close, 16);
    test_roundtrip("Twofish enc/dec", eds_twofish_init, eds_twofish_encrypt, eds_twofish_decrypt, eds_twofish_close, 16);
    test_roundtrip("GOST enc/dec",    gost_init_std,    eds_gost_encrypt,    eds_gost_decrypt,    eds_gost_close,    8);
}

/* ----------------------------------------------------------------- XTS */
static void test_xts(void) {
    printf("AES-XTS (xts-plain64):\n");
    /* XTS uses a 64-byte key = 2x32 (data cipher + tweak cipher). */
    uint8_t key1[32], key2[32];
    for (int i = 0; i < 32; i++) { key1[i] = (uint8_t)(i+1); key2[i] = (uint8_t)(0xff-i); }

    /* Build XTS context with an AES pair. */
    void* xts = eds_xts_init();
    void* a = eds_aes_init(key1, 32);
    void* b = eds_aes_init(key2, 32);
    eds_xts_attach(xts, a, b);

    /* Two full 512-byte sectors of data. */
    uint8_t buf[1024], orig[1024];
    for (int i = 0; i < 1024; i++) buf[i] = orig[i] = (uint8_t)(i & 0xff);

    eds_xts_encrypt(xts, buf, 0, 1024, 0);
    check_bool("XTS ciphertext differs from plaintext", memcmp(buf, orig, 1024) != 0);
    /* sector 0 and sector 1 must differ even though plaintext repeats each 256 bytes */
    check_bool("XTS tweak active (sector 0 != sector 1)", memcmp(buf, buf+512, 512) != 0);

    eds_xts_decrypt(xts, buf, 0, 1024, 0);
    check_bool("XTS decrypt round-trip", memcmp(buf, orig, 1024) == 0);

    /* Partial (non-sector-multiple) length still round-trips. */
    for (int i = 0; i < 1024; i++) buf[i] = orig[i] = (uint8_t)(i*5 & 0xff);
    eds_xts_encrypt(xts, buf, 16, 700, 42);
    eds_xts_decrypt(xts, buf, 16, 700, 42);
    check_bool("XTS partial-length round-trip @offset", memcmp(buf, orig, 1024) == 0);

    eds_xts_close(xts); /* frees the cipher pair list; ciphers a,b owned by list */
}

static void test_xts_vectors(void) {
    /* IEEE 1619 XTS-AES-128 published vectors: proves standard-compliance and
       therefore byte-compatibility with TrueCrypt/VeraCrypt/LUKS XTS. */
    printf("AES-XTS (IEEE 1619 published vectors):\n");
    uint8_t k1[16], k2[16], buf[32];

    /* Vector 1: zero keys, zero PTX, tweak 0 */
    memset(k1, 0, 16); memset(k2, 0, 16); memset(buf, 0, 32);
    {
        void* xts = eds_xts_init();
        eds_xts_attach(xts, eds_aes_init(k1, 16), eds_aes_init(k2, 16));
        eds_xts_encrypt(xts, buf, 0, 32, 0);
        check("XTS-AES-128 vector 1", buf,
              "917cf69ebd68b2ec9b9fe9a3eadda692cd43d2f59598ed858c02c2652fbf922e", 32);
        eds_xts_close(xts);
    }
    /* Vector 2: key1=0x11.., key2=0x22.., seq=0x3333333333, PTX=0x44.. */
    from_hex(k1, "11111111111111111111111111111111");
    from_hex(k2, "22222222222222222222222222222222");
    from_hex(buf, "4444444444444444444444444444444444444444444444444444444444444444");
    {
        void* xts = eds_xts_init();
        eds_xts_attach(xts, eds_aes_init(k1, 16), eds_aes_init(k2, 16));
        eds_xts_encrypt(xts, buf, 0, 32, 0x3333333333ULL);
        check("XTS-AES-128 vector 2", buf,
              "c454185e6a16936e39334038acef838bfb186fff7480adc4289382ecd6d394f0", 32);
        eds_xts_close(xts);
    }
}

static void test_cbc(void) {
    printf("AES-128-CBC (NIST SP 800-38A):\n");
    uint8_t key[16], iv[16], pt[64], ct[64], expect[64];
    from_hex(key, "2b7e151628aed2a6abf7158809cf4f3c");
    from_hex(pt,
        "6bc1bee22e409f96e93d7e117393172a"
        "ae2d8a571e03ac9c9eb76fac45af8e51"
        "30c81c46a35ce411e5fbc1191a0a52ef"
        "f69f2445df4f9b17ad2b417be66c3710");
    from_hex(expect,
        "7649abac8119b246cee98e9b12e9197d"
        "5086cb9b507219ee95db113a917678b2"
        "73bed6b8e3c1743b7116e69e22229516"
        "3ff1caa1681fac09120eca307586e1a7");

    /* encrypt: single sector, no IV increment -> textbook CBC over the buffer */
    void* cbc = eds_cbc_init(64);
    void* c = eds_aes_init(key, 16);
    eds_cbc_attach(cbc, c);
    memcpy(ct, pt, 64);
    from_hex(iv, "000102030405060708090a0b0c0d0e0f");
    eds_cbc_encrypt(cbc, ct, 0, 64, iv, 0);
    check("CBC encrypt (4 blocks)", ct, /* compare */
          "7649abac8119b246cee98e9b12e9197d"
          "5086cb9b507219ee95db113a917678b2"
          "73bed6b8e3c1743b7116e69e22229516"
          "3ff1caa1681fac09120eca307586e1a7", 64);
    eds_cbc_close(cbc);

    /* decrypt round-trip */
    cbc = eds_cbc_init(64);
    c = eds_aes_init(key, 16);
    eds_cbc_attach(cbc, c);
    from_hex(iv, "000102030405060708090a0b0c0d0e0f");
    eds_cbc_decrypt(cbc, ct, 0, 64, iv, 0);
    check_bool("CBC decrypt round-trip", memcmp(ct, pt, 64) == 0);
    eds_cbc_close(cbc);

    (void)expect;
}

static void test_cipher_vectors(void) {
    /* Published single-block vectors — prove byte-compatibility, not just
       internal round-trip consistency. */
    printf("Cipher published vectors:\n");
    uint8_t k16[16], k32[32], blk[16];

    memset(k16, 0, 16); memset(blk, 0, 16);
    { void* c = eds_twofish_init(k16, 16); eds_twofish_encrypt(c, blk);
      check("Twofish-128 (zero key/pt)", blk, "9f589f5cf6122c32b6bfec2f2ae8c35a", 16);
      eds_twofish_close(c); }

    memset(k32, 0, 32); memset(blk, 0, 16);
    { void* c = eds_twofish_init(k32, 32); eds_twofish_encrypt(c, blk);
      check("Twofish-256 (zero key/pt)", blk, "57ff739d4dc92c1bd7fc01700cc8216f", 16);
      eds_twofish_close(c); }

    memset(k32, 0, 32); memset(blk, 0, 16);
    { void* c = eds_serpent_init(k32, 32); eds_serpent_encrypt(c, blk);
      check("Serpent-256 (zero key/pt)", blk, "49672ba898d98df95019180445491089", 16);
      eds_serpent_close(c); }
}

static void test_cfb(void) {
    printf("AES-128-CFB128 (NIST SP 800-38A):\n");
    uint8_t key[16], iv[16], pt[64], ct[64];
    from_hex(key, "2b7e151628aed2a6abf7158809cf4f3c");
    from_hex(pt,
        "6bc1bee22e409f96e93d7e117393172a"
        "ae2d8a571e03ac9c9eb76fac45af8e51"
        "30c81c46a35ce411e5fbc1191a0a52ef"
        "f69f2445df4f9b17ad2b417be66c3710");

    /* encrypt full 4 blocks */
    void* cfb = eds_cfb_init();
    void* c = eds_aes_init(key, 16);
    eds_cfb_attach(cfb, c);
    memcpy(ct, pt, 64);
    from_hex(iv, "000102030405060708090a0b0c0d0e0f");
    eds_cfb_encrypt(cfb, ct, 0, 64, iv);
    check("CFB encrypt (4 blocks)", ct,
          "3b3fd92eb72dad20333449f8e83cfb4a"
          "c8a64537a0b3a93fcde3cdad9f1ce58b"
          "26751f67a3cbb140b1808cf187a4f4df"
          "c04b05357c5d1c0eeac4c66f9ff7f2e6", 64);
    eds_cfb_close(cfb);

    /* decrypt round-trip */
    cfb = eds_cfb_init();
    c = eds_aes_init(key, 16);
    eds_cfb_attach(cfb, c);
    from_hex(iv, "000102030405060708090a0b0c0d0e0f");
    eds_cfb_decrypt(cfb, ct, 0, 64, iv);
    check_bool("CFB decrypt round-trip", memcmp(ct, pt, 64) == 0);
    eds_cfb_close(cfb);

    /* partial (non-block-multiple) length round-trip: 37 bytes */
    cfb = eds_cfb_init();
    c = eds_aes_init(key, 16);
    eds_cfb_attach(cfb, c);
    memcpy(ct, pt, 64);
    from_hex(iv, "000102030405060708090a0b0c0d0e0f");
    eds_cfb_encrypt(cfb, ct, 0, 37, iv);
    /* first 32 bytes must still match the aligned reference ciphertext */
    check_bool("CFB partial: first 2 blocks match aligned",
        memcmp(ct,
               (uint8_t[]){0x3b,0x3f,0xd9,0x2e,0xb7,0x2d,0xad,0x20,0x33,0x34,0x49,0xf8,0xe8,0x3c,0xfb,0x4a,
                           0xc8,0xa6,0x45,0x37,0xa0,0xb3,0xa9,0x3f,0xcd,0xe3,0xcd,0xad,0x9f,0x1c,0xe5,0x8b}, 32) == 0);
    eds_cfb_close(cfb);
    cfb = eds_cfb_init();
    c = eds_aes_init(key, 16);
    eds_cfb_attach(cfb, c);
    from_hex(iv, "000102030405060708090a0b0c0d0e0f");
    eds_cfb_decrypt(cfb, ct, 0, 37, iv);
    check_bool("CFB partial-length round-trip", memcmp(ct, pt, 37) == 0);
    eds_cfb_close(cfb);
}

int main(void) {
    printf("=== edscrypto native KAT (version %d) ===\n", eds_crypto_version());
    test_aes();
    test_ripemd160();
    test_whirlpool();
    test_block_ciphers();
    test_cipher_vectors();
    test_xts();
    test_xts_vectors();
    test_cbc();
    test_cfb();
    printf("\n%s (%d failure(s))\n", g_failures==0 ? "ALL PASSED" : "FAILURES DETECTED", g_failures);
    return g_failures == 0 ? 0 : 1;
}
