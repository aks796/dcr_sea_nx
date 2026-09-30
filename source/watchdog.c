/* watchdog.c -- keeps the log reaching the SD card, and reports hangs.
 *
 * During play the log goes to a RAM ring (util.c); the frame loop writes it
 * out every 300 frames. If the engine stops returning from nativeRender, that
 * never happens and the evidence stays in RAM (hardware 2026-09-23: stuck on
 * the loading screen, debug.log ended at frame 3). So this thread, created
 * with libnx directly (the Mono GC never sees or stops it):
 *   - flushes the ring every 5 s whatever the engine is doing;
 *   - when no frame has completed for 10 s while in focus, snapshots every
 *     registered thread -- pause, read registers and the top of its stack,
 *     resume -- and only then logs, with addresses named module+offset.
 *     Nothing is logged while a thread is paused: it may hold the log lock.
 * Reports repeat at 40 s and 100 s of the same hang, then stop. MIT.
 */
#include <stdio.h>
#include <string.h>
#include <switch.h>

#include "bionic_pthread.h"
#include "dcr_sched.h"
#include "so_util.h"
#include "util.h"
int b_vsnprintf(char *buf, size_t n, const char *fmt, va_list ap); /* bionic_printf.c: NULL-safe */

const char *dcr_addr_name(uint32_t a, char *buf, size_t cap); /* exc_handler.c */
int dcr_is_code_addr(uint32_t a);                               /* exc_handler.c */
size_t dcr_readable(uint32_t p, size_t want);                   /* exc_handler.c */
uint64_t dcr_boot_frames(void);                                 /* dcr_boot.c */
int dcr_boot_in_focus(void);                                    /* dcr_boot.c */
uint32_t dcr_jit_flushes(void);                                 /* jit_arena.c */
uint32_t dcr_exc_jit_stores(void);                              /* exc_handler.c */
uint32_t dcr_signal_count(void);                                /* exc_handler.c */
uint32_t dcr_gc_suspends(void);                                 /* mono_rt.c */
uint32_t dcr_gl_frames(void);                                   /* gl_mesa.c */
const char *dcr_game_root(void);                                /* main.c */

/* The kernel's ThreadContext (svcGetThreadContext3); for an AArch32 thread
 * r[0..14] hold r0-r14. Same layout as mono_rt.c. */
typedef struct {
  uint64_t r[29];
  uint64_t fp, lr, sp, pc;
  uint32_t psr, _pad;
  uint8_t v[32][16];
  uint32_t fpcr, fpsr;
  uint64_t tpidr;
} KCtx;
_Static_assert(sizeof(KCtx) == 0x320, "kernel ThreadContext is 0x320 bytes");

static Result get_ctx(KCtx *ctx, Handle h) {
  register uint32_t r0 __asm__("r0") = (uint32_t)(uintptr_t)ctx;
  register uint32_t r1 __asm__("r1") = h;
  /* The 32-bit SVC ABI returns with r1-r3 zeroed: they must be clobbers, or
   * the compiler keeps live values there (the watchdog's first report died on
   * a pointer it had parked in r3). */
  __asm__ volatile("svc 0x33" : "+r"(r0), "+r"(r1) : : "r2", "r3", "r12", "lr", "memory");
  return r0;
}

#define MAX_SNAP 48
#define MAX_RET 40
typedef struct {
  char name[16];
  uint64_t cpu_ms;           /* CPU time the thread has run, all told */
  int tid, ok, main, gc_paused;
  s32 prio;
  u64 cores;
  uint32_t pc, lr, sp, psr;
  uint32_t ret[MAX_RET];
  int nret;
} Snap;

static Snap g_snap[MAX_SNAP];
static int g_nsnap;
static Handle g_main_thread;

/* A word that could be a return address: a code address right after a call. */
static int is_return_addr(uint32_t v) {
  if (!dcr_is_code_addr(v & ~1u) || dcr_readable((v & ~3u) - 4, 4) != 4)
    return 0;
  if (!(v & 1)) {
    if (v & 3)
      return 0;
    uint32_t w = *(const volatile uint32_t *)(v - 4);
    return ((w & 0x0F000000u) == 0x0B000000u && (w >> 28) != 0xF) /* bl */
        || (w & 0x0FFFFFF0u) == 0x012FFF30u                         /* blx reg */
        || (w >> 25) == 0x7Du;                                      /* blx imm */
  }
  const uint16_t *h = (const uint16_t *)(uintptr_t)((v & ~1u) - 4);
  return ((h[0] & 0xF800u) == 0xF000u && (h[1] & 0xC000u) == 0xC000u) /* bl/blx T1/T2 */
      || (h[1] & 0xFF87u) == 0x4780u;                                  /* blx reg */
}

static void snap_one(BThread *t, void *arg) {
  if (g_nsnap >= MAX_SNAP || t->handle == INVALID_HANDLE || t->finished)
    return;
  Snap *s = &g_snap[g_nsnap++];
  memset(s, 0, sizeof *s);
  s->tid = t->tid;
  s->main = t->handle == g_main_thread;
  memcpy(s->name, t->name, sizeof s->name);
  s->name[sizeof s->name - 1] = 0;
  u64 ticks = 0;
  if (R_FAILED(svcGetInfo(&ticks, InfoType_ThreadTickCount, t->handle, TickCountInfo_Total)))
    svcGetInfo(&ticks, InfoType_ThreadTickCountDeprecated, t->handle, TickCountInfo_Total);
  s->cpu_ms = armTicksToNs(ticks) / 1000000ull;
  s->gc_paused = t->gc_paused;
  s->prio = -1;
  svcGetThreadPriority(&s->prio, t->handle);
  dcr_thread_get_cores(t->handle, NULL, &s->cores);
  /* A thread the GC bridge has paused stays paused; everything else is
   * paused just long enough to read it. Lock order everywhere: the thread
   * list (b_thread_foreach), then the pause lock (here, dcr_prof.c). */
  b_pause_lock();
  /* Ryujinx 1.1.1098 crashed (host segfault) pausing one of the engine's
   * threads: under the emulator only a thread that is burning CPU (a spin:
   * what a hang report is for) is paused to be read. */
  if (dcr_is_emulator()) {
    /* No pause (Ryujinx blocks in it for a spinning thread, or crashes): the
     * code addresses on the thread's whole stack, outermost first -- live
     * frames and stale ones alike, the live ones nearer the top. */
    uintptr_t lo = (uintptr_t)t->stack_base, hi = lo + t->stack_size;
    if (lo) {
      uintptr_t start = hi;
      while (start > lo && dcr_readable(start - 0x1000, 0x1000) == 0x1000 && hi - start < 0x40000)
        start -= 0x1000;
      /* innermost first: from the lowest mapped word up; only words that are
       * return addresses (a BL/BLX right before them) */
      for (uintptr_t a = start; a + 4 <= hi && s->nret < MAX_RET; a += 4) {
        uint32_t v = *(const volatile uint32_t *)a;
        if (is_return_addr(v) && (!s->nret || s->ret[s->nret - 1] != v))
          s->ret[s->nret++] = v;
      }
      s->ok = 2;
    }
    b_pause_unlock();
    return;
  }
  int paused_here = !t->gc_paused && R_SUCCEEDED(svcSetThreadActivity(t->handle, ThreadActivity_Paused));
  KCtx ctx;
  if (R_SUCCEEDED(get_ctx(&ctx, t->handle))) {
    s->ok = 1;
    s->pc = (uint32_t)ctx.pc;
    s->lr = (uint32_t)ctx.r[14];
    s->sp = (uint32_t)ctx.r[13];
    s->psr = ctx.psr;
    uintptr_t lo = (uintptr_t)t->stack_base, hi = lo + t->stack_size;
    if (lo && s->sp >= lo && s->sp < hi) {
      uintptr_t end = (s->sp & ~3u) + dcr_readable(s->sp & ~3u, 0x3000); /* mapped only */
      for (uintptr_t a = s->sp & ~3u; a + 4 <= hi && a + 4 <= end && s->nret < MAX_RET; a += 4) {
        uint32_t v = *(const volatile uint32_t *)a;
        if (dcr_is_code_addr(v & ~1u))
          s->ret[s->nret++] = v;
      }
    }
  }
  if (paused_here)
    svcSetThreadActivity(t->handle, ThreadActivity_Runnable);
  b_pause_unlock();
}

/* ------------------------------------------------ emergency (lock-free) */
/* When the log lock or the thread list's lock is still taken after a second,
 * the normal report would block on it too (a hang that took the log with it:
 * nothing at all after the last line). This path takes no lock: it prints
 * straight to svcOutputDebugString (an emulator's log; a debugger) -- not to
 * a file: newlib's stdio locks may be held by a paused thread too -- then
 * dumps every thread. */
#include <stdarg.h>
static FILE *g_raw_fp;
static void raw(const char *fmt, ...) {
  char buf[600];
  va_list ap;
  va_start(ap, fmt);
  int n = b_vsnprintf(buf, sizeof buf, fmt, ap);
  va_end(ap);
  if (n < 0)
    return;
  if (n >= (int)sizeof buf)
    n = sizeof buf - 1;
  svcOutputDebugString(buf, (size_t)n);
  if (g_raw_fp) {
    fwrite(buf, 1, (size_t)n, g_raw_fp);
    fputc('\n', g_raw_fp);
    fflush(g_raw_fp);
  }
}

static void raw_one(BThread *t, void *arg) {
  if (t->handle == INVALID_HANDLE || t->finished)
    return;
  /* Ryujinx 1.1.1098 crashed (host segfault) pausing one of the engine's
   * threads: under the emulator the context is read without a pause. */
  int paused_here = !t->gc_paused && !dcr_is_emulator() &&
                    R_SUCCEEDED(svcSetThreadActivity(t->handle, ThreadActivity_Paused));
  KCtx ctx;
  uint32_t ret[MAX_RET];
  int nret = 0, ok = R_SUCCEEDED(get_ctx(&ctx, t->handle));
  if (ok) {
    uintptr_t lo = (uintptr_t)t->stack_base, hi = lo + t->stack_size, sp = (uint32_t)ctx.r[13];
    if (lo && sp >= lo && sp < hi) {
      uintptr_t end = (sp & ~3u) + dcr_readable(sp & ~3u, 0x3000);
      for (uintptr_t a = sp & ~3u; a + 4 <= hi && a + 4 <= end && nret < MAX_RET; a += 4) {
        uint32_t v = *(const volatile uint32_t *)a;
        if (dcr_is_code_addr(v & ~1u))
          ret[nret++] = v;
      }
    }
  }
  if (paused_here)
    svcSetThreadActivity(t->handle, ThreadActivity_Runnable);
  if (!ok) {
    raw("[watchdog!] tid %d handle 0x%x: no context", t->tid, (unsigned)t->handle);
    return;
  }
  char a[64], b[64], line[400];
  int n = 0;
  for (int k = 0; k < nret && n < (int)sizeof line - 64; k++)
    n += snprintf(line + n, sizeof line - n, " %s", dcr_addr_name(ret[k] & ~1u, a, sizeof a));
  line[n] = 0;
  raw("[watchdog!] tid %d handle 0x%x%s%s: pc %s lr %s | stack:%s", t->tid, (unsigned)t->handle,
      t->handle == g_main_thread ? " (main)" : "", t->gc_paused ? " (GC-paused)" : "",
      dcr_addr_name((uint32_t)ctx.pc, a, sizeof a), dcr_addr_name((uint32_t)ctx.r[14] & ~1u, b, sizeof b), line);
}

/* 1 when a lock stayed taken for a second: the emergency report was made. */
static int emergency(unsigned secs) {
  for (int i = 0; i < 10; i++) {
    uint32_t r, p;
    b_lock_words(&r, &p);
    if (!log_lock_word() && !r && !p)
      return 0; /* all came free: the normal report can run */
    svcSleepThread(100000000ll);
  }
  uint32_t r, p;
  b_lock_words(&r, &p);
  /* debug output only: fopen/fwrite take newlib's locks, which a paused
   * thread may hold as well */
  raw("[watchdog!] no frame for %u s AND a lock is stuck: log lock 0x%08lx, thread list 0x%08lx, "
      "pause 0x%08lx (owner handle | 0x40000000 waiters)", secs, (unsigned long)log_lock_word(),
      (unsigned long)r, (unsigned long)p);
  b_thread_foreach_nolock(raw_one, NULL);
  return 1;
}

extern volatile Handle g_sample_want;               /* bionic_pthread.c */
extern volatile uint32_t g_sample_sp, g_sample_lr, g_sample_n;
uint32_t jit_arena_base(void);
uint32_t jit_arena_size(void);

/* Emulator: the main thread's stack as a file (stack_main.bin: a header of
 * "DCRS", the stack's top address, the module bases, then the words from the
 * lowest mapped page to the top) for tools/stackdump.py on the host. The
 * emulator cannot pause a spinning thread for its registers. */
extern so_module main_mod, unity_mod, mono_mod;
static void dump_main_stack(BThread *t, void *arg) {
  if (t->handle != g_main_thread || !t->stack_base)
    return;
  uintptr_t lo = (uintptr_t)t->stack_base, hi = lo + t->stack_size, start = hi;
  while (start > lo && dcr_readable(start - 0x1000, 0x1000) == 0x1000 && hi - start < 0x80000)
    start -= 0x1000;
  char path[300];
  snprintf(path, sizeof path, "%s/stack_main.bin", dcr_game_root());
  FILE *f = fopen(path, "wb");
  if (!f)
    return;
  extern char _start[];
  const uint32_t hdr[8] = {0x53524344u, (uint32_t)hi, (uint32_t)start, (uint32_t)(uintptr_t)_start,
                           (uint32_t)(uintptr_t)main_mod.load_virtbase, (uint32_t)(uintptr_t)unity_mod.load_virtbase,
                           (uint32_t)(uintptr_t)mono_mod.load_virtbase, g_sample_sp};
  fwrite(hdr, 4, 8, f);
  fwrite((const void *)start, 1, hi - start, f);
  fclose(f);
  /* and the JIT code in use (up to 4 MB), for the managed frames */
  snprintf(path, sizeof path, "%s/jit_arena.bin", dcr_game_root());
  f = fopen(path, "wb");
  if (f) {
    uint32_t base = jit_arena_base(), size = jit_arena_size();
    if (size > (4u << 20))
      size = 4u << 20;
    fwrite(&base, 4, 1, f);
    fwrite((const void *)(uintptr_t)base, 1, dcr_readable(base, size), f);
    fclose(f);
  }
}


static void report(uint64_t frames, unsigned secs) {
  if (dcr_is_emulator() && secs < 20) {
    /* ask the main thread where it is, next time it calls a sampled shim */
    uint32_t n0 = g_sample_n;
    g_sample_want = g_main_thread;
    for (int i = 0; i < 20 && g_sample_n == n0; i++)
      svcSleepThread(50000000ll);
    g_sample_want = 0;
    debugPrintf("[watchdog] main thread sample: %s sp %08lx, shim called from %08lx\n",
                g_sample_n != n0 ? "taken:" : "NOT taken (it calls no sampled shim):",
                (unsigned long)g_sample_sp, (unsigned long)g_sample_lr);
    b_thread_foreach(dump_main_stack, NULL);
  }
  if (emergency(secs))
    return;
  /* Activity since the previous report: rising counters mean slow, not stuck. */
  static uint32_t p_fl, p_st, p_sig, p_gc, p_pr;
  uint32_t fl = dcr_jit_flushes(), st = dcr_exc_jit_stores(), sig = dcr_signal_count();
  uint32_t gc = dcr_gc_suspends(), pr = dcr_gl_frames();
  g_nsnap = 0;
  b_thread_foreach(snap_one, NULL); /* no logging in here */
  debugPrintf("[watchdog] no frame finished for %u s (last frame %llu), %d threads. Since the "
              "last report: presented +%lu, JIT flushes +%lu (%lu), emulated JIT stores +%lu, "
              "signals +%lu, GC suspends +%lu\n",
              secs, (unsigned long long)frames, g_nsnap, (unsigned long)(pr - p_pr),
              (unsigned long)(fl - p_fl), (unsigned long)fl, (unsigned long)(st - p_st),
              (unsigned long)(sig - p_sig), (unsigned long)(gc - p_gc));
  p_fl = fl, p_st = st, p_sig = sig, p_gc = gc, p_pr = pr;
  for (int i = 0; i < g_nsnap; i++) {
    const Snap *s = &g_snap[i];
    char a[64], b[64];
    if (s->ok == 2) { /* emulator: stack scan only */
      char line[1400];
      int n = 0;
      for (int k = 0; k < s->nret && n < (int)sizeof line - 64; k++) {
        char nm[64];
        n += snprintf(line + n, sizeof line - n, " %s", dcr_addr_name(s->ret[k] & ~1u, nm, sizeof nm));
      }
      debugPrintf("[watchdog]  tid %d%s \"%s\" CPU %llu ms; return addresses on its stack, innermost "
                  "first (live and stale frames):%s\n", s->tid, s->main ? " (main)" : "", s->name, (unsigned long long)s->cpu_ms, line);
      continue;
    }
    if (!s->ok) {
      debugPrintf("[watchdog]  tid %d%s \"%s\": no context; CPU %llu ms so far\n", s->tid,
                  s->main ? " (main)" : "", s->name, (unsigned long long)s->cpu_ms);
      continue;
    }
    debugPrintf("[watchdog]  tid %d%s%s (prio %d, cores 0x%llx): pc %08lx %s%s | lr %08lx %s | "
                "sp %08lx\n",
                s->tid, s->main ? " (main)" : "", s->gc_paused ? " (GC-paused)" : "", (int)s->prio,
                (unsigned long long)s->cores, (unsigned long)s->pc, dcr_addr_name(s->pc, a, sizeof a),
                (s->psr & 0x20) ? " T" : "", (unsigned long)s->lr, dcr_addr_name(s->lr & ~1u, b, sizeof b),
                (unsigned long)s->sp);
    char line[512];
    int n = 0;
    for (int k = 0; k < s->nret && n < (int)sizeof line - 64; k++)
      n += snprintf(line + n, sizeof line - n, " %s", dcr_addr_name(s->ret[k] & ~1u, a, sizeof a));
    if (s->nret)
      debugPrintf("[watchdog]    stack:%s\n", line);
  }
  log_flush_ring();
}

/* A system screen (the controller screen, the profile picker) holds the game's
 * main thread while it is up: no frames, and nothing wrong. */
static volatile int g_applet;
void dcr_applet_busy(int on) { g_applet = on; }
int dcr_applet_is_busy(void) { return g_applet; }

static void watchdog(void *arg) {
  uint64_t last = dcr_boot_frames(), since = armGetSystemTick();
  int reports = 0;
  for (unsigned tick = 1;; tick++) {
    svcSleepThread(1000000000ll);
    if (tick % 5 == 0)
      log_flush_ring();
    uint64_t f = dcr_boot_frames();
    uint64_t now = armGetSystemTick();
    if (f != last || !dcr_boot_in_focus() || g_applet) {
      last = f;
      since = now;
      reports = 0;
      continue;
    }
    unsigned secs = (unsigned)(armTicksToNs(now - since) / 1000000000ull);
    static const unsigned at[] = {10, 40, 100};
    if (reports == 0 && secs == 10) /* proof of life that takes no lock */
      svcOutputDebugString("[watchdog] 10 s without a frame: reporting", 41);
    if (reports < 3 && secs >= at[reports]) {
      report(f, secs);
      reports++;
    }
  }
}

void dcr_watchdog_start(void) {
  static Thread t;
  g_main_thread = envGetMainThreadHandle();
  /* The highest priority the NPDM allows, on core 2: it sleeps nearly all the
   * time, and a thread spinning at a high priority on core 0 (where the
   * port's own helper threads start) must not silence the one that would
   * report it. */
  if (R_SUCCEEDED(threadCreate(&t, watchdog, NULL, NULL, 0x8000, 0x1C, 2)))
    threadStart(&t);
  else
    debugPrintf("[watchdog] could not start\n");
}

/* Emulator: a jump into JIT memory Mono never wrote (jit_arena.c fills it
 * with calls to a stub that lands here). regs: r0-r12 and lr as pushed; the
 * stray address is lr - 4, and the stack pointer at the jump is just above
 * them -- so this stack dump holds the live frames only. No locks. */
void dcr_emu_stray(uint32_t *regs) {
  char buf[400];
  const uint32_t sp = (uint32_t)(uintptr_t)(regs + 14);
  int n = snprintf(buf, sizeof buf, "[jit] STRAY JUMP into unwritten JIT memory at %08lx (thread 0x%x): "
                   "r0 %08lx r1 %08lx r2 %08lx r3 %08lx r4 %08lx r11 %08lx r12 %08lx sp %08lx",
                   (unsigned long)(regs[13] - 4), (unsigned)threadGetCurHandle(), (unsigned long)regs[0],
                   (unsigned long)regs[1], (unsigned long)regs[2], (unsigned long)regs[3], (unsigned long)regs[4],
                   (unsigned long)regs[11], (unsigned long)regs[12], (unsigned long)sp);
  svcOutputDebugString(buf, n > 0 ? (size_t)n : 0);
  BThread *t = b_thread_self();
  if (t && t->stack_base) {
    char path[300];
    snprintf(path, sizeof path, "%s/stack_stray.bin", dcr_game_root());
    FILE *f = fopen(path, "wb");
    if (f) {
      extern char _start[];
      uint32_t hi = (uint32_t)(uintptr_t)t->stack_base + t->stack_size;
      const uint32_t hdr[8] = {0x53524344u, hi, sp, (uint32_t)(uintptr_t)_start,
                               (uint32_t)(uintptr_t)main_mod.load_virtbase, (uint32_t)(uintptr_t)unity_mod.load_virtbase,
                               (uint32_t)(uintptr_t)mono_mod.load_virtbase, regs[13] - 4};
      fwrite(hdr, 4, 8, f);
      fwrite((const void *)(uintptr_t)sp, 1, hi - sp, f);
      fclose(f);
      snprintf(path, sizeof path, "%s/jit_arena.bin", dcr_game_root());
      f = fopen(path, "wb");
      if (f) {
        uint32_t base = jit_arena_base(), size = 4u << 20;
        fwrite(&base, 4, 1, f);
        fwrite((const void *)(uintptr_t)base, 1, dcr_readable(base, size), f);
        fclose(f);
      }
    }
  }
  svcOutputDebugString("[jit] stray-jump report written; this thread stops here", 55);
  for (;;)
    svcSleepThread(1000000000ll);
}
