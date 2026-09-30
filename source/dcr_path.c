/* dcr_path.c -- turn the paths an Android build of the game asks for into
 * paths on the Switch SD card.
 *
 *   jar:file:///data/app/<pkg>-1/base.apk!/assets/bin/Data/level0
 *   file://jar:file:///.../base.apk!/assets/...        (Unity double-prefixes)
 *   /assets/...   assets/...   <root>/assets/...        (device/relative forms)
 *        -> <root>/assets/...
 *   /data/data/<pkg>/...   /data/user/0/<pkg>/...       (internal storage)
 *        -> <root>/data/...
 *   /storage/emulated/0/Android/data/<pkg>/...  /sdcard/Android/data/<pkg>/...
 *        -> <root>/external/...
 *   /data/app/<pkg>-1/lib/arm/libfoo.so                (native lib dir)
 *        -> <root>/libfoo.so
 *   /data/app/<pkg>-1/base.apk                         (ApplicationInfo.sourceDir)
 *        -> <root>/game.apk   the user's own APK: Unity 2017 reads its data
 *           straight out of the zip (apkAddCentralDirectory), including the
 *           .split parts it reassembles, so the APK is the data source
 *   /storage/emulated/0/Android/obb/<pkg>/...          (expansion files)
 *        -> <root>/obb/...
 *
 * The "!/assets" anchor is checked first because the jar forms also contain
 * "/assets" as a substring (the Drive Ahead port lost boots to that order).
 * Paths that match nothing are returned unchanged. MIT.
 */
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>

#include "config.h"
#include "dcr_path.h"
#include "util.h"

const char *dcr_game_root(void); /* main.c: "sdmc:/switch/dcr_sea_nx" */

#define PKG "net.gogame.disney.crossyroad"

static int is_sep(char c) { return c == '/' || c == '\\'; }

/* `p` begins with `pre` followed by a separator or end -> the tail, else NULL. */
static const char *after_prefix(const char *p, const char *pre) {
  size_t n = strlen(pre);
  if (!n || strncmp(p, pre, n) != 0)
    return NULL;
  const char *t = p + n;
  if (is_sep(*t))
    return t + 1;
  if (!*t)
    return "";
  return NULL;
}

/* Collapse duplicate separators and turn '\' into '/'. */
static void clean(const char *in, char *out, size_t cap) {
  size_t o = 0;
  int prev_sep = 0;
  for (; *in && o + 1 < cap; in++) {
    char c = *in == '\\' ? '/' : *in;
    if (c == '/') {
      if (prev_sep)
        continue;
      prev_sep = 1;
    } else {
      prev_sep = 0;
    }
    out[o++] = c;
  }
  out[o] = 0;
}

static const char *join(char *out, size_t cap, const char *sub, const char *tail) {
  char t[DCR_PATH_MAX];
  clean(tail, t, sizeof t);
  if (t[0])
    snprintf(out, cap, "%s/%s/%s", dcr_game_root(), sub, t);
  else
    snprintf(out, cap, "%s/%s", dcr_game_root(), sub);
  return out;
}

/* Paths into the game's StreamingAssets / the APK: traced (limited), since
 * what the engine asks for there is not visible otherwise. */
int dcr_path_traced(const char *p) {
  if (!p || strstr(p, "/assets/bin/Data/"))
    return 0; /* Unity's routine probes before its own APK reader: not news */
  return strstr(p, "AssetBundles") || strstr(p, "jar:") || strstr(p, ".apk/") ||
         strstr(p, ".apk!") || strstr(p, "StreamingAssets");
}

static const char *translate(const char *path, char *out, size_t cap);

const char *dcr_translate_path(const char *path, char *out, size_t cap) {
  const char *r = translate(path, out, cap);
  static int logged;
  if (dcr_path_traced(path) && logged < 64) {
    logged++;
    debugPrintf("[path] %s -> %s\n", path, r);
  }
  return r;
}

static const char *translate(const char *path, char *out, size_t cap) {
  if (!path || !out || !cap)
    return path;
  const char *p = path;
  while (!strncmp(p, "file://", 7))
    p += 7;
  if (!strncmp(p, "jar:file://", 11)) {
    const char *bang = strstr(p, "!/");
    if (bang)
      p = bang + 1; /* "/assets/..." */
  }

  /* <root>/... (with or without "sdmc:") is already ours. */
  const char *root = dcr_game_root();
  const char *root_nodev = strchr(root, ':') ? strchr(root, ':') + 1 : root;
  if (!strncmp(p, root, strlen(root)))
    return p == path ? path : (snprintf(out, cap, "%s", p), out);
  if (!strncmp(p, root_nodev, strlen(root_nodev))) {
    snprintf(out, cap, "sdmc:%s", p);
    return out;
  }

  const char *t;
  if ((t = after_prefix(p, "/assets")) || (t = after_prefix(p, "assets")) ||
      (t = after_prefix(p, "./assets")))
    return join(out, cap, "assets", t);

  if ((t = after_prefix(p, "/data/data/" PKG)) || (t = after_prefix(p, "/data/user/0/" PKG)))
    return join(out, cap, "data", t);

  if ((t = after_prefix(p, "/storage/emulated/0/Android/data/" PKG)) ||
      (t = after_prefix(p, "/sdcard/Android/data/" PKG)) ||
      (t = after_prefix(p, "/mnt/sdcard/Android/data/" PKG)))
    return join(out, cap, "external", t);

  if ((t = after_prefix(p, "/storage/emulated/0/Android/obb/" PKG)) ||
      (t = after_prefix(p, "/sdcard/Android/obb/" PKG)))
    return join(out, cap, "obb", t);

  if (!strncmp(p, "/data/app/", 10)) {
    const char *lib = strstr(p, "/lib/arm/");
    if (lib)
      return join(out, cap, ".", lib + 9);
    size_t n = strlen(p);
    if (n > 4 && !strcmp(p + n - 4, ".apk")) {
      snprintf(out, cap, "%s/" DCR_APK_NAME, dcr_game_root());
      return out;
    }
  }

  if (p != path) {
    snprintf(out, cap, "%s", p);
    return out;
  }
  return path;
}

/* mkdir -p for the directories the game expects Android to have created. */
void dcr_path_prepare_dirs(void) {
  static const char *const dirs[] = {"data", "data/files", "data/cache", "data/shared_prefs",
                                     "external", "external/files", "external/cache"};
  char p[DCR_PATH_MAX];
  for (unsigned i = 0; i < sizeof dirs / sizeof dirs[0]; i++) {
    snprintf(p, sizeof p, "%s/%s", dcr_game_root(), dirs[i]);
    mkdir(p, 0777);
  }
}
