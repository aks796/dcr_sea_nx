/* bionic_pthread.h -- thread records shared with the signal / GC bridge. */
#ifndef DCR_BIONIC_PTHREAD_H
#define DCR_BIONIC_PTHREAD_H
#include <switch.h>
#include "bionic.h"

#define BTHREAD_MAGIC 0x54485244u /* 'THRD' */

typedef struct BThread {
  uint32_t magic;
  Thread thr;               /* valid when owned */
  int owned;                /* created by b_pthread_create */
  Handle handle;            /* kernel thread handle: pause/resume/context */
  int tid;                  /* gettid() / tkill() identity */
  void *(*start)(void *);
  void *arg;
  void *ret;
  volatile int detached, finished;
  void *stack_base;         /* lowest address */
  size_t stack_size;
  void *keys[256];
  struct b_cleanup *cleanup_top;
  volatile int gc_paused;   /* suspended by the Boehm GC bridge (mono_gc.c) */
  char name[16];            /* prctl(PR_SET_NAME) / pthread_setname_np (profiler, watchdog) */
  struct BThread *next, *next_zombie;
} BThread;

typedef struct {
  uint32_t magic;
  int type;
  Mutex m;
  uint32_t owner;
  int count;
} BMutex;

BThread *b_thread_self(void);
BThread *b_thread_by_tid(int tid);
void b_thread_foreach(void (*fn)(BThread *, void *), void *arg);
void b_thread_foreach_nolock(void (*fn)(BThread *, void *), void *arg); /* watchdog emergency */
void b_lock_words(uint32_t *reg, uint32_t *pause);
int b_gettid(void);
/* Held around every svcSetThreadActivity pause/resume (GC bridge, watchdog). */
void b_pause_lock(void);
void b_pause_unlock(void);

#endif
