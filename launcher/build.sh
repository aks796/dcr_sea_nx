#!/bin/sh
# Build dcr_sea_nx.nro (the launcher) with the runtime's launcher build
# (devkitPro's 64-bit toolchain container). Build the wrapper first
# (../build.sh): the NRO carries ../dcrsea_nx.nsp and ../dcrsea_nx.build.
#
# The DuckTales pack can travel in the NRO too, so the SD card needs no
# dcr.apk: give this script a Disney Crossy Road (world-wide, 3.x) APK --
#   DCR_APK=/path/to/Disney+Crossy+Road_3.x.apk launcher/build.sh
# (or put it next to the wrapper as dcr.apk; romfs_extras.sh).
HERE="$(cd "$(dirname "$0")" && pwd)"
LAUNCHER_DIR="$HERE" PAYLOAD=dcrsea_nx exec "$HERE/../runtime/launcher/build.sh" "$@"
