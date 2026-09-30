/* dcr_boost.c -- the CPU at full speed while the engine is stuck in a long frame.
 *
 * A map change (WipeController.Restart) is SceneManager.LoadScene on the main
 * thread, then Resources.UnloadUnusedAssets and the full collections both
 * trigger: single frames of hundreds of milliseconds of main-thread CPU work,
 * at the Switch's 1020 MHz where the phones this was built for run 2+ GHz.
 * Horizon has a mode for exactly this, the one retail games use behind their
 * loading screens: appletSetCpuBoostMode(FastLoad) -- CPU 1785 MHz, GPU down
 * to its minimum. The GPU half is why it cannot simply stay on: gameplay
 * frames are drawn by the GPU. So it is on only INSIDE a frame that has
 * already taken 50 ms (checked on every vsync pulse, dcr_vsync.c), and off
 * again the moment that frame returns (dcr_boot.c). A normal frame is never
 * boosted; a loading frame is, from its 50th millisecond.
 *
 * The start-up is one long load, so there the boost is simply on: from the
 * moment config.ini is read (first-launch setup, library loading, Mono and
 * the engine starting, the first frames' shader compiles, the logos and the
 * game's scene behind its logo wipe) until the title menu is up. Nothing but
 * logos is drawn before that, so the GPU half costs little.
 *
 * Long frames are also written to the log, with the time they took and how
 * much of it was boosted: that is what a map change costs, measured. And once
 * a minute, which threads the CPU went to and the SoC's temperature. MIT.
 */
#include <stdio.h>
#include <string.h>
#include <switch.h>

#include "bionic_pthread.h"
#include "dcr_config.h"
#include "util.h"

#define BOOST_AFTER_MS 50
#define LOG_OVER_MS 120

static Mutex s_mx;
static volatile u64 s_frame_start; /* system tick; 0 between frames */
static volatile u64 s_boost_start;
static int s_on, s_failed, s_logged;
static int s_launch; /* boosted from start-up to the first picture */
static u64 s_launch_start;
static u64 s_boosts, s_boost_ms_total;

static u64 ms_since(u64 tick) { return armTicksToNs(armGetSystemTick() - tick) / 1000000ull; }

/* ---- where a long frame's time went, without pausing anyone: each thread's
 * CPU time (the kernel's per-thread tick count) and the file reads, from the
 * moment the frame passed BOOST_AFTER_MS to its end. The main thread mostly
 * waits in such frames (for Unity's loading thread, the render thread, the
 * GC), so this says which thread the time was really spent on. */
void dcr_io_read_stats(uint64_t *calls, uint64_t *bytes, uint64_t *ticks); /* bionic_io.c */
#define MAXT 64
typedef struct {
  BThread *t;
  int tid, main;
  char name[16];
  u64 ticks;
} TSnap;
typedef struct {
  TSnap ts[MAXT];
  int n;
} Snap;
static Snap s_snap; /* a long frame's, from its BOOST_AFTER_MS */
static u64 s_io0[3];
static int s_have_snap;

static u64 thread_ticks(Handle h) {
  u64 v = 0;
  if (R_FAILED(svcGetInfo(&v, InfoType_ThreadTickCount, h, TickCountInfo_Total)))
    svcGetInfo(&v, InfoType_ThreadTickCountDeprecated, h, TickCountInfo_Total);
  return v;
}

static void snap_thread(BThread *t, void *arg) {
  Snap *s = arg;
  if (s->n >= MAXT || t->handle == INVALID_HANDLE || t->finished)
    return;
  TSnap *e = &s->ts[s->n++];
  e->t = t;
  e->tid = t->tid;
  e->main = t->handle == envGetMainThreadHandle();
  memcpy(e->name, t->name, sizeof e->name);
  e->name[sizeof e->name - 1] = 0;
  e->ticks = thread_ticks(t->handle);
}

/* the CPU used since a snapshot, by live threads still in it */
typedef struct {
  int idx;
  u64 used;
} Used;
typedef struct {
  const Snap *snap;
  Used used[MAXT];
  int n;
} Diff;

static void diff_thread(BThread *t, void *arg) {
  Diff *d = arg;
  if (t->handle == INVALID_HANDLE || t->finished)
    return;
  for (int i = 0; i < d->snap->n; i++)
    if (d->snap->ts[i].t == t && d->snap->ts[i].tid == t->tid) {
      u64 now = thread_ticks(t->handle);
      if (now > d->snap->ts[i].ticks && d->n < MAXT) {
        d->used[d->n].idx = i;
        d->used[d->n].used = now - d->snap->ts[i].ticks;
        d->n++;
      }
      return;
    }
}

/* the busiest thread left in d (its entry then cleared), or -1 */
static int take_busiest(Diff *d, u64 *used) {
  int best = -1;
  for (int i = 0; i < d->n; i++)
    if (d->used[i].used && (best < 0 || d->used[i].used > d->used[best].used))
      best = i;
  if (best < 0)
    return -1;
  *used = d->used[best].used;
  d->used[best].used = 0;
  return d->used[best].idx;
}

static const char *snap_name(const TSnap *e) { return e->main ? "UnityMain" : (e->name[0] ? e->name : "?"); }

static void cpu_report(char *out, size_t cap) {
  static Diff d;
  d.snap = &s_snap;
  d.n = 0;
  b_thread_foreach(diff_thread, &d);
  int n = snprintf(out, cap, "; CPU since:");
  for (int k = 0; k < 4 && n < (int)cap - 48; k++) {
    u64 used;
    int idx = take_busiest(&d, &used);
    if (idx < 0 || armTicksToNs(used) < 5000000ull)
      break;
    n += snprintf(out + n, cap - n, " %s %llu ms,", snap_name(&s_snap.ts[idx]),
                  (unsigned long long)(armTicksToNs(used) / 1000000ull));
  }
  u64 io[3];
  dcr_io_read_stats(&io[0], &io[1], &io[2]);
  snprintf(out + n, cap - n, " file reads %llu (%llu KB, %llu ms)", (unsigned long long)(io[0] - s_io0[0]),
           (unsigned long long)((io[1] - s_io0[1]) >> 10),
           (unsigned long long)(armTicksToNs(io[2] - s_io0[2]) / 1000000ull));
}

static int set_boost(int on) {
  Result rc = appletSetCpuBoostMode(on ? ApmCpuBoostMode_FastLoad : ApmCpuBoostMode_Normal);
  if (R_FAILED(rc) && on && !s_failed) {
    s_failed = 1;
    debugPrintf("[boost] appletSetCpuBoostMode unavailable (0x%x): long frames run at normal clocks\n",
                (unsigned)rc);
  }
  return R_SUCCEEDED(rc);
}

/* Frame loop: around nativeRender. */
void dcr_boost_frame_begin(void) { s_frame_start = armGetSystemTick(); }

void dcr_boost_frame_end(u64 frame) {
  u64 start = s_frame_start;
  mutexLock(&s_mx);
  s_frame_start = 0;
  u64 boosted = 0;
  if (s_on) {
    boosted = ms_since(s_boost_start);
    set_boost(0);
    s_on = 0;
    s_boost_ms_total += boosted;
  }
  int launch = s_launch;
  int have = s_have_snap;
  s_have_snap = 0;
  u64 took = start ? ms_since(start) : 0;
  static char cpu[400];
  cpu[0] = 0;
  if (have && took >= LOG_OVER_MS && s_logged < 400)
    cpu_report(cpu, sizeof cpu);
  mutexUnlock(&s_mx);
  if (took >= LOG_OVER_MS && s_logged < 400) {
    s_logged++;
    if (launch)
      debugPrintf("[frame] frame %llu took %llu ms (start-up: all of it CPU-boosted)%s\n",
                  (unsigned long long)frame, (unsigned long long)took, cpu);
    else if (boosted)
      debugPrintf("[frame] frame %llu took %llu ms, the last %llu ms of it CPU-boosted%s\n",
                  (unsigned long long)frame, (unsigned long long)took, (unsigned long long)boosted, cpu);
    else
      debugPrintf("[frame] frame %llu took %llu ms%s\n", (unsigned long long)frame,
                  (unsigned long long)took, cpu);
  }
}

/* Every vsync pulse: boost a frame that has run past BOOST_AFTER_MS -- unless
 * a system screen (the controller screen, the profile picker) is what holds
 * the frame: that is the system's time, not a load (hardware log 2026-09-29:
 * 15.6 s of the controller screen spent at 1785 MHz). */
int dcr_applet_is_busy(void); /* watchdog.c */

void dcr_boost_poll(void) {
  u64 start = s_frame_start;
  if (!start)
    return;
  if (dcr_applet_is_busy()) {
    if (s_on) {
      mutexLock(&s_mx);
      if (s_on) {
        s_boost_ms_total += ms_since(s_boost_start);
        set_boost(0);
        s_on = 0;
      }
      mutexUnlock(&s_mx);
    }
    return;
  }
  if (s_have_snap || ms_since(start) < BOOST_AFTER_MS)
    return;
  mutexLock(&s_mx);
  if (s_frame_start == start && !s_have_snap) {
    s_snap.n = 0;
    b_thread_foreach(snap_thread, &s_snap);
    dcr_io_read_stats(&s_io0[0], &s_io0[1], &s_io0[2]);
    s_have_snap = 1;
    if (!s_launch && !s_on && !s_failed && dcr_config()->boost && set_boost(1)) {
      s_on = 1;
      s_boost_start = armGetSystemTick();
      s_boosts++;
    }
  }
  mutexUnlock(&s_mx);
}

/* main(), once config.ini is read. */
void dcr_boost_launch_begin(void) {
  if (!dcr_config()->boost)
    return;
  mutexLock(&s_mx);
  if (set_boost(1)) {
    s_launch = 1;
    s_launch_start = armGetSystemTick();
  }
  mutexUnlock(&s_mx);
}

/* The launch goes on past the first picture: the logos, then the game's scene
 * loading behind its logo wipe, until the title menu is up (the mod says so,
 * dcr_launch_ready) -- or LAUNCH_AFTER_PICTURE_MS after the first picture, at
 * the latest. Only logos are drawn meanwhile: the GPU half costs little. */
#define LAUNCH_AFTER_PICTURE_MS 25000
static u64 s_first_picture;
static volatile int s_ready;

void dcr_boost_launch_end(void) {
  mutexLock(&s_mx);
  int was = s_launch;
  if (was) {
    s_launch = 0;
    set_boost(0);
  }
  mutexUnlock(&s_mx);
  if (was)
    debugPrintf("[boost] start-up: %llu ms at 1785 MHz, until the %s (%llu ms of it after the first picture)\n",
                (unsigned long long)ms_since(s_launch_start), s_ready ? "title menu" : "time limit",
                (unsigned long long)(s_first_picture ? ms_since(s_first_picture) : 0));
}

/* The frame loop, after the first frame that reached the screen. */
void dcr_boost_first_picture(void) {
  if (!s_first_picture)
    s_first_picture = armGetSystemTick();
}

/* The mod (Native.LaunchReady): the title menu is up. */
void dcr_launch_ready(void) { s_ready = 1; }

/* Every frame: the end of the launch boost, once it is due. */
void dcr_boost_launch_tick(void) {
  if (s_launch && s_first_picture && (s_ready || ms_since(s_first_picture) >= LAUNCH_AFTER_PICTURE_MS))
    dcr_boost_launch_end();
}

/* ---- once a minute (dcr_boot.c): where the CPU went since the last report,
 * as each busy thread's share of one core (the game's own threads, which are
 * nearly all of it), and the SoC's temperature when the system lets it be
 * read. A game drawing 60 frames a second keeps two or three cores partly
 * busy and the GPU working, and warms a Switch as any 3D game does; a thread
 * near 100% with nothing to do would not be normal, and this line shows it. */
static int soc_celsius(void) {
  static int state; /* 0 not tried, 1 a session (10.0.0+), 2 the old call, -1 none */
  static TsSession ses;
  if (state == 0) {
    state = -1;
    /* Ryujinx answers the service but not every command of it */
    if (!dcr_is_emulator() && R_SUCCEEDED(tsInitialize())) {
      if (!hosversionAtLeast(10, 0, 0))
        state = 2;
      else if (R_SUCCEEDED(tsOpenSession(&ses, TsDeviceCode_LocationExternal)))
        state = 1;
    }
  }
  if (state == 1) {
    float c;
    if (R_SUCCEEDED(tsSessionGetTemperature(&ses, &c)))
      return (int)(c + 0.5f);
  } else if (state == 2) {
    s32 c;
    if (R_SUCCEEDED(tsGetTemperature(TsLocation_External, &c)))
      return c;
  }
  return -1000;
}

void dcr_boost_cpu_report(void) {
  static Snap snap;
  static Diff d;
  static u64 t0;
  u64 now = armGetSystemTick();
  if (t0) {
    d.snap = &snap;
    d.n = 0;
    b_thread_foreach(diff_thread, &d);
    u64 wall = armTicksToNs(now - t0), all = 0;
    for (int i = 0; i < d.n; i++)
      all += d.used[i].used;
    char line[320];
    int n = snprintf(line, sizeof line, "%llu%% of one core in all", (unsigned long long)(armTicksToNs(all) * 100 / wall));
    for (int k = 0; k < 5 && n < (int)sizeof line - 40; k++) {
      u64 used;
      int idx = take_busiest(&d, &used);
      if (idx < 0 || armTicksToNs(used) * 100 < wall) /* under 1% */
        break;
      n += snprintf(line + n, sizeof line - n, "%s %s %llu%%", k ? "," : ":", snap_name(&snap.ts[idx]),
                    (unsigned long long)(armTicksToNs(used) * 100 / wall));
    }
    int c = soc_celsius();
    char temp[48] = "";
    if (c > -1000)
      snprintf(temp, sizeof temp, "; SoC %d C", c);
    debugPrintf("[cpu] the last %llu s: %s%s\n", (unsigned long long)(wall / 1000000000ull), line, temp);
  }
  snap.n = 0;
  b_thread_foreach(snap_thread, &snap);
  t0 = now;
}

void dcr_boost_report(void) {
  if (s_boosts)
    debugPrintf("[boost] %llu long frames boosted so far, %llu ms at 1785 MHz in total\n",
                (unsigned long long)s_boosts, (unsigned long long)s_boost_ms_total);
}
