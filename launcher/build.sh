#!/bin/sh
# Build dcr_sea_nx.nro (the launcher) in devkitPro's 64-bit toolchain
# container. Build the wrapper first (../build.sh): the NRO carries
# ../dcrsea_nx.nsp and ../dcrsea_nx.build.
#
# The DuckTales pack can travel in the NRO too, so the SD card needs no
# dcr.apk: give this script a Disney Crossy Road (world-wide, 3.x) APK --
#   DCR_APK=/path/to/Disney+Crossy+Road_3.x.apk launcher/build.sh
# (or put it next to the wrapper as dcr.apk). Its five DuckTales files go into
# the NRO's romfs as dcr-<name>; the game copies them out at start-up
# (source/dcr_setup.c). The release NRO is built this way: the game is free,
# and the player already supplies their own SEA APK, which these files do
# nothing without.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
[ -f "$HERE/../dcrsea_nx.nsp" ] && [ -f "$HERE/../dcrsea_nx.build" ] || { echo "build the wrapper first (../build.sh)"; exit 1; }
DCR="${DCR_APK:-$HERE/../dcr.apk}"
mkdir -p "$HERE/romfs"
rm -f "$HERE"/romfs/dcr-* "$HERE/dcr_sea_nx.nro"
if [ -f "$DCR" ]; then
  for e in assets/AssetBundles/android/models-ducktales-characters assets/AssetBundles/android/music-ducktales \
           assets/AssetBundles/android/sounds-ducktales assets/AssetBundles/guids-to-asset-mapping.txt \
           assets/AssetBundles/android/logos.en-us; do
    unzip -p "$DCR" "$e" > "$HERE/romfs/dcr-$(basename "$e")" || { echo "$DCR has no $e"; rm -f "$HERE"/romfs/dcr-*; exit 1; }
  done
  echo "launcher: the DuckTales pack from $(basename "$DCR") goes in the NRO"
fi
exec docker run --rm --platform linux/amd64 \
  -v "$HERE/..:/work" -w /work/launcher devkitpro/devkita64:latest \
  bash -lc "make -j\$(nproc) $*"
