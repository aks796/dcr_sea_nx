/* so_util.h -- AArch32 Android .so loader for the Switch (libnx32).
 *
 * 32-bit counterpart of the Drive Ahead port's so_util. Loads armeabi-v7a
 * shared objects into a libnx32 code-memory reservation, applies the ARM REL
 * relocations Unity 2017.4 emits, resolves imports against a shim table, and
 * flips the code executable with the Atmosphere JIT syscalls. MIT / derived
 * from the Andy Nguyen / fgsfds so-loader lineage and vita2hos (load.c).
 */
#ifndef DCR_SO_UTIL_H
#define DCR_SO_UTIL_H

#include <elf.h>
#include <stdint.h>
#include <switch.h>

#define SO_MAX_SEGMENTS 8

/* One shim: an imported symbol name mapped to the host function that serves it. */
typedef struct {
  char symbol[128];
  uintptr_t func;
} DynLibFunction;

typedef struct so_module {
  struct so_module *next;

  /* file image (freed after finalize) */
  void *so_base;
  size_t so_size;

  /* mapped image: RW staging (load_base) and the RX alias (load_virtbase) */
  void *load_base;
  void *load_virtbase;
  VirtmemReservation *load_memrv;
  size_t load_size;

  Elf32_Ehdr *elf_hdr;
  Elf32_Phdr *prog_hdr;   /* into so_base (file image) */
  Elf32_Shdr *sec_hdr;
  char *shstrtab;

  Elf32_Phdr phdr[SO_MAX_SEGMENTS * 2];  /* pristine copy for dl_iterate_phdr */
  int phnum;

  Elf32_Sym *syms;
  int num_syms;
  char *dynstrtab;

  char name[64];
} so_module;

/* Load / relocate / resolve / finalize -- call in this order. so_load: base may
 * be NULL, in which case a page-aligned staging buffer is allocated (and then
 * donated to the code mapping by so_finalize -- never freed). */
int  so_load(so_module *mod, const char *filename, void *base, size_t max_size);
int  so_relocate(so_module *mod);
int  so_resolve(so_module *mod, DynLibFunction *funcs, int num_funcs,
                int taint_missing_imports);
void so_finalize(so_module *mod);       /* map RX/RW via JIT syscalls */
void so_flush_caches(so_module *mod);
void so_execute_init_array(so_module *mod);
void so_free_temp(so_module *mod);
int  so_unload(so_module *mod);

/* Symbol lookup. so_find_addr* return runtime (load_virtbase) addresses. */
uintptr_t so_find_addr(so_module *mod, const char *symbol);       /* fatal if missing */
uintptr_t so_find_addr_rx(so_module *mod, const char *symbol);    /* fatal if missing */
uintptr_t so_try_find_addr_rx(so_module *mod, const char *symbol);/* 0 if missing */
void     *so_resolve_external(const char *name);                  /* dlsym backing */
so_module *so_find_module_by_addr(const void *addr);
DynLibFunction *so_find_import(DynLibFunction *funcs, int n, const char *name);

/* Patch loaded (RX) code: writes through a temporary RW alias, then flushes.
 * hook_arm writes an 8-byte absolute-branch stub (ARM/Thumb interworking). */
int  so_patch_code(void *dst, const void *src, size_t len);
void hook_arm(uintptr_t addr, uintptr_t dst);

int  so_dl_iterate_phdr(int (*cb)(void *info, size_t size, void *data), void *data);
int  so_dump_maps(char *buf, size_t cap);

#endif /* DCR_SO_UTIL_H */
