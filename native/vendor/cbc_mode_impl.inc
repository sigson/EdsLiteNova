/*
 * cbc_mode_impl.inc — self-contained CBC mode logic.
 *
 * Extracted verbatim (logic-wise) from the original edslite native edscbc.c,
 * with the JNI plumbing removed. Included as a translation-unit fragment by
 * edscrypto.c. Structs are prefixed cbc_ and helpers are static to avoid any
 * clash with the XTS fragment sharing the same TU.
 *
 * Chaining semantics match the original: multiple ciphers may be cascaded
 * (encrypt walks head->tail, decrypt walks tail->head). The IV buffer is
 * in/out (updated with the last cipher-block), and when increment_iv is set the
 * IV is advanced as a little-endian counter every file_block_size bytes.
 */
#include <block_cipher.h>
#include <string.h>
#include <stdlib.h>

#define CBC_BLOCK_SIZE 16

typedef struct cbc_cipher_node
{
    block_cipher_interface* cipher;
    struct cbc_cipher_node* next;
    struct cbc_cipher_node* prev;
} cbc_cipher_node;

typedef struct
{
    cbc_cipher_node* ciphers_head;
    cbc_cipher_node* ciphers_tail;
    size_t file_block_size;
} cbc_context;

static void cbc_encrypt_block(const block_cipher_interface* cipher, uint8_t* buffer,
                              size_t len, uint8_t ivec[CBC_BLOCK_SIZE])
{
    size_t n;
    const uint8_t* iv = ivec;
    while (len)
    {
        for (n = 0; n < CBC_BLOCK_SIZE && n < len; ++n)
            buffer[n] = buffer[n] ^ iv[n];
        for (; n < CBC_BLOCK_SIZE; ++n)
            buffer[n] = iv[n];
        cipher->encrypt(buffer, buffer, cipher->context);
        iv = buffer;
        if (len <= CBC_BLOCK_SIZE) break;
        len -= CBC_BLOCK_SIZE;
        buffer += CBC_BLOCK_SIZE;
    }
    memcpy(ivec, iv, CBC_BLOCK_SIZE);
}

static void cbc_decrypt_block(const block_cipher_interface* cipher, uint8_t* buffer,
                              size_t len, uint8_t ivec[CBC_BLOCK_SIZE])
{
    uint8_t c;
    uint8_t tmp[CBC_BLOCK_SIZE];
    size_t n;
    while (len)
    {
        cipher->decrypt(buffer, tmp, cipher->context);
        for (n = 0; n < CBC_BLOCK_SIZE && n < len; ++n)
        {
            c = buffer[n];
            buffer[n] = tmp[n] ^ ivec[n];
            ivec[n] = c;
        }
        if (len <= CBC_BLOCK_SIZE)
        {
            for (; n < CBC_BLOCK_SIZE; ++n)
                ivec[n] = buffer[n];
            break;
        }
        len -= CBC_BLOCK_SIZE;
        buffer += CBC_BLOCK_SIZE;
    }
}

static void cbc_free_cipher_list(cbc_cipher_node* head)
{
    if (head != NULL)
    {
        if (head->next != NULL)
            cbc_free_cipher_list(head->next);
        free(head);
    }
}

static cbc_cipher_node* cbc_attach_ciphers(cbc_cipher_node* tail, block_cipher_interface* cipher)
{
    cbc_cipher_node* cp = (cbc_cipher_node*)malloc(sizeof(cbc_cipher_node));
    cp->next = NULL;
    cp->prev = tail;
    if (tail != NULL) tail->next = cp;
    cp->cipher = cipher;
    return cp;
}

static void cbc_attach_ciphers_to_tail(cbc_context* ctx, block_cipher_interface* cipher)
{
    ctx->ciphers_tail = cbc_attach_ciphers(ctx->ciphers_tail, cipher);
    if (ctx->ciphers_head == NULL)
        ctx->ciphers_head = ctx->ciphers_tail;
}

static void cbc_increment_iv(uint8_t* iv)
{
    int i;
    for (i = 0; i < CBC_BLOCK_SIZE; i++)
        if (++iv[i] & 0xFF)
            break;
}

static void cbc_encrypt(cbc_context* context, uint8_t* data, int offset, int length,
                        uint8_t* iv, unsigned char increment_iv)
{
    uint8_t current_iv[CBC_BLOCK_SIZE], block_iv[CBC_BLOCK_SIZE];
    uint8_t* cur;
    cbc_cipher_node* cn = context->ciphers_head;
    size_t file_block_size = increment_iv ? context->file_block_size : (size_t)length;
    int left, incr;

    memcpy(block_iv, iv, CBC_BLOCK_SIZE);
    while (cn != NULL)
    {
        memcpy(current_iv, iv, CBC_BLOCK_SIZE);
        for (cur = data + offset, left = length, incr = (int)file_block_size;
             left > 0; cur += incr, left -= incr)
        {
            memcpy(block_iv, current_iv, CBC_BLOCK_SIZE);
            if (left < (int)file_block_size)
                incr = left;
            cbc_encrypt_block(cn->cipher, cur, incr, block_iv);
            if (increment_iv)
                cbc_increment_iv(current_iv);
        }
        cn = cn->next;
    }
    memcpy(iv, block_iv, CBC_BLOCK_SIZE);
}

static void cbc_decrypt(cbc_context* context, uint8_t* data, int offset, int length,
                        uint8_t* iv, unsigned char increment_iv)
{
    uint8_t current_iv[CBC_BLOCK_SIZE], block_iv[CBC_BLOCK_SIZE];
    uint8_t* cur;
    cbc_cipher_node* cn = context->ciphers_tail;
    size_t file_block_size = increment_iv ? context->file_block_size : (size_t)length;
    int left, incr;

    memcpy(block_iv, iv, CBC_BLOCK_SIZE);
    while (cn != NULL)
    {
        memcpy(current_iv, iv, CBC_BLOCK_SIZE);
        for (cur = data + offset, left = length, incr = (int)file_block_size;
             left > 0; cur += incr, left -= incr)
        {
            memcpy(block_iv, current_iv, CBC_BLOCK_SIZE);
            if (left < (int)file_block_size)
                incr = left;
            cbc_decrypt_block(cn->cipher, cur, incr, block_iv);
            if (increment_iv)
                cbc_increment_iv(current_iv);
        }
        cn = cn->prev;
    }
    memcpy(iv, block_iv, CBC_BLOCK_SIZE);
}
