/* main.c -- boot sequence for the Disney Crossy Road: SEA wrapper (32-bit, Mono).
 *
 * The order here matters; each step says why it is where it is. MIT.
 */
#include <malloc.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <switch.h>
#include <sys/stat.h>
#include <unistd.h>

#include "config.h"
#include "dcr_config.h"
#include "dcr_manifest.h"
#include "dcr_migrate.h"
#include "dcr_patches.h"
#include "dcr_path.h"
#include "dcr_sched.h"
#include "dcr_time.h"
#include "error.h"
#include "imports.h"
#include "jit_arena.h"
#include "nx_init.h"
#include "selfproc.h"
#include "so_util.h"
#include "util.h"

int dcr_time_install(void);
int dcr_vsync_init(void);
int dcr_mono_install(void);
int dcr_boot_run(void);
void dcr_setup_update_from_nro(void); /* dcr_setup.c */
void dcr_setup_from_apk(const char *apk);

so_module main_mod, unity_mod, mono_mod;

static char g_root[256] = "sdmc:" DCR_ROOT_PATH;
const char *dcr_game_root(void) { return g_root; }

extern volatile uint32_t __dcr_reloc_path __attribute__((visibility("hidden")));

extern volatile uint32_t __dcr_reloc_diag[16] __attribute__((visibility("hidden")));

static void report_boot(void) {
  const u64 MB = 1024 * 1024;
  debugPrintf("[boot] === dcrsea_nx: Disney Crossy Road: SEA (Unity 5.6.4f1 / Mono, armeabi-v7a) ===\n");
  static const char *const paths[] = {"none needed", "patched through a writable alias (hardware)",
                                      "direct writes (emulator: pseudo-handle refused)"};
  debugPrintf("[boot] text relocations: %s\n",
              __dcr_reloc_path < 3 ? paths[__dcr_reloc_path] : "?");
  if (__dcr_reloc_diag[1]) /* test build (DCR_TEST_ALIAS_RELOC) only */
    debugPrintf("[reloc] alias test: result 0x%lx (a11a5 = OK) handle 0x%lx fail rc 0x%lx at %08lx"
                " | blk %08lx+%lx -> %08lx | blk %08lx+%lx -> %08lx | blk %08lx+%lx -> %08lx"
                " | RX view sees patch: %lu ok, %lu stale\n",
                (unsigned long)__dcr_reloc_diag[0], (unsigned long)__dcr_reloc_diag[1],
                (unsigned long)__dcr_reloc_diag[2], (unsigned long)__dcr_reloc_diag[3],
                (unsigned long)__dcr_reloc_diag[4], (unsigned long)__dcr_reloc_diag[5],
                (unsigned long)__dcr_reloc_diag[6], (unsigned long)__dcr_reloc_diag[7],
                (unsigned long)__dcr_reloc_diag[8], (unsigned long)__dcr_reloc_diag[9],
                (unsigned long)__dcr_reloc_diag[10], (unsigned long)__dcr_reloc_diag[11],
                (unsigned long)__dcr_reloc_diag[12], (unsigned long)__dcr_reloc_diag[13],
                (unsigned long)__dcr_reloc_diag[14]);
  debugPrintf("[heap] total %u MB, used %u MB at start, heap region %u MB, heap %u MB @ %p\n",
              (unsigned)(g_nxinit.total / MB), (unsigned)(g_nxinit.used / MB),
              (unsigned)(g_nxinit.heap_region / MB), (unsigned)(g_nxinit.heap / MB),
              (void *)g_nxinit.heap_base);
  debugPrintf("[svc] sm=%x applet=%x hid=%x time=%x fs=%x sdmc=%x\n", g_nxinit.rc_sm,
              g_nxinit.rc_applet, g_nxinit.rc_hid, g_nxinit.rc_time, g_nxinit.rc_fs,
              g_nxinit.rc_sdmc);
  if (R_FAILED(g_nxinit.rc_time))
    debugPrintf("[svc] time service unavailable: clocks fall back to the system tick\n");
}

/* Under an emulator a module runs where it is staged; libunity is staged with
 * room behind it for the instruction stubs of emu_fixups.c (branch range). */
#define EMU_POOL_BYTES 0x10000u
static uint32_t *g_emu_pool;

static int load_module(so_module *mod, const char *name) {
  char path[512];
  snprintf(path, sizeof path, "%s/%s", g_root, name);
  void *base = NULL;
  size_t max = SO_REGION_BYTES;
  if (dcr_is_emulator() && mod == &unity_mod && (base = memalign(0x1000, SO_REGION_BYTES)))
    max = SO_REGION_BYTES - EMU_POOL_BYTES;
  int rc = so_load(mod, path, base, max);
  if (rc < 0) {
    const char *why = rc == -1 ? "cannot open it, or it is not a 32-bit ARM ELF"
                    : rc == -2 ? "out of memory"
                    : rc == -3 ? "larger than SO_REGION_BYTES"
                    : rc == -4 ? "too many program headers" : "?";
    debugPrintf("[boot] so_load(%s) failed rc=%d: %s\n", path, rc, why);
    return -1;
  }
  if (base) {
    g_emu_pool = (uint32_t *)((uint8_t *)base + mod->load_size);
    memset(g_emu_pool, 0, EMU_POOL_BYTES);
  }
  so_relocate(mod);
  return 0;
}

/* Imports are bound once every module is loaded: Unity 5.6's libunity
 * imports libmono's exports directly (DT_NEEDED libmono.so). */
static void resolve_module(so_module *mod, const char *name) {
  int missing = so_resolve(mod, dcr_imports, dcr_imports_count, 1);
  debugPrintf("[boot] %-12s %6u KB  staged %p -> code %p  (%d unresolved imports)\n", name,
              (unsigned)(mod->load_size >> 10), mod->load_base, mod->load_virtbase, missing);
}

int main(int argc, char *argv[]) {
  int moved = dcr_migrate_old_folder(g_root); /* the folder's old name (dcr_migrate.h) */
  mkdir(g_root, 0777);
  log_init(g_root);
  log_console_open(); /* blank: text only when asked or for setup work */
  report_boot();
  if (moved)
    debugPrintf("[boot] moved %d file(s)/folder(s) from " DCR_OLD_GAME_DIR " into %s\n", moved, g_root);

  if (chdir(g_root) != 0)
    debugPrintf("[boot] WARNING: chdir(%s) failed\n", g_root);
  dcr_config_load(); /* config.ini: patches, resolution, boost */
  void dcr_boost_launch_begin(void);
  dcr_boost_launch_begin(); /* CPU at 1785 MHz until the first picture (dcr_boost.c) */
  /* The log goes on screen only when asked; otherwise dcr_setup.c shows it
   * while it has work to show (first launch, a new game.apk or NRO). */
  if (dcr_config()->boot_log)
    log_console_show_text();
  dcr_time_init();
  dcr_path_prepare_dirs();

  /* A newer build of this program in the launcher NRO: install it and
   * restart into it before anything else happens (dcr_setup.c). */
  dcr_setup_update_from_nro();

  /* the user's APKs under any name become game.apk / dcr.apk (dcr_setup.c) */
  void dcr_setup_adopt_apks(void);
  dcr_setup_adopt_apks();

  char apk[512];
  snprintf(apk, sizeof apk, "%s/" DCR_APK_NAME, g_root);
  void dcr_apkcache_set_path(const char *real);
  dcr_apkcache_set_path(apk); /* the engine's reads of it are cached (dcr_apkcache.c) */
  if (dcr_manifest_load(apk) != 0)
    fatal_error("%s is missing or unreadable.\n\n"
                "Copy the APK of your own Disney Crossy Road, Southeast Asia edition\n"
                "(armeabi-v7a, version 1.5.4), into that folder, under any name: the game\n"
                "reads its data from it, and the libraries are unpacked from it on the\n"
                "first launch.",
                apk);

  if (dcr_self_process() == INVALID_HANDLE)
    fatal_error("Could not obtain a handle to this process.\n"
                "The loader needs it to map the game's code.");

  /* libmain/libunity/libmono and classes.txt, from game.apk when they are
   * missing or it has changed */
  dcr_setup_from_apk(apk);
  if (load_module(&main_mod, DCR_LIB_MAIN) < 0 ||
      load_module(&unity_mod, DCR_LIB_UNITY) < 0 ||
      load_module(&mono_mod, DCR_LIB_MONO) < 0)
    fatal_error("Could not load the game libraries from %s.\n\n"
                "They are unpacked from the APK (lib/armeabi-v7a/) on launch: delete\n"
                "libmain.so, libunity.so, libmono.so and .setup there to unpack them again.",
                g_root);

  resolve_module(&main_mod, DCR_LIB_MAIN);
  resolve_module(&unity_mod, DCR_LIB_UNITY);
  resolve_module(&mono_mod, DCR_LIB_MONO);
  if (dcr_is_emulator()) {
    /* EMULATOR ONLY: instructions Ryujinx's A32 decoder lacks (emu_fixups.c) */
    int dcr_emu_fix_module(so_module *m, uint32_t *pool, size_t pool_words);
    int dcr_emu_fix_self(void);
    if (g_emu_pool)
      dcr_emu_fix_module(&unity_mod, g_emu_pool, EMU_POOL_BYTES / 4);
    dcr_emu_fix_self();
  }

  so_finalize(&main_mod);
  so_finalize(&unity_mod);
  so_finalize(&mono_mod);
  so_flush_caches(&main_mod);
  so_flush_caches(&unity_mod);
  so_flush_caches(&mono_mod);
  debugPrintf("[boot] modules mapped RX/RW\n");

  /* Patches and hooks go in before any engine code runs (init_array first). */
  dcr_patches_apply();       /* Choreographer VSYNC enable/stop -> bx lr   */
  if (dcr_config()->sound_priority) {
    void dcr_fmod_patch_voices(void);
    dcr_fmod_patch_voices(); /* FMOD: 64 real voices, not 32 (dcr_fmod.c) */
  }
  if (dcr_config()->mix_48k) {
    void dcr_fmod_patch_rate(void);
    dcr_fmod_patch_rate(); /* FMOD mixes at 48 kHz, as on a phone (dcr_fmod.c) */
  }

  dcr_time_install();        /* 11 Time icalls + 4 TimeManager entries     */
  dcr_vsync_init();          /* WaitVSync's mutex/cond/counter             */
  dcr_mono_install();        /* icache flush hook + Boehm GC bridge        */

  if (jit_arena_init() != 0)
    fatal_error("Could not reserve executable memory for Mono's JIT.");
  {
    void dcr_mono_hook_exceptions(void);
    dcr_mono_hook_exceptions(); /* names the scripts behind "NullReferenceException" lines */
    void dcr_mod_jitlog_install(void);
    dcr_mod_jitlog_install(); /* a debug aid: <root>/jitlog maps the JIT code (dcr_mod.c) */
  }
  /* The main thread becomes a guest thread like the engine's own: priority
   * 59 on cores 0-2, where the kernel time-slices (dcr_sched.c). */
  dcr_sched_init();
  {
    void dcr_audio_selftest(void);
    dcr_audio_selftest();
    void dcr_pthread_selftest(void);
    dcr_pthread_selftest();
    void dcr_io_selftest(void);
    dcr_io_selftest();
  }
#if DCR_GL_MESA
  {
    /* Graphics self-test: always under an emulator, on hardware when
     * config.ini [debug] gl_selftest is on. */
    if (dcr_is_emulator() || dcr_config()->gl_selftest) {
      int dcr_gl_selftest(void);
      dcr_gl_selftest();
    }
  }
#endif

  /* Android runs a library's constructors when it is loaded: libmain first
   * (System.loadLibrary), then libunity (dlopen from libmain), then libmono
   * (dlopen from libunity during player start-up). */
  so_execute_init_array(&main_mod);
  so_execute_init_array(&unity_mod);
  so_execute_init_array(&mono_mod);
  debugPrintf("[boot] module constructors done\n");

  dcr_boot_run();
  return 0;
}
