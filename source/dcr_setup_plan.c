/* dcr_setup_plan.c -- Disney Crossy Road's part of the first launch: its
 * setup plan for the runtime's dcr_setup.c, and its own steps.
 *
 * The runtime's dcr_setup.c does the common work from this plan:
 *   game.apk rewritten stored when its assets/ are compressed (Unity would
 *   inflate each .split at every map load), then the three libraries, then
 *   classes.txt, then the DuckTales pack (below), then "Starting the game".
 *
 * The bar, in permille of the whole first launch (as the port's own had it):
 *     0- 800  game.apk rewritten uncompressed (by bytes written)
 *   800- 820  the rewritten copy checked
 *   820- 940  libmain / libunity / libmono unpacked (by bytes)
 *   940- 960  the Java class list
 *   960- 990  the DuckTales pack, from dcr.apk or the launcher NRO
 *        1000 the game starts
 *
 * .setup keys, which must not change (or every player unpacks again once):
 * libmain.so, libunity.so, libmono.so, classes.txt, dcr0..dcr4. MIT.
 */
#include <stdio.h>
#include <string.h>

#include "dcr_path.h"
#include "dcr_setup.h"
#include "rt_settings.h"
#include "util.h"

static const char *const k_libs[] = {"libmain.so", "libunity.so", "libmono.so"};

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
#define N_DCR ((unsigned)(sizeof k_dcr_entries / sizeof k_dcr_entries[0]))

/* file i's part of the step's bar */
static int slice(const RtSetupCtx *ctx, unsigned i) { return ctx->p0 + (ctx->p1 - ctx->p0) * (int)i / (int)N_DCR; }

/* The same files a launcher NRO may carry at the top of its romfs, as
 * dcr-<name> (launcher/build.sh puts them there when it is given a dcr.apk):
 * copied out like dcr.apk's, again when the NRO's build changes (the build
 * is the stamp's crc). */
static void ducktales_from_nro(RtSetupCtx *ctx) {
  int copied = 0, failed = 0;
  for (unsigned i = 0; i < N_DCR; i++) {
    const char *name = strrchr(k_dcr_entries[i], '/') + 1;
    char rn[96], dst[128], key[32];
    snprintf(rn, sizeof rn, "dcr-%s", name);
    snprintf(dst, sizeof dst, "data/files/dcr/%s", name);
    snprintf(key, sizeof key, "dcr%u", i);
    int r = rt_setup_copy_from_nro(rn, dst, key, "Copying the DuckTales pack", slice(ctx, i), slice(ctx, i + 1));
    copied += r == 2;
    failed += r < 0;
  }
  if (copied || failed)
    debugPrintf("[setup] the launcher carries the DuckTales pack: %d file(s) copied, %d failed\n", copied, failed);
}

static void ducktales(RtSetupCtx *ctx) {
  char apk[300], dir[300];
  rt_root_path(apk, sizeof apk, "dcr.apk");
  if (rt_file_size(apk) < 0) {
    ducktales_from_nro(ctx); /* a launcher built with them (launcher/build.sh with a dcr.apk) */
    return;
  }
  mz_zip_archive zip;
  memset(&zip, 0, sizeof zip);
  if (!mz_zip_reader_init_file(&zip, apk, 0)) {
    debugPrintf("[setup] dcr.apk is not a readable APK: no DuckTales pack\n");
    return;
  }
  rt_root_path(dir, sizeof dir, "data/files/dcr");
  int copied = 0, missing = 0;
  for (unsigned i = 0; i < N_DCR; i++) {
    const char *name = strrchr(k_dcr_entries[i], '/') + 1;
    int idx = mz_zip_reader_locate_file(&zip, k_dcr_entries[i], NULL, 0);
    mz_zip_archive_file_stat st;
    if (idx < 0 || !mz_zip_reader_file_stat(&zip, (mz_uint)idx, &st)) {
      missing++;
      continue;
    }
    char dst[340], key[32];
    unsigned long crc = 0;
    snprintf(dst, sizeof dst, "%s/%s", dir, name);
    snprintf(key, sizeof key, "dcr%u", i);
    if (rt_file_size(dst) == (long)st.m_uncomp_size && rt_setup_stamp_get(key, &crc, NULL) &&
        crc == (unsigned long)st.m_crc32)
      continue;
    if (rt_setup_extract_entry(&zip, idx, dst, "Copying the DuckTales pack from dcr.apk", slice(ctx, i),
                               slice(ctx, i + 1)) == 0) {
      rt_setup_stamp_set(key, (unsigned long)st.m_crc32, (unsigned long)st.m_uncomp_size);
      copied++;
    }
  }
  mz_zip_reader_end(&zip);
  if (copied || missing)
    debugPrintf("[setup] dcr.apk: %d DuckTales file(s) copied, %d not in that APK%s\n", copied, missing,
                missing ? " (is it Disney Crossy Road 3.x?)" : "");
}

static const RtSetupStep k_steps[] = {
    {"DuckTales pack", RT_STEP_AFTER_ZIP, 960, 990, ducktales},
};

const RtSetupPlan port_setup_plan = {
    .libs = k_libs,
    .nlibs = sizeof k_libs / sizeof k_libs[0],
    .libs_what = "Unpacking the game's libraries",
    .apk_requirement = "This port needs the 32-bit ARM (armeabi-v7a) build of Disney Crossy\n"
                       "Road, Southeast Asia edition, version 1.5.4: use an APK of that version.",
    .libs_p0 = 820,
    .libs_p1 = 940,
    .classes_p0 = 940,
    .classes_p1 = 960,
    .store_apk_prefix = "assets/",
    .store_p0 = 0,
    .store_p1 = 800,
    .steps = k_steps,
    .nsteps = sizeof k_steps / sizeof k_steps[0],
};
