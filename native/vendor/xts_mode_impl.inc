#include <mode_hdr.h>

#define KEY_SIZE 64
#define BYTES_PER_XTS_BLOCK 16
#define LONG_LBA 1


UNIT_TYPEDEF(buf_unit, UNIT_BITS);
BUFR_TYPEDEF(buf_type, UNIT_BITS, BYTES_PER_XTS_BLOCK);

static void gf_mulx(void *x)
{
#if UNIT_BITS == 8

    uint_8t i = 16, t = ((uint_8t*)x)[15];
    while(--i)
        ((uint_8t*)x)[i] = (((uint_8t*)x)[i] << 1) | (((uint_8t*)x)[i - 1] & 0x80 ? 1 : 0);
    ((uint_8t*)x)[0] = (((uint_8t*)x)[0] << 1) ^ (t & 0x80 ? 0x87 : 0x00);

#elif PLATFORM_BYTE_ORDER == IS_LITTLE_ENDIAN

#  if UNIT_BITS == 64

#   define GF_MASK  li_64(8000000000000000) 
#   define GF_XOR   li_64(0000000000000087) 
    uint_64t _tt = ((UPTR_CAST(x,64)[1] & GF_MASK) ? GF_XOR : 0);
    UPTR_CAST(x,64)[1] = (UPTR_CAST(x,64)[1] << 1) | (UPTR_CAST(x,64)[0] & GF_MASK ? 1 : 0);
    UPTR_CAST(x,64)[0] = (UPTR_CAST(x,64)[0] << 1) ^ _tt;

#  else /* UNIT_BITS == 32 */

#   define GF_MASK  li_32(80000000) 
#   define GF_XOR   li_32(00000087) 
    uint_32t _tt = ((UPTR_CAST(x,32)[3] & GF_MASK) ? GF_XOR : 0);;
    UPTR_CAST(x,32)[3] = (UPTR_CAST(x,32)[3] << 1) | (UPTR_CAST(x,32)[2] & GF_MASK ? 1 : 0);
    UPTR_CAST(x,32)[2] = (UPTR_CAST(x,32)[2] << 1) | (UPTR_CAST(x,32)[1] & GF_MASK ? 1 : 0);
    UPTR_CAST(x,32)[1] = (UPTR_CAST(x,32)[1] << 1) | (UPTR_CAST(x,32)[0] & GF_MASK ? 1 : 0);
    UPTR_CAST(x,32)[0] = (UPTR_CAST(x,32)[0] << 1) ^ _tt;

#  endif

#else /* PLATFORM_BYTE_ORDER == IS_BIG_ENDIAN */

#  if UNIT_BITS == 64

#   define MASK_01  li_64(0101010101010101)
#   define GF_MASK  li_64(0000000000000080) 
#   define GF_XOR   li_64(8700000000000000) 
    uint_64t _tt = ((UPTR_CAST(x,64)[1] & GF_MASK) ? GF_XOR : 0);
    UPTR_CAST(x,64)[1] =  ((UPTR_CAST(x,64)[1] << 1) & ~MASK_01) 
        | (((UPTR_CAST(x,64)[1] >> 15) | (UPTR_CAST(x,64)[0] << 49)) & MASK_01);
    UPTR_CAST(x,64)[0] = (((UPTR_CAST(x,64)[0] << 1) & ~MASK_01) 
        |  ((UPTR_CAST(x,64)[0] >> 15) & MASK_01)) ^ _tt;

#  else /* UNIT_BITS == 32 */

#   define MASK_01  li_32(01010101)
#   define GF_MASK  li_32(00000080) 
#   define GF_XOR   li_32(87000000) 
    uint_32t _tt = ((UPTR_CAST(x,32)[3] & GF_MASK) ? GF_XOR : 0);
    UPTR_CAST(x,32)[3] =  ((UPTR_CAST(x,32)[3] << 1) & ~MASK_01) 
        | (((UPTR_CAST(x,32)[3] >> 15) | (UPTR_CAST(x,32)[2] << 17)) & MASK_01);
    UPTR_CAST(x,32)[2] =  ((UPTR_CAST(x,32)[2] << 1) & ~MASK_01) 
        | (((UPTR_CAST(x,32)[2] >> 15) | (UPTR_CAST(x,32)[1] << 17)) & MASK_01);
    UPTR_CAST(x,32)[1] =  ((UPTR_CAST(x,32)[1] << 1) & ~MASK_01) 
        | (((UPTR_CAST(x,32)[1] >> 15) |   (UPTR_CAST(x,32)[0] << 17)) & MASK_01);
    UPTR_CAST(x,32)[0] = (((UPTR_CAST(x,32)[0] << 1) & ~MASK_01) 
        |  ((UPTR_CAST(x,32)[0] >> 15) & MASK_01)) ^ _tt;

#  endif

#endif
}

static void xts_encrypt_sector(const block_cipher_interface *cipherA, const block_cipher_interface *cipherB, uint8_t *buffer, int length, uint64_t start_sector)
{
	buf_type hh;
    uint8_t *pos = buffer, *hi = buffer + length;

    xor_function f_ptr = (!ALIGN_OFFSET(buffer, UNIT_BITS >> 3) ? xor_block_aligned : xor_block );
    
#if defined( LONG_LBA )
    *UPTR_CAST(hh, 64) = start_sector;
    memset(UPTR_CAST(hh, 8) + 8, 0, 8);
    uint_64t_to_le(*UPTR_CAST(hh, 64));
#else
    *UPTR_CAST(hh, 32) = start_sector;
    memset(UPTR_CAST(hh, 8) + 4, 0, 12);
    uint_32t_to_le(*UPTR_CAST(hh, 32));
#endif

    cipherB->encrypt(UPTR_CAST(hh, 8),UPTR_CAST(hh, 8),cipherB->context);

    while(pos + BYTES_PER_XTS_BLOCK <= hi)
    {
        f_ptr(pos, pos, hh);
        cipherA->encrypt(pos,pos,cipherA->context);        
        f_ptr(pos, pos, hh);
        pos += BYTES_PER_XTS_BLOCK;
        gf_mulx(hh);
    }

    if(pos < hi)
    {
        uint8_t *tp = pos - BYTES_PER_XTS_BLOCK;
        while(pos < hi)
        {
            uint8_t tt = *(pos - BYTES_PER_XTS_BLOCK);
            *(pos - BYTES_PER_XTS_BLOCK) = *pos;
            *pos++ = tt;
        }
        f_ptr(tp, tp, hh);
        cipherA->encrypt(tp,tp,cipherA->context);
        f_ptr(tp, tp, hh);
    }
}

static void xts_decrypt_sector(const block_cipher_interface *cipherA, const block_cipher_interface *cipherB, uint8_t *buffer, int length, uint64_t start_sector)
{
	buf_type hh, hh2;
    uint8_t *pos = buffer, *hi = buffer + length;

    xor_function f_ptr = (!ALIGN_OFFSET(buffer, UNIT_BITS >> 3) ? xor_block_aligned : xor_block );

#if defined( LONG_LBA )
    *UPTR_CAST(hh, 64) = start_sector;
    memset(UPTR_CAST(hh, 8) + 8, 0, 8);
    uint_64t_to_le(*UPTR_CAST(hh, 64));
#else
    *UPTR_CAST(hh, 32) = start_sector;
    memset(UPTR_CAST(hh, 8) + 4, 0, 12);
    uint_32t_to_le(*UPTR_CAST(hh, 32));
#endif

    cipherB->encrypt(UPTR_CAST(hh, 8),UPTR_CAST(hh, 8),cipherB->context);

    while(pos + BYTES_PER_XTS_BLOCK <= hi)
    {
        if(hi - pos > BYTES_PER_XTS_BLOCK && hi - pos < 2 * BYTES_PER_XTS_BLOCK)
        {
            memcpy(hh2, hh, BYTES_PER_XTS_BLOCK);
            gf_mulx(hh);
        }
        f_ptr(pos, pos, hh);
        cipherA->decrypt(pos,pos,cipherA->context);
        f_ptr(pos, pos, hh);
        pos += BYTES_PER_XTS_BLOCK;
        gf_mulx(hh);
    }

    if(pos < hi)
    {
        uint8_t *tp = pos - BYTES_PER_XTS_BLOCK;
        while(pos < hi)
        {
            uint8_t tt = *(pos - BYTES_PER_XTS_BLOCK);
            *(pos - BYTES_PER_XTS_BLOCK) = *pos;
            *pos++ = tt;
        }
        f_ptr(tp, tp, hh2);
        cipherA->decrypt(tp,tp,cipherA->context);
        f_ptr(tp, tp, hh2);
    }
}

static void free_cipher_list(cipher_pair *ciphers_head)
{
	if(ciphers_head!=NULL)
	{
		if(ciphers_head->next!=NULL)
			free_cipher_list((cipher_pair *)ciphers_head->next);
		free(ciphers_head);
	}
}

static cipher_pair *attach_ciphers(cipher_pair *tail,block_cipher_interface *cipherA,block_cipher_interface *cipherB)
{
	cipher_pair *cp = (cipher_pair *)malloc(sizeof(cipher_pair));
	cp->next = NULL;
	cp->prev = tail;
	if(tail!=NULL)
		tail->next = cp;
	cp->cipherA = cipherA;
	cp->cipherB = cipherB;
	return cp;
}

static void attach_ciphers_to_tail(xts_context *ctx,block_cipher_interface *cipherA,block_cipher_interface *cipherB)
{
	ctx->ciphers_tail = attach_ciphers(ctx->ciphers_tail,cipherA,cipherB);
	if(ctx->ciphers_head == NULL)
		ctx->ciphers_head = ctx->ciphers_tail;
}

static int is_buffer_empty(uint8_t *buf)
{
    int i;
    for(i=0;i<XTS_SECTOR_SIZE;i++)
        if(buf[i])
            return 0;
    return 1;
}

void xts_encrypt(xts_context *context, uint8_t *data, int offset, int length, uint64_t start_sector_index)
{
    uint64_t sector_index;
    uint8_t *cur;
    cipher_pair *cp = context->ciphers_head;  
    int left, incr;

    while(cp!=NULL)
    {
        for(cur = data + offset,left = length, incr = XTS_SECTOR_SIZE, sector_index = start_sector_index; left > 0; sector_index++, cur += incr, left-=incr )
        {
            if(left < XTS_SECTOR_SIZE)
                incr = left;
            if(!context->allow_skip || !is_buffer_empty(cur))
                xts_encrypt_sector(cp->cipherA, cp->cipherB, cur, incr, sector_index);
        }
        cp = cp->next;
    } 
}

void xts_decrypt(xts_context *context, uint8_t *data, int offset, int length, uint64_t start_sector_index)
{
    uint64_t sector_index;
    uint8_t *cur;
    cipher_pair *cp = context->ciphers_tail;  
    int left, incr;

    while(cp!=NULL)
    {
        for(cur = data + offset,left = length, incr = XTS_SECTOR_SIZE, sector_index = start_sector_index; left > 0; sector_index++, cur += incr, left-=incr )
        {
            if(left < XTS_SECTOR_SIZE)
                incr = left;
            if(!context->allow_skip || !is_buffer_empty(cur))
                xts_decrypt_sector(cp->cipherA, cp->cipherB, cur, incr, sector_index);
        }  
        cp = cp->prev;
    }
}

