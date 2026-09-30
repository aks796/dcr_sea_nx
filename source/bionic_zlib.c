/* bionic_zlib.c -- the three libz imports, on miniz (libnx32 ships libminiz).
 *
 * Only libunity uses zlib, and only for inflate (it reads compressed entries
 * of the APK/OBB and its own compressed data). Its only inflateInit2_ passes
 * windowBits = -15 (raw deflate), which miniz supports. zlib's z_stream and
 * miniz's mz_stream have the same 56-byte layout on 32-bit ARM (all fields are
 * pointers, unsigned int and unsigned long), and the flush/return codes are
 * zlib's, so the stream the engine owns passes straight through. MIT.
 */
#define MINIZ_NO_ZLIB_COMPATIBLE_NAMES
#include <miniz/miniz.h>

#include "util.h"

_Static_assert(sizeof(mz_stream) == 56, "mz_stream must match zlib's 32-bit z_stream");

int b_inflateInit2_(mz_streamp strm, int window_bits, const char *version, int stream_size) {
  if (stream_size != (int)sizeof(mz_stream))
    return MZ_VERSION_ERROR;
  int r = mz_inflateInit2(strm, window_bits);
  if (r != MZ_OK)
    debugPrintf("[zlib] inflateInit2(windowBits=%d) failed: %d\n", window_bits, r);
  return r;
}

int b_inflate(mz_streamp strm, int flush) { return mz_inflate(strm, flush); }
int b_inflateEnd(mz_streamp strm) { return mz_inflateEnd(strm); }
