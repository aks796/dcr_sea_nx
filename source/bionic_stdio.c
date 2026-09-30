/* bionic_stdio.c -- <stdio.h> for the bionic ABI.
 *
 * A FILE* the game holds is one of:
 *   - &__sF[0..2]   bionic's stdin/stdout/stderr (84-byte FILEs -- confirmed
 *                   in libmono: 85 uses of __sF+0xa8, 17 of __sF+0x54);
 *   - a newlib FILE* we returned from fopen()/fdopen().
 * The game never looks inside a FILE (no __srget/__swbuf imports), so a newlib
 * FILE* is safe to hand over opaque. Writes to stdout/stderr go to the log.
 *
 * Synthetic files (/proc/..., /dev/urandom, pipes) are fake fds in bionic_io.c;
 * fdopen()/fopen() on those build a newlib FILE over them with funopen(). That
 * matters because Unity's C++ ifstream goes open() -> fdopen(): the Drive Ahead
 * lineage lost a boot to a /proc/cpuinfo read that failed exactly there. MIT.
 */
#include <errno.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "bionic.h"
#include "bionic_io.h"
#include "dcr_path.h"
#include "util.h"

unsigned char b___sF[3 * B_FILE_SIZE];

typedef enum { S_NONE, S_IN, S_OUT, S_ERR } StdKind;

static StdKind std_kind(const void *fp) {
  const unsigned char *p = fp;
  if (p == &b___sF[0]) return S_IN;
  if (p == &b___sF[B_FILE_SIZE]) return S_OUT;
  if (p == &b___sF[2 * B_FILE_SIZE]) return S_ERR;
  return S_NONE;
}

static FILE *real_fp(void *fp) {
  switch (std_kind(fp)) {
  case S_IN: return stdin;
  case S_OUT: return stdout;
  case S_ERR: return stderr;
  default: return (FILE *)fp;
  }
}

static void log_stream(StdKind k, const char *s, size_t n) {
  char line[1024];
  if (n >= sizeof line)
    n = sizeof line - 1;
  memcpy(line, s, n);
  line[n] = 0;
  debugPrintf("[%s] %s%s", k == S_OUT ? "stdout" : "stderr", line,
              (n && line[n - 1] == '\n') ? "" : "\n");
}

/* -------------------------------------------------- fake-fd backed FILEs */
static int ck_read(void *c, char *buf, _READ_WRITE_BUFSIZE_TYPE n) { return (int)b_read((int)(intptr_t)c, buf, (size_t)n); }
static int ck_write(void *c, const char *buf, _READ_WRITE_BUFSIZE_TYPE n) { return (int)n; }
static fpos_t ck_seek(void *c, fpos_t off, int whence) { return (fpos_t)b_lseek64((int)(intptr_t)c, off, whence); }
static int ck_close(void *c) { return b_close((int)(intptr_t)c); }

static FILE *fake_fdopen(int fd) {
  return funopen((void *)(intptr_t)fd, ck_read, ck_write, ck_seek, ck_close);
}

/* ----------------------------------------------------------- open/close */
void *b_fopen(const char *path, const char *mode) {
  if (!path || !mode) {
    b_set_errno(L_EINVAL);
    return NULL;
  }
  if (!strncmp(path, "/proc/", 6) || !strncmp(path, "/dev/", 5)) {
    int fd = b_open(path, L_O_RDONLY);
    return fd < 0 ? NULL : fake_fdopen(fd);
  }
  char buf[DCR_PATH_MAX];
  const char *real = dcr_translate_path(path, buf, sizeof buf);
  FILE *f = fopen(real, mode);
  if (dcr_path_traced(path))
    debugPrintf("[io] fopen(%s, %s) -> %p\n", path, mode, (void *)f);
  if (!f)
    b_fix_errno();
  else
    b_track_open(fileno(f), real, strpbrk(mode, "wa+") != NULL);
  return f;
}

void *b_fdopen(int fd, const char *mode) {
  if (b_is_fake_fd(fd))
    return fake_fdopen(fd);
  FILE *f = fdopen(fd, mode);
  if (!f)
    b_fix_errno();
  return f;
}

int b_fclose(void *fp) {
  if (std_kind(fp) != S_NONE)
    return 0;
  int fd = fileno((FILE *)fp);
  if (fd >= 0)
    b_untrack_open(fd);
  return fclose((FILE *)fp);
}

/* --------------------------------------------------------------- output */
const char *b_safe_format(const char *fmt, va_list ap, char *buf, size_t cap); /* bionic_printf.c */
int b_vfprintf(void *fp, const char *fmt, va_list ap) {
  char safe[1024];
  fmt = b_safe_format(fmt, ap, safe, sizeof safe);
  StdKind k = std_kind(fp);
  if (k == S_OUT || k == S_ERR) {
    char buf[1024];
    int n = vsnprintf(buf, sizeof buf, fmt, ap);
    if (n > 0)
      log_stream(k, buf, (size_t)n < sizeof buf ? (size_t)n : sizeof buf - 1);
    return n;
  }
  return vfprintf(real_fp(fp), fmt, ap);
}

int b_fprintf(void *fp, const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  int r = b_vfprintf(fp, fmt, ap);
  va_end(ap);
  return r;
}

int b_vprintf(const char *fmt, va_list ap) { return b_vfprintf(&b___sF[B_FILE_SIZE], fmt, ap); }

int b_printf(const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  int r = b_vprintf(fmt, ap);
  va_end(ap);
  return r;
}

int b_fputs(const char *s, void *fp) {
  StdKind k = std_kind(fp);
  if (k == S_OUT || k == S_ERR) {
    log_stream(k, s, strlen(s));
    return 1;
  }
  return fputs(s, real_fp(fp));
}

int b_puts(const char *s) {
  log_stream(S_OUT, s, strlen(s));
  return 1;
}

int b_fputc(int c, void *fp) {
  StdKind k = std_kind(fp);
  if (k == S_OUT || k == S_ERR)
    return (unsigned char)c; /* single chars to the console: dropped, not worth a log line */
  return fputc(c, real_fp(fp));
}
int b_putc(int c, void *fp) { return b_fputc(c, fp); }
int b_putchar(int c) { return (unsigned char)c; }

size_t b_fwrite(const void *p, size_t sz, size_t n, void *fp) {
  StdKind k = std_kind(fp);
  if (k == S_OUT || k == S_ERR) {
    log_stream(k, p, sz * n);
    return n;
  }
  return fwrite(p, sz, n, real_fp(fp));
}

int b_fflush(void *fp) {
  if (!fp || std_kind(fp) != S_NONE)
    return 0;
  return fflush((FILE *)fp);
}

/* ---------------------------------------------------------------- input */
size_t b_fread(void *p, size_t sz, size_t n, void *fp) {
  if (std_kind(fp) != S_NONE)
    return 0;
  return fread(p, sz, n, (FILE *)fp);
}

char *b_fgets(char *s, int n, void *fp) {
  if (std_kind(fp) != S_NONE)
    return NULL;
  return fgets(s, n, (FILE *)fp);
}

int b_getc(void *fp) { return std_kind(fp) != S_NONE ? EOF : getc((FILE *)fp); }
int b_ungetc(int c, void *fp) { return std_kind(fp) != S_NONE ? EOF : ungetc(c, (FILE *)fp); }

int b_fscanf(void *fp, const char *fmt, ...) {
  if (std_kind(fp) != S_NONE)
    return EOF;
  va_list ap;
  va_start(ap, fmt);
  int r = vfscanf((FILE *)fp, fmt, ap);
  va_end(ap);
  return r;
}

/* ---------------------------------------------------------- positioning */
int b_fseek(void *fp, long off, int whence) {
  if (std_kind(fp) != S_NONE) {
    b_set_errno(L_ESPIPE);
    return -1;
  }
  int r = fseek((FILE *)fp, off, whence);
  if (r)
    b_fix_errno();
  return r;
}

long b_ftell(void *fp) { return std_kind(fp) != S_NONE ? -1 : ftell((FILE *)fp); }
void b_clearerr(void *fp) { if (std_kind(fp) == S_NONE) clearerr((FILE *)fp); }
int b_ferror(void *fp) { return std_kind(fp) != S_NONE ? 0 : ferror((FILE *)fp); } /* Unity 5.6 */

/* ----------------------------------------------------------- wide chars */
wint_t b_getwc(void *fp) { return std_kind(fp) != S_NONE ? WEOF : getwc((FILE *)fp); }
wint_t b_putwc(wchar_t c, void *fp) { return std_kind(fp) != S_NONE ? (wint_t)c : putwc(c, (FILE *)fp); }
wint_t b_ungetwc(wint_t c, void *fp) { return std_kind(fp) != S_NONE ? WEOF : ungetwc(c, (FILE *)fp); }

int b_setvbuf(void *fp, char *buf, int mode, size_t size) {
  if (std_kind(fp) != S_NONE)
    return 0; /* the console streams go to the log, unbuffered either way */
  return setvbuf((FILE *)fp, buf, mode, size); /* _IOFBF/_IOLBF/_IONBF: 0/1/2 in both */
}

/* No processes to spawn (Mono probes for tools such as "uname" through popen). */
void *b_popen(const char *cmd, const char *mode) {
  debugPrintf("[stdio] popen(\"%s\") refused\n", cmd ? cmd : "");
  b_set_errno(L_ENOSYS);
  return NULL;
}
int b_pclose(void *fp) {
  b_set_errno(L_ECHILD);
  return -1;
}
