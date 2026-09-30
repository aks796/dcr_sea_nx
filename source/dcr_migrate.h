/* dcr_migrate.h -- the game folder's old name.
 *
 * Up to build 202609300710 the game folder was /switch/disneycrossyroadsea;
 * it is now /switch/dcr_sea_nx, named like the NRO. Whatever the old folder
 * holds (the APKs, config.ini, the save, leaderboard.txt, profiles, the
 * unpacked libraries) is moved into the new one, entry by entry: a rename on
 * the same card, nothing copied. An entry the new folder already has stays
 * where it is, and so do the old NROs. The launcher and the wrapper both call
 * this, whichever runs first after the update. Returns the entries moved.
 * MIT.
 */
#ifndef DCR_MIGRATE_H
#define DCR_MIGRATE_H

#include <dirent.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <sys/stat.h>

#define DCR_OLD_GAME_DIR "sdmc:/switch/disneycrossyroadsea"

static inline int dcr_migrate_old_folder(const char *to) {
  DIR *d = opendir(DCR_OLD_GAME_DIR);
  if (!d)
    return 0;
  /* names first: renaming while the directory is being read skips entries */
  static char names[96][128];
  int n = 0;
  struct dirent *e;
  while ((e = readdir(d)) && n < 96) {
    size_t len = strlen(e->d_name);
    if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..") || len >= sizeof names[0])
      continue;
    if (len > 4 && !strcasecmp(e->d_name + len - 4, ".nro"))
      continue;
    memcpy(names[n++], e->d_name, len + 1);
  }
  closedir(d);
  mkdir(to, 0777);
  int moved = 0;
  for (int i = 0; i < n; i++) {
    char a[320], b[320];
    struct stat st;
    snprintf(a, sizeof a, "%s/%.127s", DCR_OLD_GAME_DIR, names[i]);
    snprintf(b, sizeof b, "%.160s/%.127s", to, names[i]);
    if (stat(b, &st) == 0)
      continue;
    if (rename(a, b) == 0)
      moved++;
  }
  return moved;
}

#endif
