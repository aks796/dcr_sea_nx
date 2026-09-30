/* jit_arena.h -- dual-view executable memory for Mono's JIT (see jit_arena.c). */
#ifndef DCR_JIT_ARENA_H
#define DCR_JIT_ARENA_H
#include <stddef.h>

int jit_arena_init(void);
void *jit_alloc(size_t len);                 /* returns an RX address */
int jit_free(void *rx, size_t len);
int jit_contains(const void *p);             /* p inside the RX view */
int jit_range_contains(const void *p, size_t n);
void *jit_rw(const void *rx);                /* RX address -> writable alias */
int jit_is_split(void);                      /* rx != rw (hardware) */
void jit_flush(void *code, int size);        /* mono_arch_flush_icache */

/* Instruction-cache maintenance for a 32-bit process (see jit_arena.c):
 * libnx32's armICacheInvalidate is a no-op, so use these for any code written
 * at run time. */
void dcr_icache_invalidate(void);            /* every core, whole I-cache */
void dcr_code_flush(void *code, size_t size); /* clean D-cache + invalidate I */

#endif
