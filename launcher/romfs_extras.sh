# romfs_extras.sh -- sourced by the runtime's launcher/build.sh (HERE, PORT,
# ROMFS set). The DuckTales pack from a world-wide Disney Crossy Road (3.x)
# APK goes into the NRO's romfs as dcr-<name>; the game copies the files out
# at start-up (source/dcr_setup_plan.c). The release NRO is built this way:
# the game is free, and the player already supplies their own SEA APK, which
# these files do nothing without.
DCR="${DCR_APK:-$PORT/dcr.apk}"
rm -f "$ROMFS"/dcr-* "$HERE/dcr_sea_nx.nro"
if [ -f "$DCR" ]; then
  for e in assets/AssetBundles/android/models-ducktales-characters assets/AssetBundles/android/music-ducktales \
           assets/AssetBundles/android/sounds-ducktales assets/AssetBundles/guids-to-asset-mapping.txt \
           assets/AssetBundles/android/logos.en-us; do
    unzip -p "$DCR" "$e" > "$ROMFS/dcr-$(basename "$e")" || { echo "$DCR has no $e"; rm -f "$ROMFS"/dcr-*; exit 1; }
  done
  echo "launcher: the DuckTales pack from $(basename "$DCR") goes in the NRO"
fi
