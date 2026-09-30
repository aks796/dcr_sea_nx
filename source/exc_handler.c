/* exc_handler.c -- what happens when a thread faults (entered from exc32.S).
 *
 * 1. JIT STORES. Mono's code lives in the JIT arena's execute-only view (rx);
 *    the same pages are writable at another address (rw, jit_arena.c). Bulk
 *    code arrives by memcpy, which the shim already sends to rw -- mono_codegen
 *    compiles into a malloc'd buffer and memcpys the method into place (read
 *    out of libmono: reserve -> memcpy -> patch_code -> flush). What remains
 *    are individual stores: patch_code's call/branch fixups, trampolines
 *    emitted in place, call sites patched on first call. Each one data-aborts
 *    on rx; this file decodes the store, performs it on rw, cleans the line,
 *    and resumes after the instruction. ARM state only: libmono is ARM code.
 *
 * 2. EVERYTHING ELSE is a crash: a report (registers, module+offset of pc/lr,
 *    a return-address scan of the stack) goes to svcOutputDebugString and to
 *    <root>/crash.log, then a failure result hands the fault back to the
 *    kernel, which kills the process and lets Atmosphere write its own report.
 *    The report path takes no newlib or port locks -- the faulting thread may
 *    hold any of them -- and writes the file through the FS service directly.
 * MIT.
 */
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <switch.h>

#include "arm_store_emu.h"
#include "bionic.h"
#include "jit_arena.h"
#include "so_util.h"
#include "util.h"
int b_vsnprintf(char *buf, size_t n, const char *fmt, va_list ap); /* bionic_printf.c: NULL-safe */

/* ams::svc::aarch32::ExceptionInfo, with the 64-bit-kernel status block. */
typedef struct {
  uint32_t r[8];
  uint32_t sp, lr, pc, flags;
  uint32_t pstate, afsr0, afsr1, esr, far;
} ExcInfo32;

typedef struct {
  uint32_t fpscr, pad;
  uint64_t d16_31[16];
  uint64_t d0_15[16];
  uint32_t r8_12[5];
  uint32_t lr_copy;
} ExcFrame;
_Static_assert(sizeof(ExcFrame) == 288, "must match exc32.S");

#define EXC_INSTRUCTION_ABORT 0x100
#define EXC_DATA_ABORT 0x101
#define EXC_UNALIGNED_INSTRUCTION 0x102
#define EXC_UNALIGNED_DATA 0x103
#define EXC_UNDEFINED_INSTRUCTION 0x104
#define EXC_EXCEPTION_INSTRUCTION 0x105
#define EXC_MEMORY_SYSTEM_ERROR 0x106
#define EXC_FPU 0x200
#define EXC_INVALID_SYSCALL 0x301
#define EXC_SYSCALL_BREAK 0x302

static volatile uint32_t g_jit_stores;
uint32_t dcr_exc_jit_stores(void) { return g_jit_stores; }

/* ------------------------------------------------------------ registers */
static uint32_t *reg_slot(ExcInfo32 *i, ExcFrame *f, unsigned n) {
  if (n < 8) return &i->r[n];
  if (n < 13) return &f->r8_12[n - 8];
  if (n == 13) return &i->sp;
  if (n == 14) return &i->lr;
  return &i->pc;
}
static uint32_t get_r(ExcInfo32 *i, ExcFrame *f, unsigned n) { return *reg_slot(i, f, n); }

/* ---------------------------------------------------------------- stores */
/* The JIT's rx view goes to rw. A store elsewhere in the same instruction (an
 * STM that also touches the stack, say) is plain memory and written directly.
 * No cache maintenance per store: Mono calls mono_arch_flush_icache (hooked to
 * jit_flush) after writing code, as it must on ARM Linux too. */
static int jit_put(void *ctx, uint32_t addr, const void *src, size_t n) {
  const void *p = (const void *)(uintptr_t)addr;
  int in_jit = jit_range_contains(p, n);
  if (in_jit && (!jit_contains(p) || !jit_contains((const char *)p + n - 1)))
    return 0; /* straddles the arena edge: not something Mono does */
  if (!src)
    return 1;
  volatile uint8_t *d = in_jit ? jit_rw(p) : (volatile uint8_t *)(uintptr_t)addr;
  for (size_t k = 0; k < n; k++)
    d[k] = ((const uint8_t *)src)[k];
  return 1;
}

static int emulate_store(ExcInfo32 *i, ExcFrame *f) {
  ArmRegs s;
  for (unsigned n = 0; n < 16; n++)
    s.r[n] = get_r(i, f, n);
  for (unsigned n = 0; n < 16; n++) {
    s.d[n] = f->d0_15[n];
    s.d[n + 16] = f->d16_31[n];
  }
  s.cpsr = i->pstate;
  if (!arm_emulate_store(*(const volatile uint32_t *)(uintptr_t)i->pc, &s, jit_put, NULL))
    return 0;
  for (unsigned n = 0; n < 15; n++)
    *reg_slot(i, f, n) = s.r[n]; /* writeback and STREX status */
  return 1;
}

/* ---------------------------------------------------------- crash report */
extern char _start[];
extern char __rodata_start[] __attribute__((visibility("hidden"))); /* end of our .text (dcr32.ld) */
static char g_crash[4096];
static size_t g_crash_len;

static void out(const char *fmt, ...) {
  char line[256];
  va_list ap;
  va_start(ap, fmt);
  int n = b_vsnprintf(line, sizeof line, fmt, ap);
  va_end(ap);
  if (n <= 0)
    return;
  if ((size_t)n >= sizeof line)
    n = sizeof line - 1;
  svcOutputDebugString(line, (u32)n);
  if (g_crash_len + (size_t)n < sizeof g_crash) {
    memcpy(g_crash + g_crash_len, line, (size_t)n);
    g_crash_len += (size_t)n;
  }
}

static const char *where(uint32_t a, char *buf, size_t cap) {
  so_module *m = so_find_module_by_addr((const void *)a);
  if (m) {
    const char *n = strrchr(m->name, '/') ? strrchr(m->name, '/') + 1 : m->name;
    snprintf(buf, cap, "%.40s+0x%lx", n, (unsigned long)(a - (uint32_t)(uintptr_t)m->load_virtbase));
  } else if (jit_contains((const void *)a)) {
    snprintf(buf, cap, "JIT code");
  } else if (a >= (uint32_t)(uintptr_t)_start && a < (uint32_t)(uintptr_t)__rodata_start) {
    snprintf(buf, cap, "dcrsea_nx+0x%lx", (unsigned long)(a - (uint32_t)(uintptr_t)_start));
  } else {
    snprintf(buf, cap, "?");
  }
  return buf;
}

static int is_code(uint32_t a) {
  so_module *m = so_find_module_by_addr((const void *)a);
  if (m)
    return 1;
  return jit_contains((const void *)a) ||
         (a >= (uint32_t)(uintptr_t)_start && a < (uint32_t)(uintptr_t)__rodata_start);
}

/* For the watchdog (watchdog.c). */
const char *dcr_addr_name(uint32_t a, char *buf, size_t cap) { return where(a, buf, cap); }
int dcr_is_code_addr(uint32_t a) { return is_code(a); }

/* How many bytes from p on are mapped and readable (up to the end of p's
 * memory region), at most `want`. Another thread's stack is read only this
 * far: its recorded bounds can be wider than what is mapped (hardware
 * 2026-09-24: the profiler died reading past a stack's last page). */
size_t dcr_readable(uint32_t p, size_t want) {
  MemoryInfo mi;
  u32 pi;
  if (R_FAILED(svcQueryMemory(&mi, &pi, p)) || !(mi.perm & Perm_R) || mi.type == MemType_Unmapped)
    return 0;
  uint64_t end = mi.addr + mi.size;
  if (end <= p)
    return 0;
  return end - p < want ? (size_t)(end - p) : want;
}

static void write_crash_file(void) {
  FsFileSystem *fs = fsdevGetDeviceFileSystem("sdmc");
  if (!fs)
    return;
  static const char path[] = "/switch/dcr_sea_nx/crash.log";
  fsFsCreateFile(fs, path, 0, 0);
  FsFile file;
  if (R_FAILED(fsFsOpenFile(fs, path, FsOpenMode_Write | FsOpenMode_Append, &file)))
    return;
  s64 size = 0;
  fsFileGetSize(&file, &size);
  fsFileWrite(&file, size, g_crash, g_crash_len, FsWriteOption_Flush);
  fsFileClose(&file);
}

static const char *type_name(uint32_t t) {
  switch (t) {
  case EXC_INSTRUCTION_ABORT: return "instruction abort";
  case EXC_DATA_ABORT: return "data abort";
  case EXC_UNALIGNED_INSTRUCTION: return "unaligned instruction";
  case EXC_UNALIGNED_DATA: return "unaligned data";
  case EXC_UNDEFINED_INSTRUCTION: return "undefined instruction";
  case EXC_EXCEPTION_INSTRUCTION: return "exception instruction";
  case EXC_MEMORY_SYSTEM_ERROR: return "memory system error";
  case EXC_FPU: return "FPU exception";
  case EXC_INVALID_SYSCALL: return "invalid system call";
  case EXC_SYSCALL_BREAK: return "svcBreak";
  default: return "exception";
  }
}

static void crash_report(uint32_t type, ExcInfo32 *i, ExcFrame *f) {
  char w1[64], w2[64];
  u64 tid = 0;
  svcGetThreadId(&tid, CUR_THREAD_HANDLE);
  g_crash_len = 0;
  out("\n[crash] ===== %s (0x%lx) in thread %llu =====\n", type_name(type), (unsigned long)type,
      (unsigned long long)tid);
  out("[crash] pc  %08lx  %s%s\n", (unsigned long)i->pc, where(i->pc, w1, sizeof w1),
      (i->pstate & 0x20) ? " (Thumb)" : "");
  out("[crash] lr  %08lx  %s\n", (unsigned long)i->lr, where(i->lr, w2, sizeof w2));
  out("[crash] far %08lx  esr %08lx  pstate %08lx\n", (unsigned long)i->far, (unsigned long)i->esr,
      (unsigned long)i->pstate);
  for (unsigned r = 0; r < 13; r += 4) {
    char line[128];
    int n = 0;
    for (unsigned k = r; k < r + 4 && k < 13; k++)
      n += snprintf(line + n, sizeof line - n, " r%-2u %08lx", k, (unsigned long)get_r(i, f, k));
    out("[crash]%s\n", line);
  }
  out("[crash] sp  %08lx\n", (unsigned long)i->sp);
  if (!(i->pstate & 0x20) && is_code(i->pc))
    out("[crash] insn %08lx\n", (unsigned long)*(const volatile uint32_t *)i->pc);

  /* Return addresses on the stack (heuristic: words that point into code). */
  MemoryInfo mi;
  u32 pi;
  if (R_SUCCEEDED(svcQueryMemory(&mi, &pi, i->sp)) && mi.perm & Perm_R) {
    uint32_t end = (uint32_t)(mi.addr + mi.size), shown = 0;
    for (uint32_t a = i->sp & ~3u; a + 4 <= end && a < i->sp + 0x2000 && shown < 16; a += 4) {
      uint32_t v = *(const volatile uint32_t *)a;
      if (is_code(v & ~1u)) {
        out("[crash]   #%-2lu %08lx  %s\n", (unsigned long)shown, (unsigned long)v, where(v & ~1u, w1, sizeof w1));
        shown++;
      }
    }
  }
  /* Log lines still in the RAM ring (quiet mode, see dcr_boot.c). */
  static char tail[4096];
  if (log_ring_tail(tail, sizeof tail))
    out("[crash] last log lines not yet in debug.log:\n%s\n", tail);
  write_crash_file();
}

/* ---------------------------------------------------------------- signals
 * Mono's JIT emits no explicit null checks: a managed null dereference faults,
 * and on Android the kernel delivers SIGSEGV to the handler Mono installed,
 * which rewrites the signal context to resume in its exception-throwing code
 * (mono_arch_handle_exception) -> a C# NullReferenceException the game can
 * catch. So faults in JIT code are delivered the same way here:
 *   1. build a Linux ARM siginfo_t + ucontext_t (core registers and the VFP
 *      block in uc_regspace) on the faulting thread's own stack;
 *   2. resume the thread in dcr_signal_run(sig, info, uc, handler), which calls
 *      the handler in normal (not exception) context, as Linux does;
 *   3. dcr_signal_run ends in dcr_sigreturn (exc32.S), an undefined
 *      instruction that traps back here: the thread resumes from *uc.
 * Faults elsewhere (native code) still get the crash report: Mono would only
 * abort on those, and the report says more. A fault inside a handler is fatal,
 * as it is on Linux when the signal is blocked. */
typedef struct {
  uint32_t trap_no, error_code, oldmask;
  uint32_t arm_r[11];                     /* r0-r10 */
  uint32_t arm_fp, arm_ip, arm_sp, arm_lr, arm_pc, arm_cpsr;
  uint32_t fault_address;
} LSigContext;

typedef struct {
  uint32_t uc_flags, uc_link;
  uint32_t ss_sp, ss_flags, ss_size;      /* stack_t */
  LSigContext uc_mcontext;
  uint32_t uc_sigmask[32];                /* sigset_t + padding to 128 bytes */
  uint32_t uc_regspace[128] __attribute__((aligned(8)));
} LUContext;
_Static_assert(__builtin_offsetof(LUContext, uc_mcontext) == 20, "Linux ARM ucontext");
_Static_assert(__builtin_offsetof(LUContext, uc_regspace) == 232, "Linux ARM ucontext");

typedef struct {                          /* struct vfp_sigframe */
  uint32_t magic, size;
  uint64_t fpregs[32];
  uint32_t fpscr, pad;
  uint32_t fpexc, fpinst, fpinst2, pad2;
} LVfpFrame;
#define VFP_MAGIC 0x56465001u

typedef struct {
  int32_t si_signo, si_errno, si_code;
  uint32_t si_addr;
  uint32_t pad[28];                       /* siginfo_t is 128 bytes */
} LSigInfo;

typedef struct {
  LUContext uc;
  LSigInfo si;
} SigFrame;

extern char dcr_sigreturn[];               /* exc32.S */
int dcr_sigaction_get(int sig, struct b_sigaction *out); /* bionic_signal.c */

static __thread int t_in_signal;
static volatile uint32_t g_signals;
uint32_t dcr_signal_count(void) { return g_signals; }

void __attribute__((noreturn, noinline))
dcr_signal_run(int sig, LSigInfo *info, LUContext *uc, void (*handler)(int, void *, void *)) {
  uint32_t n = __atomic_add_fetch(&g_signals, 1, __ATOMIC_RELAXED);
  if (n <= 16 || n % 1000 == 0)
    debugPrintf("[signal] #%lu: signal %d at pc %08lx (fault address %08lx) -> handler %p\n",
                (unsigned long)n, sig, (unsigned long)uc->uc_mcontext.arm_pc,
                (unsigned long)info->si_addr, (void *)handler);
  handler(sig, info, uc);
  ((void (*)(LUContext *))(uintptr_t)dcr_sigreturn)(uc);
  __builtin_unreachable();
}

static int deliver_signal(uint32_t type, ExcInfo32 *i, ExcFrame *f) {
  const uint32_t ec = i->esr >> 26, fsc = i->esr & 0x3f;
  int sig, code;
  uint32_t addr = i->far;
  if ((type == EXC_INSTRUCTION_ABORT || type == EXC_UNDEFINED_INSTRUCTION) && ec == 0) {
    sig = L_SIGILL, code = 1 /* ILL_ILLOPC */, addr = i->pc;
  } else if (type == EXC_DATA_ABORT || type == EXC_INSTRUCTION_ABORT) {
    sig = L_SIGSEGV, code = (fsc >= 0x0c && fsc <= 0x0f) ? 2 /* SEGV_ACCERR */ : 1 /* SEGV_MAPERR */;
  } else if (type == EXC_UNALIGNED_DATA || type == EXC_UNALIGNED_INSTRUCTION) {
    sig = L_SIGBUS, code = 1 /* BUS_ADRALN */;
  } else {
    return 0;
  }
  if (!jit_contains((const void *)(uintptr_t)i->pc) || t_in_signal)
    return 0;
  struct b_sigaction act;
  if (!dcr_sigaction_get(sig, &act) || (uintptr_t)act.sa_handler_or_action <= 1)
    return 0; /* SIG_DFL / SIG_IGN: a real crash */

  /* The frame goes below the interrupted stack pointer, on a writable stack. */
  const uint32_t sp = (i->sp - (uint32_t)sizeof(SigFrame) - 64) & ~7u;
  MemoryInfo mi;
  u32 pi;
  if (R_FAILED(svcQueryMemory(&mi, &pi, sp)) || (mi.perm & Perm_Rw) != Perm_Rw ||
      (u64)i->sp > mi.addr + mi.size)
    return 0;
  SigFrame *fr = (SigFrame *)(uintptr_t)sp;
  memset(fr, 0, sizeof *fr);
  fr->si.si_signo = sig;
  fr->si.si_code = code;
  fr->si.si_addr = addr;
  LSigContext *m = &fr->uc.uc_mcontext;
  m->trap_no = 14;
  m->error_code = i->esr;
  for (unsigned r = 0; r < 11; r++)
    m->arm_r[r] = get_r(i, f, r);
  m->arm_fp = get_r(i, f, 11);
  m->arm_ip = get_r(i, f, 12);
  m->arm_sp = i->sp;
  m->arm_lr = i->lr;
  m->arm_pc = i->pc;
  m->arm_cpsr = i->pstate;
  m->fault_address = addr;
  fr->uc.ss_flags = 2; /* SS_DISABLE */
  LVfpFrame *vfp = (LVfpFrame *)fr->uc.uc_regspace;
  vfp->magic = VFP_MAGIC;
  vfp->size = sizeof *vfp;
  for (unsigned d = 0; d < 16; d++) {
    vfp->fpregs[d] = f->d0_15[d];
    vfp->fpregs[16 + d] = f->d16_31[d];
  }
  vfp->fpscr = f->fpscr;
  vfp->fpexc = 0x40000000u; /* EN */

  /* Resume in dcr_signal_run on the thread's stack. */
  uint32_t entry = (uint32_t)(uintptr_t)dcr_signal_run;
  i->r[0] = (uint32_t)sig;
  i->r[1] = (uint32_t)(uintptr_t)&fr->si;
  i->r[2] = (uint32_t)(uintptr_t)&fr->uc;
  i->r[3] = (uint32_t)(uintptr_t)act.sa_handler_or_action;
  i->sp = sp;
  i->lr = 0;
  i->pc = entry & ~1u;
  i->pstate &= ~0x0600fc20u;              /* IT state and T */
  if (entry & 1)
    i->pstate |= 0x20u;
  t_in_signal = 1;
  return 1;
}

/* The trap at dcr_sigreturn: resume from the context the handler left. */
static int do_sigreturn(ExcInfo32 *i, ExcFrame *f) {
  const LUContext *uc = (const LUContext *)(uintptr_t)i->r[0];
  const LSigContext *m = &uc->uc_mcontext;
  for (unsigned r = 0; r < 8; r++)
    i->r[r] = m->arm_r[r];
  f->r8_12[0] = m->arm_r[8];
  f->r8_12[1] = m->arm_r[9];
  f->r8_12[2] = m->arm_r[10];
  f->r8_12[3] = m->arm_fp;
  f->r8_12[4] = m->arm_ip;
  i->sp = m->arm_sp;
  i->lr = m->arm_lr;
  const uint32_t cpsr = m->arm_cpsr;
  i->pc = (cpsr & 0x20) ? (m->arm_pc & ~1u) : (m->arm_pc & ~3u);
  i->pstate = (i->pstate & ~0xFE0FFE20u) | (cpsr & 0xFE0FFE20u);
  const LVfpFrame *vfp = (const LVfpFrame *)uc->uc_regspace;
  if (vfp->magic == VFP_MAGIC) {
    for (unsigned d = 0; d < 16; d++) {
      f->d0_15[d] = vfp->fpregs[d];
      f->d16_31[d] = vfp->fpregs[16 + d];
    }
    f->fpscr = vfp->fpscr;
  }
  t_in_signal = 0;
  return 1;
}

/* ---------------------------------------------------------------- entry */
Result dcr_exception_dispatch(uint32_t type, ExcInfo32 *info, ExcFrame *frame) {
  if (info->pc == (uint32_t)(uintptr_t)dcr_sigreturn && !(info->pstate & 0x20) &&
      do_sigreturn(info, frame))
    return 0;
  if (type == EXC_DATA_ABORT && !(info->pstate & 0x20) && jit_is_split() &&
      jit_contains((const void *)info->far)) {
    if (emulate_store(info, frame)) {
      info->pc += 4;
      g_jit_stores++;
      return 0;
    }
  }
  if (deliver_signal(type, info, frame))
    return 0;
  crash_report(type, info, frame);
  return MAKERESULT(Module_Libnx, LibnxError_BadInput);
}
