/* bionic_wchar.c -- multibyte conversion and syscall().
 *
 * mbstate_t: bionic's is 4 bytes on 32-bit, newlib's is 8. Handing a game-owned
 * mbstate_t to newlib's mbrtowc would write 4 bytes past it, so conversion is
 * done here. bionic's multibyte encoding is always UTF-8 (its "C" locale too),
 * so this is a restartable UTF-8 codec keeping at most 3 pending bytes in the
 * game's 4-byte state: seq[0..2] = bytes, seq[3] = how many.
 *
 * syscall(): the engine and Mono use the raw syscall entry for exactly these
 * (found at the call sites, ARM EABI numbers):
 *   libunity  241 sched_setaffinity / 242 sched_getaffinity  (tid, 4, &mask)
 *   libmono   316 inotify_init / 317 inotify_add_watch / 318 inotify_rm_watch
 * plus gettid, which libraries commonly reach this way. MIT.
 */
#include <stdarg.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

#include "bionic.h"
#include "bionic_pthread.h"
#include "util.h"

typedef struct { uint8_t seq[4]; } b_mbstate_t;

static b_mbstate_t g_mbrtowc_state, g_wcrtomb_state;

static int utf8_len(uint8_t b) {
  if (b < 0x80) return 1;
  if (b < 0xc2) return -1;
  if (b < 0xe0) return 2;
  if (b < 0xf0) return 3;
  if (b < 0xf5) return 4;
  return -1;
}

size_t b_mbrtowc(uint32_t *pwc, const char *s, size_t n, b_mbstate_t *ps) {
  if (!ps)
    ps = &g_mbrtowc_state;
  if (!s) {
    memset(ps, 0, sizeof *ps);
    return 0;
  }
  if (n == 0)
    return (size_t)-2;

  uint8_t buf[4];
  int have = ps->seq[3];
  if (have > 3)
    have = 0;
  memcpy(buf, ps->seq, (size_t)have);
  int need = have ? utf8_len(buf[0]) : utf8_len((uint8_t)s[0]);
  if (need < 0) {
    memset(ps, 0, sizeof *ps);
    b_set_errno(L_EILSEQ);
    return (size_t)-1;
  }
  size_t used = 0;
  while (have < need && used < n)
    buf[have++] = (uint8_t)s[used++];
  if (have < need) {
    memcpy(ps->seq, buf, (size_t)have);
    ps->seq[3] = (uint8_t)have;
    return (size_t)-2;
  }
  memset(ps, 0, sizeof *ps);

  uint32_t c;
  switch (need) {
  case 1: c = buf[0]; break;
  case 2: c = (buf[0] & 0x1fu) << 6 | (buf[1] & 0x3fu); break;
  case 3: c = (buf[0] & 0x0fu) << 12 | (buf[1] & 0x3fu) << 6 | (buf[2] & 0x3fu); break;
  default: c = (buf[0] & 0x07u) << 18 | (buf[1] & 0x3fu) << 12 | (buf[2] & 0x3fu) << 6 | (buf[3] & 0x3fu); break;
  }
  for (int i = 1; i < need; i++)
    if ((buf[i] & 0xc0) != 0x80)
      goto bad;
  if ((need == 3 && (c < 0x800 || (c >= 0xd800 && c <= 0xdfff))) || (need == 4 && (c < 0x10000 || c > 0x10ffff)))
    goto bad;
  if (pwc)
    *pwc = c;
  return c ? used : 0;
bad:
  b_set_errno(L_EILSEQ);
  return (size_t)-1;
}

size_t b_wcrtomb(char *s, uint32_t wc, b_mbstate_t *ps) {
  if (!ps)
    ps = &g_wcrtomb_state;
  memset(ps, 0, sizeof *ps);
  if (!s)
    return 1; /* wcrtomb(NULL, ...) == wcrtomb(buf, L'\0', ...) */
  if (wc < 0x80) {
    s[0] = (char)wc;
    return 1;
  }
  if (wc < 0x800) {
    s[0] = (char)(0xc0 | wc >> 6);
    s[1] = (char)(0x80 | (wc & 0x3f));
    return 2;
  }
  if (wc >= 0xd800 && wc <= 0xdfff)
    goto bad;
  if (wc < 0x10000) {
    s[0] = (char)(0xe0 | wc >> 12);
    s[1] = (char)(0x80 | ((wc >> 6) & 0x3f));
    s[2] = (char)(0x80 | (wc & 0x3f));
    return 3;
  }
  if (wc <= 0x10ffff) {
    s[0] = (char)(0xf0 | wc >> 18);
    s[1] = (char)(0x80 | ((wc >> 12) & 0x3f));
    s[2] = (char)(0x80 | ((wc >> 6) & 0x3f));
    s[3] = (char)(0x80 | (wc & 0x3f));
    return 4;
  }
bad:
  b_set_errno(L_EILSEQ);
  return (size_t)-1;
}

uint32_t b_btowc(int c) { return (c == -1 || c > 0x7f || c < 0) ? 0xffffffffu /* WEOF */ : (uint32_t)c; }
int b_wctob(uint32_t c) { return c < 0x80 ? (int)c : -1; }

/* ------------------------------------------------------------- syscall */
#define NR_gettid 224
#define NR_sched_setaffinity 241
#define NR_sched_getaffinity 242
#define NR_inotify_init 316
#define NR_inotify_add_watch 317
#define NR_inotify_rm_watch 318

long b_syscall(long nr, ...) {
  va_list ap;
  va_start(ap, nr);
  long a1 = va_arg(ap, long), a2 = va_arg(ap, long), a3 = va_arg(ap, long);
  va_end(ap);
  (void)a1;
  switch (nr) {
  case NR_gettid:
    return b_gettid();
  case NR_sched_setaffinity:
    return 0;
  case NR_sched_getaffinity:
    /* returns the number of bytes written to the mask, as the raw syscall does */
    if (a3 && a2 >= 4) {
      memset((void *)a3, 0, (size_t)a2);
      *(uint32_t *)a3 = 0x7; /* the three application cores */
      return 4;
    }
    b_set_errno(L_EINVAL);
    return -1;
  case NR_inotify_init:
  case NR_inotify_add_watch:
  case NR_inotify_rm_watch:
    b_set_errno(L_ENOSYS); /* Mono falls back to polling FileSystemWatcher */
    return -1;
  default: {
    static int logged;
    if (logged++ < 16)
      debugPrintf("[syscall] unhandled syscall(%ld) from %p\n", nr, __builtin_return_address(0));
    b_set_errno(L_ENOSYS);
    return -1;
  }
  }
}
