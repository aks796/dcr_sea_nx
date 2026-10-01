/* port_config.h -- Disney Crossy Road's settings for the android32 runtime.
 *
 * Macros only: the runtime's C files, its assembly and the launcher all read
 * this (runtime/source/rt_settings.h). What each setting does is next to its
 * default in the runtime; runtime/docs/ lists them all. MIT.
 */
#ifndef PORT_CONFIG_H
#define PORT_CONFIG_H

/* ------------------------------------------------------------------ the game */
#define PORT_TITLE    "Disney Crossy Road"
#define PORT_NAME     "dcr_sea_nx"
#define PORT_PACKAGE  "net.gogame.disney.crossyroad"
#define PORT_BANNER   "dcr_sea_nx: Disney Crossy Road (Southeast Asia edition 1.5.4; Unity 5.6.4f1 / Mono, armeabi-v7a)"
/* up to build 202609300710 the folder was /switch/disneycrossyroadsea */
#define PORT_OLD_ROOT_PATHS "/switch/disneycrossyroadsea"
/* libmain + libunity + libmono end to end (~22 MB mapped), with room for a
 * content update; under-reserving shows up as so_load() -3 */
#define PORT_SO_REGION_BYTES (48u * 1024 * 1024)

/* The APKs, by what is in them: the SEA game becomes game.apk; the
 * world-wide edition (for DuckTales, optional: the NRO carries its files)
 * becomes dcr.apk. */
#define PORT_APK_DESC "Disney Crossy Road, Southeast Asia edition 1.5.4 (armeabi-v7a)"
#define PORT_APK_ROLES                                                                        \
  {.what = "Disney Crossy Road: SEA", .name = "game.apk",                                   \
   .package = "net.gogame.disney.crossyroad", .flags = RT_APK_ADOPT},                        \
  {.what = "the world-wide Disney Crossy Road (DuckTales)", .name = "dcr.apk",               \
   .package = "com.disney.disneycrossyroad",                                                 \
   .flags = RT_APK_ADOPT | RT_APK_OPTIONAL | RT_APK_PACKAGE_PREFIX}
#define PORT_LAUNCHER_START_NOTE "(the first start unpacks the game's libraries from the APK)"

/* ------------------------------------------------------------------ libc */
#define RT_PROC_COMM        "disneycrossyro"
#define RT_EXTRA_ENV        "MONO_DEBUG=explicit-null-checks" /* load-bearing: Mono without SIGSEGV */
#define RT_DL_HAS_OPENSLES  0 /* FMOD falls back to its Java output (dcr_audio.c) */
#define RT_ZLIB_HOLDBACK    0

/* ------------------------------------------------------------------ JNI, NDK */
#define RT_JNI_UNHANDLED_INSTANCE_SINGLETON   1 /* Unity's C# plugins */
#define RT_JNI_UNHANDLED_BUILDER_RETURNS_SELF 1
#define RT_LOOPER_MAIN_IMPLICIT               0

/* ------------------------------------------------------------------ frames */
#define RT_GL_SWAP_ENDS_FRAME 0 /* the frame is nativeRender (dcr_boot.c) */
#define RT_CAPTURE_TRIES      300
#define RT_BOOST_LAUNCH_AFTER_PICTURE_MS 25000
#define RT_MAIN_THREAD_NAME   "UnityMain"
#define RT_PAD_MAX_PLAYERS    8

/* ------------------------------------------------------------------ watchdog */
#define RT_WATCHDOG_PRIO 0x1C
#define RT_WATCHDOG_CORE 2
#define RT_WATCHDOG_BOOT 1

/* ------------------------------------------------------------------ files */
#define RT_PATH_CWD_RELATIVE   0
#define RT_DIRCACHE            0 /* Mono probes and writes under data/ */
#define RT_MANIFEST_LOG_PREFIX "unity"
#define RT_EMU_FIXUPS          0 /* its self-fix runs after the module fix (dcr_main.c) */

#endif
