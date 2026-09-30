/* android_ndk.c -- the libandroid.so imports: ANativeWindow, ALooper, sensors,
 * and the AInputEvent/AKeyEvent getters.
 *
 *   ANativeWindow  the one window is libnx's default NWindow. The GL layer's
 *                  eglCreateWindowSurface receives it back unchanged.
 *   ALooper        Unity uses the looper as a per-thread wait/wake primitive,
 *                  not for fd polling, so a condvar per thread is sufficient
 *                  (same finding as the Drive Ahead port).
 *   ASensor*       no sensors are reported. Crossy Road does not need tilt.
 *   AInputEvent    events are produced by this port (dcr_input.c) as the
 *                  DcrInputEvent below, so the getters read that struct.
 * MIT.
 */
#include <stdint.h>
#include <string.h>
#include <switch.h>

#include "config.h"
#include "util.h"

/* ============================== ANativeWindow ============================== */
static int32_t g_win_w = DCR_FORCE_SCREEN_W, g_win_h = DCR_FORCE_SCREEN_H;

void dcr_window_size(int *w, int *h) {
  if (w) *w = g_win_w;
  if (h) *h = g_win_h;
}

/* The rendering size (config.ini [display] resolution), set before the
 * engine starts. The vi layer scales it to the screen: 1080p is shown as is
 * docked and downscaled on the 720p panel. */
void dcr_window_set_size(int w, int h) {
  if (w > 0 && h > 0) {
    g_win_w = w;
    g_win_h = h;
  }
}

static void window_set_geom(NWindow *w, u32 bw, u32 bh) {
  if (!nwindowIsValid(w) || log_console_active())
    return; /* the on-screen boot log owns the window (null renderer) */
  nwindowSetDimensions(w, bw, bh);
  nwindowSetCrop(w, 0, 0, bw, bh);
  nwindowSetSwapInterval(w, 1);
}

/* Called by the EGL layer right before the window surface is created. */
void dcr_window_prepare(void) { window_set_geom(nwindowGetDefault(), (u32)g_win_w, (u32)g_win_h); }

void *b_ANativeWindow_fromSurface(void *env, void *surface) {
  NWindow *w = nwindowGetDefault();
  window_set_geom(w, (u32)g_win_w, (u32)g_win_h);
  return w;
}
void b_ANativeWindow_acquire(void *w) {}
void b_ANativeWindow_release(void *w) {}
int32_t b_ANativeWindow_getWidth(void *w) { return g_win_w; }
int32_t b_ANativeWindow_getHeight(void *w) { return g_win_h; }
int32_t b_ANativeWindow_setBuffersGeometry(void *w, int32_t width, int32_t height, int32_t fmt) {
  if (width > 0 && height > 0) {
    debugPrintf("[window] setBuffersGeometry %dx%d fmt %d\n", width, height, fmt);
    window_set_geom((NWindow *)w, (u32)width, (u32)height);
  }
  return 0;
}

/* ================================= ALooper ================================= */
#define MAX_LOOPERS 32
#define ALOOPER_POLL_WAKE (-1)
#define ALOOPER_POLL_TIMEOUT (-3)

typedef struct {
  Handle owner;
  Mutex m;
  CondVar cv;
  int signaled;
} Looper;

static Looper g_loopers[MAX_LOOPERS];
static Mutex g_loopers_lock;

static Looper *looper_for_current(int create) {
  Handle me = threadGetCurHandle();
  mutexLock(&g_loopers_lock);
  Looper *free_slot = NULL;
  for (int i = 0; i < MAX_LOOPERS; i++) {
    if (g_loopers[i].owner == me) {
      mutexUnlock(&g_loopers_lock);
      return &g_loopers[i];
    }
    if (!g_loopers[i].owner && !free_slot)
      free_slot = &g_loopers[i];
  }
  if (create && free_slot) {
    memset(free_slot, 0, sizeof *free_slot);
    free_slot->owner = me;
  }
  mutexUnlock(&g_loopers_lock);
  return create ? free_slot : NULL;
}

void *b_ALooper_prepare(int opts) { return looper_for_current(1); }
void *b_ALooper_forThread(void) { return looper_for_current(0); }

void b_ALooper_wake(void *l) {
  Looper *L = l;
  if (!L)
    return;
  mutexLock(&L->m);
  L->signaled = 1;
  condvarWakeAll(&L->cv);
  mutexUnlock(&L->m);
}

int b_ALooper_pollOnce(int timeout_ms, int *fd, int *events, void **data) {
  Looper *L = looper_for_current(1);
  if (fd) *fd = 0;
  if (events) *events = 0;
  if (data) *data = NULL;
  if (!L)
    return ALOOPER_POLL_TIMEOUT;
  mutexLock(&L->m);
  if (!L->signaled && timeout_ms != 0) {
    if (timeout_ms < 0)
      condvarWait(&L->cv, &L->m);
    else
      condvarWaitTimeout(&L->cv, &L->m, (u64)timeout_ms * 1000000ull);
  }
  int was = L->signaled;
  L->signaled = 0;
  mutexUnlock(&L->m);
  return was ? ALOOPER_POLL_WAKE : ALOOPER_POLL_TIMEOUT;
}

/* ================================= sensors ================================= */
static int g_sensor_manager;
static int g_sensor_queue;

void *b_ASensorManager_getInstance(void) { return &g_sensor_manager; }
int b_ASensorManager_getSensorList(void *m, const void ***list) {
  static const void *empty[1];
  if (list)
    *list = empty;
  return 0;
}
const void *b_ASensorManager_getDefaultSensor(void *m, int type) { return NULL; }
void *b_ASensorManager_createEventQueue(void *m, void *looper, int ident, void *cb, void *data) {
  return &g_sensor_queue;
}
int b_ASensorManager_destroyEventQueue(void *m, void *q) { return 0; }
int b_ASensorEventQueue_enableSensor(void *q, const void *s) { return -1; }
int b_ASensorEventQueue_disableSensor(void *q, const void *s) { return 0; }
int b_ASensorEventQueue_setEventRate(void *q, const void *s, int32_t us) { return 0; }
int b_ASensorEventQueue_hasEvents(void *q) { return 0; }
int b_ASensorEventQueue_getEvents(void *q, void *ev, size_t n) { return 0; }
const char *b_ASensor_getName(const void *s) { return ""; }
const char *b_ASensor_getVendor(const void *s) { return ""; }
int b_ASensor_getType(const void *s) { return 0; }
float b_ASensor_getResolution(const void *s) { return 0.0f; }
int b_ASensor_getMinDelay(const void *s) { return 0; }

/* =============================== input events ============================== */
typedef struct {
  int32_t type;      /* AINPUT_EVENT_TYPE_KEY = 1 */
  int32_t device_id;
  int32_t action;    /* AKEY_EVENT_ACTION_DOWN = 0, UP = 1 */
  int32_t key_code;
  int32_t meta_state;
} DcrInputEvent;

int32_t b_AInputEvent_getType(const DcrInputEvent *e) { return e ? e->type : 0; }
int32_t b_AInputEvent_getDeviceId(const DcrInputEvent *e) { return e ? e->device_id : 0; }
int32_t b_AKeyEvent_getAction(const DcrInputEvent *e) { return e ? e->action : 0; }
int32_t b_AKeyEvent_getKeyCode(const DcrInputEvent *e) { return e ? e->key_code : 0; }
int32_t b_AKeyEvent_getMetaState(const DcrInputEvent *e) { return e ? e->meta_state : 0; }
