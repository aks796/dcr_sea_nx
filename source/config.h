/* config.h -- Disney Crossy Road's own build-wide constants (the runtime's
 * settings are in port_config.h).
 *
 * Disney Crossy Road SEA 1.5.4: Unity 5.6.4f1, Mono backend, armeabi-v7a.
 * MIT.
 */
#ifndef DCR_CONFIG_H
#define DCR_CONFIG_H

#include "rt_settings.h"

/* The game's modules, and where Unity's data is inside the APK. */
#define DCR_LIB_MAIN    "libmain.so"
#define DCR_LIB_UNITY   "libunity.so"
#define DCR_LIB_MONO    "libmono.so"
#define DCR_DATA_DIR    "assets/bin/Data"
#define DCR_MANAGED_DIR "assets/bin/Data/Managed"
#define DCR_PKG_NAME    PORT_PACKAGE

/* Feature switches wired against dcr_offsets.h. */
#define DCR_PATCH_CHOREOGRAPHER 1   /* patch ChoreographerEnableVSync -> ret */
#define DCR_PATCH_VSYNC         1   /* pump the vsync counter from a thread   */
#define DCR_TIME_ICALL_HOOKS    1   /* hook the 11 Time icalls                */

/* Mono runtime policy (see mono_rt.c): avoid the signal paths Horizon can't
 * service. Coop suspend removes the tkill dependency; explicit null checks
 * remove the SIGSEGV dependency. */
#define DCR_MONO_COOP_SUSPEND        1
#define DCR_MONO_EXPLICIT_NULLCHECKS 1

#endif /* DCR_CONFIG_H */
