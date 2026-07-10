/*
 * cfb_mode_impl.inc — self-contained CFB-128 mode logic.
 *
 * Extracted (logic-wise) from the original edslite native edscfb.c, with the
 * JNI plumbing removed. Included as a translation-unit fragment by
 * edscrypto.c. Structs/helpers are prefixed cfb_ and kept static so they do
 * not clash with the XTS/CBC fragments sharing the same TU.
 *
 * This is full-block feedback CFB (segment size == block size == 128 bits),
 * matching the original mode used by EncFS AESCFBStreamCipher. Multiple
 * ciphers may be cascaded (encrypt/decrypt each walk head->tail, re-reading the
 * caller IV for every cipher, exactly like the original). The IV buffer is
 * in/out (updated with the running feedback of the last cipher).
 *
 * The XOR is written byte-wise rather than with the original's word-at-a-time
 * casts. The result is bit-identical (XOR is XOR) but avoids unaligned-access
 * UB, so the ciphertext/digests stay byte-for-byte compatible on every ABI.
 */
#include <block_cipher.h>
#include <string.h>
#include <stdlib.h>

#define CFB_BLOCK_SIZE 16

typedef struct cfb_cipher_node
{
    block_cipher_interface* cipher;
    struct cfb_cipher_node* next;
    struct cfb_cipher_node* prev;
} cfb_cipher_node;

typedef struct
{
    cfb_cipher_node* ciphers_head;
    cfb_cipher_node* ciphers_tail;
} cfb_context;

static void cfb_encrypt_buffer(const block_cipher_interface* cipher, uint8_t* buffer,
                               size_t len, uint8_t ivec[CFB_BLOCK_SIZE])
{
    size_t n;
    while (len >= CFB_BLOCK_SIZE)
    {
        cipher->encrypt(ivec, ivec, cipher->context);
        for (n = 0; n < CFB_BLOCK_SIZE; ++n)
            buffer[n] = ivec[n] ^= buffer[n];
        len -= CFB_BLOCK_SIZE;
        buffer += CFB_BLOCK_SIZE;
    }
    if (len)
    {
        cipher->encrypt(ivec, ivec, cipher->context);
        for (n = 0; n < len; ++n)
            buffer[n] = ivec[n] ^= buffer[n];
    }
}

static void cfb_decrypt_buffer(const block_cipher_interface* cipher, uint8_t* buffer,
                               size_t len, uint8_t ivec[CFB_BLOCK_SIZE])
{
    size_t n;
    uint8_t c;
    while (len >= CFB_BLOCK_SIZE)
    {
        cipher->encrypt(ivec, ivec, cipher->context);
        for (n = 0; n < CFB_BLOCK_SIZE; ++n)
        {
            c = buffer[n];
            buffer[n] = ivec[n] ^ c;
            ivec[n] = c;
        }
        len -= CFB_BLOCK_SIZE;
        buffer += CFB_BLOCK_SIZE;
    }
    if (len)
    {
        cipher->encrypt(ivec, ivec, cipher->context);
        for (n = 0; n < len; ++n)
        {
            c = buffer[n];
            buffer[n] = ivec[n] ^ c;
            ivec[n] = c;
        }
    }
}

static void cfb_free_cipher_list(cfb_cipher_node* head)
{
    if (head != NULL)
    {
        if (head->next != NULL)
            cfb_free_cipher_list(head->next);
        free(head);
    }
}

static cfb_cipher_node* cfb_attach_ciphers(cfb_cipher_node* tail, block_cipher_interface* cipher)
{
    cfb_cipher_node* cp = (cfb_cipher_node*)malloc(sizeof(cfb_cipher_node));
    cp->next = NULL;
    cp->prev = tail;
    if (tail != NULL) tail->next = cp;
    cp->cipher = cipher;
    return cp;
}

static void cfb_attach_ciphers_to_tail(cfb_context* ctx, block_cipher_interface* cipher)
{
    ctx->ciphers_tail = cfb_attach_ciphers(ctx->ciphers_tail, cipher);
    if (ctx->ciphers_head == NULL)
        ctx->ciphers_head = ctx->ciphers_tail;
}

static void cfb_encrypt(cfb_context* context, uint8_t* data, int offset, int length, uint8_t* iv)
{
    uint8_t current_iv[CFB_BLOCK_SIZE];
    cfb_cipher_node* cn = context->ciphers_head;
    while (cn != NULL)
    {
        memcpy(current_iv, iv, CFB_BLOCK_SIZE);
        cfb_encrypt_buffer(cn->cipher, (uint8_t*)data + offset, (size_t)length, current_iv);
        cn = cn->next;
    }
    memcpy(iv, current_iv, CFB_BLOCK_SIZE);
}

static void cfb_decrypt(cfb_context* context, uint8_t* data, int offset, int length, uint8_t* iv)
{
    uint8_t current_iv[CFB_BLOCK_SIZE];
    cfb_cipher_node* cn = context->ciphers_head;
    while (cn != NULL)
    {
        memcpy(current_iv, iv, CFB_BLOCK_SIZE);
        cfb_decrypt_buffer(cn->cipher, (uint8_t*)data + offset, (size_t)length, current_iv);
        cn = cn->next;
    }
    memcpy(iv, current_iv, CFB_BLOCK_SIZE);
}
