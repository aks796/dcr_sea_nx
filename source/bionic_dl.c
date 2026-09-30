/* bionic_dl.c -- dlopen / dlsym / dladdr and the ARM EHABI exidx lookup.
 *
 * libunity reaches Mono entirely through the dynamic linker: it has no mono_*
 * imports at all, it dlopen()s "libmono.so" and dlsym()s every entry point
 * (mono_jit_init_version, mono_set_dirs, ... 216 names). So dlopen of a module
 * we loaded returns that module and dlsym reads its exports. GLES entry points
 * arrive the same way (libunity imports no gl* symbol), served by the GL layer.
 *
 *   "libvulkan.so"            refused: forces the engine onto GLES
 *   system libraries          our shim table + the GL layer
 *   libmain/libunity/libmono  the loaded module
 *   anything else             not found (P/Invoke plugins; logged)
 *
 * __gnu_Unwind_Find_exidx is bionic's hook for the EHABI unwinder statically
 * linked into libmain (and used by libunity's C++ exceptions): given a PC it
 * returns that module's .ARM.exidx table. Answering it properly means C++
 * exceptions thrown and caught inside the engine work -- the arm64 sibling had
 * no such hook and every throw went straight to std::terminate. MIT.
 */
#include <elf.h>
#include <stdio.h>
#include <string.h>

#include "bionic.h"
#include "gl_layer.h"
#include "imports.h"
#include "so_util.h"
#include "util.h"

extern so_module main_mod, unity_mod, mono_mod;

static int g_sys_handle, g_default_handle;
static __thread const char *t_dlerror;

static const char *const g_system_libs[] = {
    "libc.so", "libm.so", "libdl.so", "liblog.so", "libandroid.so", "libz.so",
    "libstdc++.so", "libEGL.so", "libGLESv1_CM.so", "libGLESv2.so", "libGLESv3.so",
    "libjnigraphics.so",
};
/* Reported ABSENT (dlopen -> NULL), so the engine takes its fallback:
 *   libOpenSLES.so   FMOD then uses its Java output, org.fmod.FMODAudioDevice,
 *                    which dcr_audio.c drives into audout;
 *   libOpenMAXAL.so / libmediandk.so   no hardware video decoding here. */

void *b_dlopen(const char *name, int flags) {
  if (!name)
    return &g_default_handle;
  const char *base = strrchr(name, '/') ? strrchr(name, '/') + 1 : name;
  if (!strcmp(base, "libvulkan.so")) {
    t_dlerror = "libvulkan.so: not available (GLES only)";
    return NULL;
  }
  if (!strcmp(base, "libmono.so") && mono_mod.load_virtbase)
    return &mono_mod;
  if (!strcmp(base, "libunity.so"))
    return &unity_mod;
  if (!strcmp(base, "libmain.so"))
    return &main_mod;
  for (unsigned i = 0; i < ARRAY_SIZE(g_system_libs); i++)
    if (!strcmp(base, g_system_libs[i]))
      return &g_sys_handle;
  debugPrintf("[dl] dlopen(%s) -> not found (from %p)\n", name, __builtin_return_address(0));
  t_dlerror = "library not found";
  return NULL;
}

int b_dlclose(void *h) { return 0; }

char *b_dlerror(void) {
  const char *e = t_dlerror;
  t_dlerror = NULL;
  return (char *)e;
}

static uintptr_t sys_lookup(const char *name) {
  uintptr_t a = dcr_import_lookup(name);
  if (!a)
    a = dcr_gl_lookup(name);
  return a;
}

void *b_dlsym(void *h, const char *name) {
  if (!name)
    return NULL;
  uintptr_t a = 0;
  if (h == &main_mod || h == &unity_mod || h == &mono_mod) {
    a = so_try_find_addr_rx((so_module *)h, name);
  } else if (h == &g_sys_handle) {
    a = sys_lookup(name);
  } else {
    /* RTLD_DEFAULT (NULL/0), RTLD_NEXT (-1) and dlopen(NULL): everything. */
    a = (uintptr_t)so_resolve_external(name);
    if (!a)
      a = sys_lookup(name);
  }
  /* mono_add_internal_call is wrapped, however it is looked up. */
  void *dcr_icall_interpose(const char *sym, void *real); /* dcr_icall_hooks.c */
  a = (uintptr_t)dcr_icall_interpose(name, (void *)a);
  if (!a) {
    static int logged;
    if (logged++ < 64)
      debugPrintf("[dl] dlsym(%s) -> NULL (from %p)\n", name, __builtin_return_address(0));
    t_dlerror = "symbol not found";
  }
  return (void *)a;
}

int b_dladdr(const void *addr, b_Dl_info *info) {
  so_module *m = so_find_module_by_addr(addr);
  if (!m || !info)
    return 0;
  static char names[3][160];
  int idx = m == &main_mod ? 0 : m == &unity_mod ? 1 : 2;
  const char *base = strrchr(m->name, '/') ? strrchr(m->name, '/') + 1 : m->name;
  snprintf(names[idx], sizeof names[idx], "/data/app/net.gogame.disney.crossyroad-1/lib/arm/%s", base);
  info->dli_fname = names[idx];
  info->dli_fbase = m->load_virtbase;
  info->dli_sname = NULL;
  info->dli_saddr = NULL;
  /* nearest exported symbol at or below addr */
  uintptr_t off = (uintptr_t)addr - (uintptr_t)m->load_virtbase, best = 0;
  for (int i = 0; i < m->num_syms; i++) {
    const Elf32_Sym *s = &m->syms[i];
    if (s->st_shndx == SHN_UNDEF || !s->st_name)
      continue;
    uintptr_t v = s->st_value & ~1u;
    if (v <= off && v >= best) {
      best = v;
      info->dli_sname = m->dynstrtab + s->st_name;
      info->dli_saddr = (void *)((uintptr_t)m->load_virtbase + s->st_value);
    }
  }
  return 1;
}

/* _Unwind_Ptr __gnu_Unwind_Find_exidx(_Unwind_Ptr pc, int *pcount) */
uintptr_t b___gnu_Unwind_Find_exidx(uintptr_t pc, int *pcount) {
  so_module *m = so_find_module_by_addr((const void *)pc);
  if (m) {
    for (int i = 0; i < m->phnum; i++)
      if (m->phdr[i].p_type == PT_ARM_EXIDX) {
        if (pcount)
          *pcount = (int)(m->phdr[i].p_memsz / 8);
        return (uintptr_t)m->load_virtbase + m->phdr[i].p_vaddr;
      }
  }
  if (pcount)
    *pcount = 0;
  return 0;
}
