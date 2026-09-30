#!/bin/sh
# Build mod/dcrmod.dll -- the port's own C#, loaded into the game's Mono by
# source/dcr_mod.c -- with the Roslyn compiler of the .NET SDK image.
#
# Compiled for the game's runtime: Unity 5.6's Mono 2.x and its .NET 3.5
# class library, referenced from mod/refs/ (made from YOUR copy of the game by
# tools/mod/publicize.py; never shipped). C# 7.3 features that need nothing
# newer than that library are fine (no ValueTuple, async or Span).
#
#   mod/build_mod.sh          # mod/dcrmod.dll
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
IMAGE="${DCR_DOTNET_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}"
if [ ! -f "$HERE/refs/Assembly-CSharp.dll" ]; then
  echo "build_mod.sh: no mod/refs/ -- run: python3 tools/mod/publicize.py <apktool dir or game.apk>" >&2
  exit 1
fi
REFS="mscorlib System System.Core System.Xml UnityEngine UnityEngine.UI UnityEngine.Networking \
Assembly-CSharp Assembly-CSharp-firstpass MDebug-Release proto protobuf-net ProtoSerializer"
ARGS=""
for r in $REFS; do ARGS="$ARGS -r:/w/refs/$r.dll"; done
exec docker run --rm -v "$HERE:/w" -w /w "$IMAGE" bash -c "
  dotnet /usr/share/dotnet/sdk/*/Roslyn/bincore/csc.dll -nologo -noconfig -nostdlib+ \
    -t:library -langversion:7.3 -runtimemetadataversion:v2.0.50727 -optimize+ -unsafe+ \
    -deterministic -debug- -warn:4 -nowarn:0618,0649,0169,0414 \
    $ARGS -out:/w/dcrmod.dll src/*.cs && ls -l dcrmod.dll"
