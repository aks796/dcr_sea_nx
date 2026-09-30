/* dcr_setup.c -- a first launch from nothing but game.apk.
 *
 * The game folder (/switch/dcr_sea_nx) needs only the user's own APK (any
 * name: it becomes game.apk) and the launcher NRO (launcher/). Everything else is made from
 * the APK here, before anything is loaded:
 *   libmain.so libunity.so libmono.so   lib/armeabi-v7a/ out of game.apk
 *   classes.txt                         the Java class names its classes*.dex
 *                                       define (jni_core.c answers FindClass
 *                                       with exactly those)
 * and made again whenever game.apk changes: .setup records the CRC-32 of each
 * source entry. Files that are already right (copied by tools/stage_sd.py,
 * say) are checked once and kept. The APK as published (APKPure's, say)
 * compresses the game's data; it is rewritten uncompressed once, first.
 *
 * UPDATES FROM THE NRO. This program runs as a forwarder title through an
 * ExeFS override, /atmosphere/contents/<title id>/exefs.nsp, which the
 * launcher wrote on its first run (it carries dcrsea_nx.nsp in its romfs). When
 * the NRO in the game folder carries a NEWER build than the one running
 * (romfs:/dcrsea_nx.build against DCR_BUILD), the override is rewritten from it
 * (dcr_exefs.h) and the program restarts into the new build: updating is
 * copying the new NRO over the old one. Never a downgrade, never a file this
 * program did not come from (the override must exist and name this title),
 * and never twice for the same build (.update records the attempt). MIT.
 */
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <switch.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <unistd.h>

#define MINIZ_NO_ZLIB_COMPATIBLE_NAMES
#include <miniz/miniz.h>

#include "config.h"
#include "dcr_build.h"
#include "dcr_exefs.h"
#include "dcr_formats.h"
#include "error.h"
#include "util.h"

const char *dcr_game_root(void); /* main.c */

static const char *const k_libs[] = {DCR_LIB_MAIN, DCR_LIB_UNITY, DCR_LIB_MONO};
#define ABI_DIR "lib/armeabi-v7a/"

static void root_path(char *out, size_t cap, const char *name) {
  snprintf(out, cap, "%s/%s", dcr_game_root(), name);
}

/* Setup work shows on screen as the green progress bar of the PvZ Touch port
 * (util.c log_console_progress): the name, what is being done, the bar. The
 * log it writes goes to debug.log only -- or scrolls on screen instead with
 * config.ini [debug] boot_log_on_screen.
 *
 * The bar, in permille of the whole first launch:
 *     0- 800  game.apk rewritten uncompressed (by bytes written)
 *   800- 820  the rewritten copy checked
 *   820- 940  libmain / libunity / libmono unpacked
 *   940-1000  the Java class list, then the game starts */
static int g_setup_shown;
static void setup_progress(const char *what, int permille) {
  if (!g_setup_shown) {
    g_setup_shown = 1;
    debugPrintf("[setup] setting up Disney Crossy Road: SEA -- this happens once\n");
  }
  log_console_progress(what, permille);
  log_console_update(); /* the log on screen, if it is */
}
int dcr_setup_did_work(void) { return g_setup_shown; }

static long file_size(const char *path) {
  struct stat st;
  return stat(path, &st) == 0 && S_ISREG(st.st_mode) ? (long)st.st_size : -1;
}

/* ---------------------------------------------------------------- .setup */
/* One line per made file: "<name> <crc of its source> <size>". */
#define MAX_STAMP 16
typedef struct {
  char name[32];
  unsigned long crc, size;
} Stamp;
static Stamp g_stamp[MAX_STAMP];
static int g_nstamp, g_stamp_dirty;

static void stamp_load(void) {
  char path[300];
  root_path(path, sizeof path, ".setup");
  FILE *f = fopen(path, "r");
  if (!f)
    return;
  while (g_nstamp < MAX_STAMP && fscanf(f, "%31s %lx %lu", g_stamp[g_nstamp].name,
                                        &g_stamp[g_nstamp].crc, &g_stamp[g_nstamp].size) == 3)
    g_nstamp++;
  fclose(f);
}

static Stamp *stamp_get(const char *name) {
  for (int i = 0; i < g_nstamp; i++)
    if (!strcmp(g_stamp[i].name, name))
      return &g_stamp[i];
  return NULL;
}

static void stamp_set(const char *name, unsigned long crc, unsigned long size) {
  Stamp *s = stamp_get(name);
  if (!s && g_nstamp < MAX_STAMP) {
    s = &g_stamp[g_nstamp++];
    snprintf(s->name, sizeof s->name, "%s", name);
  }
  if (s && (s->crc != crc || s->size != size)) {
    s->crc = crc;
    s->size = size;
    g_stamp_dirty = 1;
  }
}

static void stamp_save(void) {
  if (!g_stamp_dirty)
    return;
  char path[300];
  root_path(path, sizeof path, ".setup");
  FILE *f = fopen(path, "w");
  if (!f)
    return;
  for (int i = 0; i < g_nstamp; i++)
    fprintf(f, "%s %08lx %lu\n", g_stamp[i].name, g_stamp[i].crc, g_stamp[i].size);
  fclose(f);
}

/* ------------------------------------------------------------- libraries */
static unsigned long crc_of_file(const char *path) {
  FILE *f = fopen(path, "rb");
  if (!f)
    return 0;
  static unsigned char buf[1 << 16];
  mz_ulong crc = mz_crc32(0, NULL, 0);
  size_t n;
  while ((n = fread(buf, 1, sizeof buf, f)) > 0)
    crc = mz_crc32(crc, buf, n);
  fclose(f);
  return (unsigned long)crc;
}

/* Write to <dst>.part, then put it in place: a half-written library is never
 * mistaken for a whole one. */
static int extract_entry(mz_zip_archive *zip, int idx, const char *dst) {
  char tmp[320];
  snprintf(tmp, sizeof tmp, "%s.part", dst);
  unlink(tmp);
  if (!mz_zip_reader_extract_to_file(zip, (mz_uint)idx, tmp, 0)) {
    unlink(tmp);
    return -1;
  }
  unlink(dst);
  return rename(tmp, dst);
}

static void ensure_libs(mz_zip_archive *zip, const char *apk) {
  for (unsigned i = 0; i < sizeof k_libs / sizeof k_libs[0]; i++) {
    char arc[64], dst[300];
    snprintf(arc, sizeof arc, ABI_DIR "%s", k_libs[i]);
    root_path(dst, sizeof dst, k_libs[i]);
    int idx = mz_zip_reader_locate_file(zip, arc, NULL, 0);
    mz_zip_archive_file_stat st;
    if (idx < 0 || !mz_zip_reader_file_stat(zip, (mz_uint)idx, &st))
      fatal_error("%s has no %s.\n\n"
                  "This port needs the 32-bit ARM (armeabi-v7a) build of Disney Crossy\n"
                  "Road, Southeast Asia edition, version 1.5.4: use an APK of that version.",
                  apk, arc);
    unsigned long crc = (unsigned long)st.m_crc32, size = (unsigned long)st.m_uncomp_size;
    long have = file_size(dst);
    Stamp *s = stamp_get(k_libs[i]);
    if (have == (long)size && s && s->crc == crc)
      continue; /* made from this APK before */
    if (have == (long)size && crc_of_file(dst) == crc) {
      stamp_set(k_libs[i], crc, size); /* already the right file */
      continue;
    }
    setup_progress("Unpacking the game's libraries", 820 + (int)i * 40);
    debugPrintf("[setup] unpacking %s from game.apk (%lu KB)...\n", k_libs[i], size >> 10);
    if (extract_entry(zip, idx, dst) != 0)
      fatal_error("Could not write %s (from %s).\n\nIs the SD card full or read-only?", dst, apk);
    stamp_set(k_libs[i], crc, size);
  }
}

/* ------------------------------------------------------------ classes.txt */
static int cmp_str(const void *a, const void *b) {
  return strcmp(*(char *const *)a, *(char *const *)b);
}

static void ensure_classes(mz_zip_archive *zip) {
  /* classes.dex, classes2.dex, ... at the top of the APK */
  int idx[32], n = 0;
  mz_ulong crc = mz_crc32(0, NULL, 0);
  for (int k = 1; k <= 32 && n < 32; k++) {
    char nm[32];
    if (k == 1)
      snprintf(nm, sizeof nm, "classes.dex");
    else
      snprintf(nm, sizeof nm, "classes%d.dex", k);
    int i = mz_zip_reader_locate_file(zip, nm, NULL, 0);
    mz_zip_archive_file_stat st;
    if (i < 0 || !mz_zip_reader_file_stat(zip, (mz_uint)i, &st))
      break;
    idx[n++] = i;
    uint32_t c = st.m_crc32;
    crc = mz_crc32(crc, (const unsigned char *)&c, sizeof c);
  }
  char dst[300];
  root_path(dst, sizeof dst, "classes.txt");
  Stamp *s = stamp_get("classes.txt");
  if (!n || (s && s->crc == (unsigned long)crc && s->size == (unsigned long)n && file_size(dst) > 0))
    return; /* nothing to read, or already made from these */

  setup_progress("Reading the game's Java classes", 940);
  debugPrintf("[setup] listing the Java classes of game.apk (%d dex file%s)...\n", n, n > 1 ? "s" : "");
  Names ns = {0};
  for (int k = 0; k < n; k++) {
    size_t len = 0;
    void *d = mz_zip_reader_extract_to_heap(zip, (mz_uint)idx[k], &len, 0);
    if (d) {
      dex_names(d, len, &ns);
      free(d);
    }
  }
  if (ns.n) {
    qsort(ns.v, (size_t)ns.n, sizeof *ns.v, cmp_str);
    char tmp[320];
    snprintf(tmp, sizeof tmp, "%s.part", dst);
    FILE *f = fopen(tmp, "w");
    int written = 0;
    if (f) {
      fputs("# Java classes defined by the game's APK (names only). The wrapper's JNI\n"
            "# FindClass/Class.forName report exactly these, plus the Android framework.\n", f);
      for (int i = 0; i < ns.n; i++)
        if (i == 0 || strcmp(ns.v[i], ns.v[i - 1])) {
          fputs(ns.v[i], f);
          fputc('\n', f);
          written++;
        }
      if (fclose(f) == 0) {
        unlink(dst);
        if (rename(tmp, dst) == 0) {
          stamp_set("classes.txt", (unsigned long)crc, (unsigned long)n);
          debugPrintf("[setup] classes.txt: %d Java class names\n", written);
        }
      }
    }
  }
  for (int i = 0; i < ns.n; i++)
    free(ns.v[i]);
  free(ns.v);
}

/* ------------------------------------------------- an uncompressed APK */
/* The APK as published compresses the game's data (assets/bin/Data: 114 MB
 * in 12), which Unity then inflates at every read -- each 1 MB .split of a
 * level on every map load. The layout this port runs on stores every entry
 * (what tools/stage_sd.py makes), so a compressed game.apk is rewritten that
 * way once: every entry kept, same names, same CRCs, data 4-byte aligned. */
static int compressed_assets(mz_zip_archive *zip, uint64_t *stored_size) {
  mz_uint n = mz_zip_reader_get_num_files(zip);
  int found = 0;
  *stored_size = 22;
  for (mz_uint i = 0; i < n; i++) {
    mz_zip_archive_file_stat st;
    if (!mz_zip_reader_file_stat(zip, i, &st))
      continue;
    if (st.m_method != 0 && !strncmp(st.m_filename, "assets/", 7))
      found = 1;
    *stored_size += 30 + 3 + 46 + 2 * strlen(st.m_filename) + st.m_uncomp_size;
  }
  return found;
}

static void put16(uint8_t *p, unsigned v) { p[0] = (uint8_t)v, p[1] = (uint8_t)(v >> 8); }
static void put32(uint8_t *p, uint32_t v) { exefs_wr32(p, v); }

static int write_stored_zip(mz_zip_archive *zip, const char *dst, uint64_t total) {
  mz_uint n = mz_zip_reader_get_num_files(zip);
  FILE *f = fopen(dst, "wb");
  uint8_t *cd = malloc((size_t)n * (46 + 512)), *c = cd;
  if (!f || !cd || n > 0xFFFF) {
    if (f)
      fclose(f);
    free(cd);
    return -1;
  }
  setvbuf(f, NULL, _IOFBF, 1 << 20);
  uint32_t off = 0;
  int ok = 1;
  for (mz_uint i = 0; i < n && ok; i++) {
    mz_zip_archive_file_stat st;
    if (!mz_zip_reader_file_stat(zip, i, &st)) {
      ok = 0;
      break;
    }
    size_t nl = strlen(st.m_filename), len = 0;
    void *data = NULL;
    if (st.m_uncomp_size && !(data = mz_zip_reader_extract_to_heap(zip, i, &len, 0))) {
      ok = 0;
      break;
    }
    uint32_t size = (uint32_t)st.m_uncomp_size, pad = (4 - (off + 30 + nl) % 4) % 4;
    uint8_t h[30] = {0};
    put32(h, 0x04034b50);
    put16(h + 4, 10);        /* version needed: stored */
    put16(h + 12, 0x0021);   /* 1980-01-01 */
    put32(h + 14, st.m_crc32);
    put32(h + 18, size);
    put32(h + 22, size);
    put16(h + 26, (unsigned)nl);
    put16(h + 28, pad);      /* extra field: alignment padding */
    static const uint8_t zeros[4];
    ok = fwrite(h, 1, 30, f) == 30 && fwrite(st.m_filename, 1, nl, f) == nl &&
         fwrite(zeros, 1, pad, f) == pad && (!size || fwrite(data, 1, size, f) == size);
    free(data);
    memset(c, 0, 46);
    put32(c, 0x02014b50);
    put16(c + 4, 20);
    put16(c + 6, 10);
    put16(c + 14, 0x0021);
    put32(c + 16, st.m_crc32);
    put32(c + 20, size);
    put32(c + 24, size);
    put16(c + 28, (unsigned)nl);
    put32(c + 42, off);
    memcpy(c + 46, st.m_filename, nl);
    c += 46 + nl;
    off += 30 + (uint32_t)nl + pad + size;
    setup_progress("Unpacking game.apk (once, about a minute)",
                   total ? (int)((uint64_t)off * 800 / total) : 0);
    if (i % 100 == 99)
      debugPrintf("[setup]   %u/%u files\n", (unsigned)(i + 1), (unsigned)n);
  }
  uint8_t e[22] = {0};
  put32(e, 0x06054b50);
  put16(e + 8, n);
  put16(e + 10, n);
  put32(e + 12, (uint32_t)(c - cd));
  put32(e + 16, off);
  if (ok)
    ok = fwrite(cd, 1, (size_t)(c - cd), f) == (size_t)(c - cd) && fwrite(e, 1, 22, f) == 22;
  free(cd);
  if (fclose(f) != 0)
    ok = 0;
  return ok ? 0 : -1;
}

/* game.apk rewritten stored, if it is compressed; the zip reopened on the
 * result. The original is replaced only once its copy has been read back. */
static void ensure_stored(mz_zip_archive *zip, const char *apk) {
  uint64_t need;
  if (!compressed_assets(zip, &need))
    return;
  struct statvfs vs;
  if (statvfs(dcr_game_root(), &vs) == 0 &&
      (uint64_t)vs.f_bavail * vs.f_frsize < need + (16u << 20)) {
    debugPrintf("[setup] game.apk is compressed and there is not room (%llu MB) for its uncompressed "
                "copy: using it as it is -- loading is slower\n", (unsigned long long)(need >> 20));
    return;
  }
  setup_progress("Unpacking game.apk (once, about a minute)", 0);
  debugPrintf("[setup] game.apk is the compressed original: writing it uncompressed, once "
              "(%llu MB; this takes a minute)...\n", (unsigned long long)(need >> 20));
  char tmp[320], old[320];
  snprintf(tmp, sizeof tmp, "%s.part", apk);
  snprintf(old, sizeof old, "%s.original", apk);
  unlink(tmp);
  int ok = write_stored_zip(zip, tmp, need) == 0;
  setup_progress("Checking the unpacked game.apk", 800);
  mz_uint n = mz_zip_reader_get_num_files(zip);
  mz_zip_reader_end(zip);
  if (ok) {
    mz_zip_archive chk;
    uint64_t dummy;
    memset(&chk, 0, sizeof chk);
    ok = mz_zip_reader_init_file(&chk, tmp, 0) && mz_zip_reader_get_num_files(&chk) == n &&
         !compressed_assets(&chk, &dummy);
    mz_zip_reader_end(&chk);
  }
  if (ok) {
    unlink(old);
    ok = rename(apk, old) == 0 && rename(tmp, apk) == 0;
    if (ok)
      unlink(old);
    else if (file_size(apk) < 0)
      rename(old, apk); /* put the original back */
  }
  if (!ok) {
    unlink(tmp);
    debugPrintf("[setup] could not write the uncompressed game.apk: using the compressed one -- "
                "loading is slower\n");
  } else {
    debugPrintf("[setup] game.apk is now uncompressed (%u files)\n", (unsigned)n);
  }
  memset(zip, 0, sizeof *zip);
  if (!mz_zip_reader_init_file(zip, apk, 0))
    fatal_error("%s could not be reopened after being rewritten.", apk);
}

/* ------------------------------------------------ the APKs, under any name
 * The user drops their APKs in the folder under whatever name they came
 * with. Each .apk other than game.apk and dcr.apk is read for its package
 * name (AndroidManifest.xml, binary XML: UTF-16 strings) and renamed:
 *   net.gogame.disney.crossyroad       this game (SEA)          -> game.apk
 *   com.disney.disneycrossyroad*       the world-wide edition   -> dcr.apk
 * replacing the one before it (a newer APK dropped in is an update). Any
 * other APK is left alone. */
static int bytes_have(const uint8_t *b, size_t n, const char *s) {
  size_t k = strlen(s);
  for (size_t i = 0; i + k <= n; i++)
    if (!memcmp(b + i, s, k))
      return 1;
  for (size_t i = 0; i + 2 * k <= n; i++) {
    size_t j = 0;
    while (j < k && b[i + 2 * j] == (uint8_t)s[j] && b[i + 2 * j + 1] == 0)
      j++;
    if (j == k)
      return 1;
  }
  return 0;
}

/* 1 = Disney Crossy Road: SEA, 2 = the world-wide edition, 0 = neither */
static int apk_kind(const char *path) {
  mz_zip_archive zip;
  memset(&zip, 0, sizeof zip);
  if (!mz_zip_reader_init_file(&zip, path, 0))
    return 0;
  size_t n = 0;
  uint8_t *m = mz_zip_reader_extract_file_to_heap(&zip, "AndroidManifest.xml", &n, 0);
  mz_zip_reader_end(&zip);
  int kind = 0;
  if (m) {
    if (bytes_have(m, n, "net.gogame.disney.crossyroad"))
      kind = 1;
    else if (bytes_have(m, n, "com.disney.disneycrossyroad"))
      kind = 2;
    mz_free(m);
  }
  return kind;
}

void dcr_setup_adopt_apks(void) {
  char names[8][128];
  int n = 0;
  DIR *d = opendir(dcr_game_root());
  if (!d)
    return;
  struct dirent *e;
  while ((e = readdir(d)) && n < 8) {
    size_t len = strlen(e->d_name);
    if (len < 5 || len >= sizeof names[0] || strcasecmp(e->d_name + len - 4, ".apk") ||
        !strcasecmp(e->d_name, DCR_APK_NAME) || !strcasecmp(e->d_name, "dcr.apk"))
      continue;
    memcpy(names[n++], e->d_name, len + 1);
  }
  closedir(d);
  for (int i = 0; i < n; i++) {
    char src[320], dst[320];
    root_path(src, sizeof src, names[i]);
    int kind = apk_kind(src);
    if (!kind) {
      debugPrintf("[setup] %s is not a Disney Crossy Road APK: left as it is\n", names[i]);
      continue;
    }
    root_path(dst, sizeof dst, kind == 1 ? DCR_APK_NAME : "dcr.apk");
    remove(dst);
    int ok = rename(src, dst) == 0;
    debugPrintf("[setup] %s is %s: %s %s\n", names[i],
                kind == 1 ? "Disney Crossy Road: SEA" : "the world-wide Disney Crossy Road (DuckTales)",
                ok ? "renamed" : "could NOT be renamed", kind == 1 ? DCR_APK_NAME : "dcr.apk");
  }
}

/* ------------------------------------------------------------- dcr.apk
 * The DuckTales pack (mod/src/Characters.cs) comes from the player's own copy
 * of Disney Crossy Road, the world-wide edition, put next to game.apk as
 * dcr.apk. Its DuckTales character models, music and sounds are asset bundles
 * stored in that APK; they are copied once to data/files/dcr/ (the game's
 * files folder, where Unity opens them), with that APK's GUID-to-asset table
 * and its English theme logos (for the DuckTales card).
 * Each file is copied again only when its CRC in dcr.apk changes. */
static const char *const k_dcr_entries[] = {
    "assets/AssetBundles/android/models-ducktales-characters",
    "assets/AssetBundles/android/music-ducktales",
    "assets/AssetBundles/android/sounds-ducktales",
    "assets/AssetBundles/guids-to-asset-mapping.txt",
    "assets/AssetBundles/android/logos.en-us",
};

static void dcr_from_nro(void);

static void ensure_dcr(void) {
  char apk[300], dir[300];
  root_path(apk, sizeof apk, "dcr.apk");
  if (file_size(apk) < 0) {
    dcr_from_nro(); /* a launcher built with them (launcher/build.sh with a dcr.apk) */
    return;
  }
  mz_zip_archive zip;
  memset(&zip, 0, sizeof zip);
  if (!mz_zip_reader_init_file(&zip, apk, 0)) {
    debugPrintf("[setup] dcr.apk is not a readable APK: no DuckTales pack\n");
    return;
  }
  root_path(dir, sizeof dir, "data");
  mkdir(dir, 0777);
  root_path(dir, sizeof dir, "data/files");
  mkdir(dir, 0777);
  root_path(dir, sizeof dir, "data/files/dcr");
  mkdir(dir, 0777);
  int copied = 0, missing = 0;
  for (unsigned i = 0; i < sizeof k_dcr_entries / sizeof k_dcr_entries[0]; i++) {
    const char *name = strrchr(k_dcr_entries[i], '/') + 1;
    int idx = mz_zip_reader_locate_file(&zip, k_dcr_entries[i], NULL, 0);
    mz_zip_archive_file_stat st;
    if (idx < 0 || !mz_zip_reader_file_stat(&zip, (mz_uint)idx, &st)) {
      missing++;
      continue;
    }
    char dst[340], key[32];
    snprintf(dst, sizeof dst, "%s/%s", dir, name);
    snprintf(key, sizeof key, "dcr%u", i);
    Stamp *stp = stamp_get(key);
    if (file_size(dst) == (long)st.m_uncomp_size && stp && stp->crc == (unsigned long)st.m_crc32)
      continue;
    setup_progress("Copying the DuckTales pack from dcr.apk", 960);
    if (extract_entry(&zip, idx, dst) == 0) {
      stamp_set(key, (unsigned long)st.m_crc32, (unsigned long)st.m_uncomp_size);
      copied++;
    }
  }
  mz_zip_reader_end(&zip);
  if (copied || missing)
    debugPrintf("[setup] dcr.apk: %d DuckTales file(s) copied, %d not in that APK%s\n", copied, missing,
                missing ? " (is it Disney Crossy Road 3.x?)" : "");
}

void dcr_setup_from_apk(const char *apk) {
  mz_zip_archive zip;
  memset(&zip, 0, sizeof zip);
  if (!mz_zip_reader_init_file(&zip, apk, 0))
    return; /* main.c reports a missing or unreadable APK */
  stamp_load();
  ensure_stored(&zip, apk);
  ensure_libs(&zip, apk);
  ensure_classes(&zip);
  mz_zip_reader_end(&zip);
  ensure_dcr();
  stamp_save();
  if (g_setup_shown)
    setup_progress("Starting the game", 1000);
}

/* ---------------------------------------------------- updates from the NRO */
/* The newest launcher NRO in the game folder: its path and build. */
static uint64_t find_nro(char *path, size_t cap) {
  uint64_t best = 0;
  DIR *d = opendir(dcr_game_root());
  if (!d)
    return 0;
  struct dirent *e;
  while ((e = readdir(d))) {
    size_t n = strlen(e->d_name);
    if (n < 5 || strcasecmp(e->d_name + n - 4, ".nro"))
      continue;
    char p[320];
    root_path(p, sizeof p, e->d_name);
    FILE *f = fopen(p, "rb");
    if (!f)
      continue;
    uint64_t b = nro_build(f);
    fclose(f);
    if (b > best) {
      best = b;
      snprintf(path, cap, "%s", p);
    }
  }
  closedir(d);
  return best;
}

/* The DuckTales files a launcher NRO may carry at the top of its romfs, as
 * dcr-<name> (launcher/build.sh puts them there when it is given a dcr.apk):
 * copied out like dcr.apk's, again when the NRO's build changes. */
static void dcr_from_nro(void) {
  char nro[320], dir[300];
  uint64_t build = find_nro(nro, sizeof nro);
  if (!build)
    return;
  FILE *f = fopen(nro, "rb");
  if (!f)
    return;
  int copied = 0, found = 0;
  for (unsigned i = 0; i < sizeof k_dcr_entries / sizeof k_dcr_entries[0]; i++) {
    const char *name = strrchr(k_dcr_entries[i], '/') + 1;
    char rn[96];
    snprintf(rn, sizeof rn, "dcr-%s", name);
    long off = 0;
    size_t len = 0;
    if (nro_romfs_file(f, rn, &off, &len) != 0 || !len)
      continue;
    found++;
    if (found == 1) {
      root_path(dir, sizeof dir, "data");
      mkdir(dir, 0777);
      root_path(dir, sizeof dir, "data/files");
      mkdir(dir, 0777);
      root_path(dir, sizeof dir, "data/files/dcr");
      mkdir(dir, 0777);
    }
    char dst[340], key[32];
    snprintf(dst, sizeof dst, "%s/%s", dir, name);
    snprintf(key, sizeof key, "dcr%u", i);
    Stamp *stp = stamp_get(key);
    if (file_size(dst) == (long)len && stp && stp->crc == (unsigned long)build)
      continue;
    setup_progress("Copying the DuckTales pack", 960);
    uint8_t *buf = malloc(len);
    int ok = buf && fseek(f, off, SEEK_SET) == 0 && fread(buf, 1, len, f) == len;
    if (ok) {
      char tmp[360];
      snprintf(tmp, sizeof tmp, "%s.part", dst);
      FILE *o = fopen(tmp, "wb");
      ok = o && fwrite(buf, 1, len, o) == len;
      if (o)
        fclose(o);
      if (ok) {
        remove(dst);
        ok = rename(tmp, dst) == 0;
      }
    }
    free(buf);
    if (ok) {
      stamp_set(key, (unsigned long)build, (unsigned long)len);
      copied++;
    }
  }
  fclose(f);
  if (found)
    debugPrintf("[setup] the launcher carries the DuckTales pack: %d of %d file(s) copied\n", copied, found);
}

static uint8_t *read_whole(const char *path, size_t *len) {
  FILE *f = fopen(path, "rb");
  if (!f)
    return NULL;
  fseek(f, 0, SEEK_END);
  long n = ftell(f);
  fseek(f, 0, SEEK_SET);
  uint8_t *b = n > 0 ? malloc((size_t)n) : NULL;
  if (b && fread(b, 1, (size_t)n, f) != (size_t)n) {
    free(b);
    b = NULL;
  }
  fclose(f);
  *len = b ? (size_t)n : 0;
  return b;
}

void dcr_setup_update_from_nro(void) {
  u64 tid = 0;
  if (R_FAILED(svcGetInfo(&tid, InfoType_ProgramId, CUR_PROCESS_HANDLE, 0)) || !exefs_is_forwarder_tid(tid))
    return;
  char ovr[128], marker[300], nro[320];
  snprintf(ovr, sizeof ovr, "sdmc:/atmosphere/contents/%016llX/exefs.nsp", (unsigned long long)tid);
  root_path(marker, sizeof marker, ".update");
  if (file_size(ovr) <= 0)
    return; /* not running through an override: nothing of ours to update */

  uint64_t attempted = 0;
  FILE *mf = fopen(marker, "r");
  if (mf) {
    if (fscanf(mf, "%llu", (unsigned long long *)&attempted) != 1)
      attempted = 0;
    fclose(mf);
    if (attempted <= DCR_BUILD)
      unlink(marker); /* that update took */
  }
  uint64_t build = find_nro(nro, sizeof nro);
  debugPrintf("[setup] build %llu%s\n", (unsigned long long)DCR_BUILD,
              build > DCR_BUILD ? "; the launcher NRO carries a newer one" : "");
  if (build <= DCR_BUILD)
    return;
  if (attempted == build) {
    debugPrintf("[setup] %s: build %llu was installed but this is still build %llu -- not retrying "
                "(delete %s to try again)\n", nro, (unsigned long long)build,
                (unsigned long long)DCR_BUILD, marker);
    return;
  }

  /* the override must be this program's own: 32-bit, this title */
  size_t cur_len = 0, npdm_len, nsp_len = 0;
  uint8_t *cur = read_whole(ovr, &cur_len);
  const uint8_t *npdm;
  uint64_t pid = 0;
  int is64 = 1;
  int ours = cur && exefs_find(cur, cur_len, "main.npdm", &npdm, &npdm_len) == 0 &&
             npdm_info(npdm, npdm_len, &pid, &is64) == 0 && pid == tid && !is64;
  free(cur);
  if (!ours)
    return;

  FILE *f = fopen(nro, "rb");
  long off;
  uint8_t *nsp = NULL, *out = NULL;
  size_t out_len = 0;
  if (f && nro_romfs_file(f, "dcrsea_nx.nsp", &off, &nsp_len) == 0 && (nsp = malloc(nsp_len)) &&
      fseek(f, off, SEEK_SET) == 0 && fread(nsp, 1, nsp_len, f) == nsp_len)
    exefs_build_override(nsp, nsp_len, tid, &out, &out_len);
  if (f)
    fclose(f);
  free(nsp);
  if (!out) {
    debugPrintf("[setup] %s: its copy of the wrapper is unreadable -- not updating\n", nro);
    return;
  }
  setup_progress("Updating to the new build, then restarting", 1000);
  debugPrintf("[setup] updating to build %llu from %s, then restarting...\n", (unsigned long long)build, nro);
  char tmp[160];
  snprintf(tmp, sizeof tmp, "%s.part", ovr);
  FILE *o = fopen(tmp, "wb");
  int ok = o && fwrite(out, 1, out_len, o) == out_len;
  if (o && fclose(o) != 0)
    ok = 0;
  free(out);
  if (ok) {
    unlink(ovr);
    ok = rename(tmp, ovr) == 0;
  }
  if (!ok) {
    unlink(tmp);
    debugPrintf("[setup] could not write %s -- still running build %llu\n", ovr, (unsigned long long)DCR_BUILD);
    return;
  }
  mf = fopen(marker, "w");
  if (mf) {
    fprintf(mf, "%llu\n", (unsigned long long)build);
    fclose(mf);
  }
  log_flush_ring();
  Result rc = appletRestartProgram(NULL, 0);
  fatal_error("Updated to build %llu from %s.\n\n"
              "Restarting did not work (0x%x): close the game and launch it again.",
              (unsigned long long)build, nro, (unsigned)rc);
}
