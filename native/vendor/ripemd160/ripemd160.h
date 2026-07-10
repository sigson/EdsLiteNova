#ifndef EDS_RIPEMD160_H
#define EDS_RIPEMD160_H
#include <stdint.h>

#define RIPEMD160_BLOCK_LENGTH 64
#define RIPEMD160_DIGEST_LENGTH 20

typedef struct RMD160Context
{
    uint32_t state[5];
    uint64_t count;
    unsigned char buffer[RIPEMD160_BLOCK_LENGTH];
} RMD160_CTX;

void rmd160_init(RMD160_CTX *ctx);
void rmd160_update(RMD160_CTX *ctx, const uint8_t *input, uint32_t lenArg);
void rmd160_final(unsigned char *digest, RMD160_CTX *ctx);

#endif
