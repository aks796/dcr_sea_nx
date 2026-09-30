/* config.h -- build-wide constants for the Disney Crossy Road Switch wrapper.
 *
 * Unity 2017.4.17f1, Mono backend, armeabi-v7a. AArch32 host process built
 * against libnx32 (see the Makefile). MIT.
 */
#ifndef DCR_CONFIG_H
#define DCR_CONFIG_H

/* Where on the SD card the game files live (named like the NRO; before build
 * 202609300710: /switch/disneycrossyroadsea, see dcr_migrate.h), and the
 * module names. */
#define DCR_ROOT_PATH   "/switch/dcr_sea_nx"
#define DCR_LIB_MAIN    "libmain.so"
#define DCR_LIB_UNITY   "libunity.so"
#define DCR_LIB_MONO    "libmono.so"
#define DCR_APK_NAME    "game.apk"   /* the user's own APK, under any name at first (dcr_setup.c) */
#define DCR_DATA_DIR    "assets/bin/Data"
#define DCR_MANAGED_DIR "assets/bin/Data/Managed"

/* The reserved region the three game modules are mapped into, end to end.
 * Measured from THIS build's ELF program headers (highest p_vaddr+p_memsz):
 *   libmain    ~0x5000       (< 1 MB)
 *   libunity   0x11d3c60     (~18 MB mapped)
 *   libmono    0x3bce08      (~4 MB mapped)
 * ~22 MB with page rounding. 48 MB leaves better than a doubling of headroom
 * for a content update, and (unlike the arm64 sibling) 32-bit address space is
 * scarce, so this is deliberately not oversized. Under-reserving shows up as
 * so_load() returning -3, which load_module() reports by name.
 *
 * NOTE: kept in sync with the Makefile's LOAD_ADDRESS. A 32-bit process's
 * address space tops out at 4 GB and libnx32's virtmem carves it up, so the
 * loader takes its RX reservation from virtmemFindCodeMemory rather than a
 * fixed LOAD_ADDRESS (see so_util32.c). This macro sizes the RW staging pool. */
#define SO_REGION_BYTES (48u * 1024 * 1024)

/* Left outside the heap. GPU buffers do NOT need it: libdrm_nouveau allocates
 * them with memalign and hands them to nvmap, i.e. from the heap. This is only
 * headroom for kernel-side allocations. */
#define GFX_RESERVE_MB  16u

/* Render resolution (handheld and docked share one; compositor scales). */
#define DCR_FORCE_SCREEN_W 1280
#define DCR_FORCE_SCREEN_H 720

/* Feature switches wired against dcr_offsets.h in later phases. */
#define DCR_PATCH_CHOREOGRAPHER 1   /* patch ChoreographerEnableVSync -> ret */
#define DCR_PATCH_VSYNC         1   /* pump the vsync counter from a thread   */
#define DCR_TIME_ICALL_HOOKS    1   /* hook the 11 Time icalls                */

/* Mono runtime policy (see mono_rt.c): avoid the signal paths Horizon can't
 * service. Coop suspend removes the tkill dependency; explicit null checks
 * remove the SIGSEGV dependency. */
#define DCR_MONO_COOP_SUSPEND        1
#define DCR_MONO_EXPLICIT_NULLCHECKS 1

#define DEBUG_LOG 1

/* The renderer: 1 = mesa/nouveau (gl_mesa.c, portlibs32/ from
 * mesa32), 0 = null GL (gl_null.c: runs the game, draws
 * nothing). Set by the Makefile. The boot log is on screen either way until
 * the game creates its window surface. */
#ifndef DCR_GL_MESA
#define DCR_GL_MESA 0
#endif

#endif /* DCR_CONFIG_H */
