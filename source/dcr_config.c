/* dcr_config.c -- <game folder>/config.ini, the user's settings.
 *
 * Written with every option, its default and a line of explanation on the
 * first start; an existing file is appended to (options a newer build adds,
 * at the end, with their defaults), so edits and comments survive updates.
 * The one exception is a changed default: a value still at the old default is
 * moved to the new one, once, by line ([config] version records it). Plain INI: [section], key = value, # comments;
 * booleans take true/false, yes/no, on/off, 1/0. Read once at start-up:
 * changes apply the next time the game starts.
 *
 * The flag files of earlier builds (keep_locks, hop_on_release) are carried
 * into the first config.ini written. MIT.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/stat.h>
#include <switch.h>

#include "dcr_build.h"
#include "dcr_config.h"
#include "util.h"

const char *dcr_game_root(void); /* main.c */
void dcr_window_set_size(int w, int h); /* android_ndk.c */

static DcrConfig g_cfg = {1, 1, 1, 1, 1280, 720, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1};

const DcrConfig *dcr_config(void) { return &g_cfg; }

typedef struct {
  const char *section, *key, *def, *help;
} Opt;

enum { O_UNLOCK, O_STARS, O_TOPBAR, O_STORE, O_DUCKTALES, O_SORCERER, O_EXTRAS, O_PROFILES, O_HOP, O_MENUS, O_ONLINE, O_LOCALMP, O_MATCHSECS, O_RES, O_BOOST, O_WIPE, O_BUNDLES, O_SNDPRIO, O_QUIETCENTER, O_MIX48, O_GLTEST, O_PROFILE, O_BOOTLOG, O_SOUNDS, O_MOD, O_VERSION, O_COUNT };

static const Opt k_opts[O_COUNT] = {
    {"game", "unlock_all_characters", "true",
     "Every character and collection unlocked (the online events that give\n"
     "# some away never come here)."},
    {"game", "max_character_stars", "true",
     "Every character at its maximum star level, with all of that level's boosts."},
    {"game", "hide_top_bar", "true",
     "Hide the thin coloured bar across the top of the screen (the game's mode bar)."},
    {"game", "free_purchases", "true",
     "The store is free: ticket packs, pixel packs and bundles are yours for\n"
     "# the asking (the game's own offline store; there is no Google Play here)."},
    {"game", "ducktales", "true",
     "The DuckTales pack from Disney Crossy Road (the world-wide edition): its\n"
     "# characters, world, music and sounds, as its own section of the character\n"
     "# select. Read at start-up from your own copy of that game: put its APK in\n"
     "# this folder as dcr.apk (without it, this does nothing)."},
    {"game", "sorcerer_mickey", "true",
     "Sorcerer's Apprentice Mickey, an Epic figurine in Mickey & Friends (his\n"
     "# models are in the game's files but he was never released). Like the Genie,\n"
     "# he turns the obstacles ahead of him into Fantasia's marching brooms."},
    {"game", "hidden_characters", "true",
     "The figurines whose models are in the game's files but not in its character\n"
     "# select: Golden Camel, Dragon Genie, Elf Pleakley, Santa Jumba, Vampire\n"
     "# Stitch, Witch Lilo and The Ocean (released only in the world-wide edition),\n"
     "# Classic Mickey, the Fantasia Broom, Safari Mickey, Max, Oswald, Ortensia,\n"
     "# the Scuba Diver and Human Cadenza (never released), and the hidden Meowana\n"
     "# and Hanging Tree. Each in its own theme, won in the prize machine."},
    {"game", "profiles", "true",
     "Switch profiles on the leaderboard and in multiplayer. The first time the\n"
     "# leaderboard opens, the Switch's profile picker comes up (B: no profile,\n"
     "# not asked again); X in the leaderboard changes it, - drops it. Your runs\n"
     "# then go on that profile's name and icon. In multiplayer each player can\n"
     "# press X in the waiting room to play as a profile. Scores are kept in\n"
     "# leaderboard.txt next to this file."},
    {"controls", "hop_on_press", "true",
     "Hop the moment A or a direction is pressed. false: the game's own\n"
     "# crouch on press, hop on release."},
    {"controls", "menu_controls", "true",
     "Every menu works with the controller: A presses, B goes back, + pauses,\n"
     "# L/R change theme in the character select, and the screens open with a\n"
     "# button already chosen. Touch still works."},
    {"online", "pretend_online", "true",
     "The game thinks it is online and signed in, answered by the port itself: no\n"
     "# \"no connection\" popups, and the prize machine, daily missions, weekend\n"
     "# challenges (a different one each week), the ticket machine, free gifts,\n"
     "# leaderboards, the daily login reward and coin drops in runs all work;\n"
     "# \"watch an ad\" rewards are given at once."},
    {"multiplayer", "local_multiplayer", "true",
     "The multiplayer button starts LOCAL multiplayer: up to 4 players on this\n"
     "# Switch, one controller (or one Joy-Con held sideways) each. Four modes:\n"
     "# Coin Crazy, Snatch and Run, Last One Standing and Hop Non-Stop (the last\n"
     "# two are knock-outs: no timer, one life each). Player 1 picks the world\n"
     "# with L/R in the waiting room; + pauses a match (B then ends it)."},
    {"multiplayer", "match_seconds", "120",
     "How long a Coin Crazy or Snatch and Run match lasts, in seconds (30-900)."},
    {"display", "resolution", "auto",
     "Rendering resolution: auto (1080 if docked when the game starts, 720 in\n"
     "# handheld), 1080 or 720. The Switch scales the picture to the screen either\n"
     "# way. 1080 in handheld drops to 30 fps: the handheld GPU cannot keep up."},
    {"performance", "boost_cpu_when_loading", "true",
     "CPU at 1785 MHz while the game starts (until its title menu, through\n"
     "# the logos) and inside loading frames (those over 50 ms), normal otherwise."},
    {"performance", "shorter_transitions", "true",
     "Start loading as soon as the screen wipe has covered the screen (0.6 s)\n"
     "# instead of 0.8 s after it began: map changes 0.2 s faster."},
    {"performance", "bundles_in_place", "true",
     "Open the game's asset bundles (characters, worlds, music) where they lie\n"
     "# inside game.apk. Off: the game's own way, which copies each one into a\n"
     "# cache on the SD card first (slow on a first start)."},
    {"audio", "important_sounds_first", "true",
     "Every sound playing is heard: 64 voices instead of the game's 32 (a busy\n"
     "# road plays 40-50 sounds at once, and the extra ones -- hops, car engines,\n"
     "# crowds -- were silent), and hops and clicks come before ambient loops."},
    {"audio", "one_theme_switch_sound", "true",
     "Changing theme in the character selector plays its sound once (the\n"
     "# carousel's click no longer lands on top of it). The SEA edition already\n"
     "# plays one: nothing to change there."},
    {"audio", "mix_at_48khz", "true",
     "Mix the game's audio at 48 kHz, as phones do. Off: 24 kHz, the Android\n"
     "# fallback's rate (muffled music, harsh effects)."},
    {"debug", "gl_selftest", "false", "Graphics self-test picture at start-up."},
    {"debug", "profile_long_frames", "false",
     "Write where the time goes in frames over 100 ms (loading) to debug.log.\n"
     "# It slows those frames down a little: leave off unless asked for a log."},
    {"debug", "boot_log_on_screen", "false",
     "Show the start-up log on screen at every launch. Off: the screen stays\n"
     "# dark until the game draws, and the log appears only while something is\n"
     "# being set up or updated (first launch, a new game.apk or NRO)."},
    {"debug", "log_sounds", "false",
     "Write every sound the game plays (and its volume faders) to debug.log\n"
     "# (for a bug report about audio)."},
    {"debug", "port_mod", "true",
     "The port's own additions to the game (menu controls, local multiplayer,\n"
     "# the extra characters, the online spoof). Off only to rule them out."},
    {"config", "version", "3", "Settings file format; leave as it is."},
};

static char g_val[O_COUNT][16];
static int g_have[O_COUNT];

static void path_of(char *out, size_t cap, const char *name) {
  snprintf(out, cap, "%s/%s", dcr_game_root(), name);
}

static int exists(const char *name) {
  char p[300];
  struct stat st;
  path_of(p, sizeof p, name);
  return stat(p, &st) == 0;
}

static char *trim(char *s) {
  while (*s == ' ' || *s == '\t')
    s++;
  char *e = s + strlen(s);
  while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r' || e[-1] == '\n'))
    *--e = 0;
  return s;
}

static void parse(FILE *f) {
  char line[256], section[32] = "";
  while (fgets(line, sizeof line, f)) {
    char *s = trim(line);
    if (!*s || *s == '#' || *s == ';')
      continue;
    if (*s == '[') {
      char *e = strchr(s, ']');
      if (e) {
        *e = 0;
        snprintf(section, sizeof section, "%s", trim(s + 1));
      }
      continue;
    }
    char *eq = strchr(s, '=');
    if (!eq)
      continue;
    *eq = 0;
    char *key = trim(s), *val = trim(eq + 1);
    char *hash = strpbrk(val, "#;");
    if (hash) {
      *hash = 0;
      val = trim(val);
    }
    for (int i = 0; i < O_COUNT; i++)
      if (!strcasecmp(section, k_opts[i].section) && !strcasecmp(key, k_opts[i].key)) {
        snprintf(g_val[i], sizeof g_val[i], "%s", val);
        g_have[i] = 1;
      }
  }
}

static void write_opts(FILE *f, int only_missing) {
  const char *last = NULL;
  for (int i = 0; i < O_COUNT; i++) {
    if (only_missing && g_have[i])
      continue;
    if (!last || strcmp(last, k_opts[i].section))
      fprintf(f, "\n[%s]\n", k_opts[i].section);
    last = k_opts[i].section;
    fprintf(f, "# %s\n%s = %s\n", k_opts[i].help, k_opts[i].key, g_val[i]);
  }
}

/* Rewrite the value of `key` in the file, keeping every other line. */
static void migrate_line(const char *path, const char *key, const char *value) {
  FILE *f = fopen(path, "r");
  if (!f)
    return;
  fseek(f, 0, SEEK_END);
  long n = ftell(f);
  fseek(f, 0, SEEK_SET);
  char *in = n > 0 ? malloc((size_t)n + 1) : NULL;
  size_t got = in ? fread(in, 1, (size_t)n, f) : 0;
  fclose(f);
  if (!in)
    return;
  in[got] = 0;
  char *out = malloc(got + 128);
  if (!out) {
    free(in);
    return;
  }
  size_t o = 0, klen = strlen(key);
  for (char *line = in; *line;) {
    char *nl = strchr(line, '\n');
    size_t len = nl ? (size_t)(nl - line) + 1 : strlen(line);
    char *t = line;
    while (*t == ' ' || *t == '\t')
      t++;
    if (!strncasecmp(t, key, klen) && (t[klen] == ' ' || t[klen] == '=' || t[klen] == '\t'))
      o += (size_t)sprintf(out + o, "%s = %s\n", key, value);
    else {
      memcpy(out + o, line, len);
      o += len;
    }
    line += len;
  }
  if ((f = fopen(path, "w"))) {
    fwrite(out, 1, o, f);
    fclose(f);
  }
  free(in);
  free(out);
}

static int as_bool(int i) {
  const char *v = g_val[i];
  if (!strcasecmp(v, "true") || !strcasecmp(v, "yes") || !strcasecmp(v, "on") || !strcmp(v, "1"))
    return 1;
  if (!strcasecmp(v, "false") || !strcasecmp(v, "no") || !strcasecmp(v, "off") || !strcmp(v, "0"))
    return 0;
  debugPrintf("[config] %s = %s: not true/false, using %s\n", k_opts[i].key, v, k_opts[i].def);
  return !strcmp(k_opts[i].def, "true");
}

void dcr_config_load(void) {
  for (int i = 0; i < O_COUNT; i++)
    snprintf(g_val[i], sizeof g_val[i], "%s", k_opts[i].def);
  char path[300];
  path_of(path, sizeof path, "config.ini");
  FILE *f = fopen(path, "r");
  if (f) {
    parse(f);
    fclose(f);
    if (!g_have[O_VERSION] && g_have[O_RES] && !strcmp(g_val[O_RES], "1080")) {
      /* version 1 (build 202609241315) defaulted to 1080; 1080 in handheld
       * runs at 30 fps, so that default becomes auto */
      migrate_line(path, "resolution", "auto");
      snprintf(g_val[O_RES], sizeof g_val[0], "auto");
      debugPrintf("[config] resolution 1080 (the old default) -> auto\n");
    }
    int ver = g_have[O_VERSION] ? atoi(g_val[O_VERSION]) : 1;
    if (ver < 3 && g_have[O_PROFILE] && !strcmp(g_val[O_PROFILE], "true")) {
      /* version 2 (builds 202609241343-1403) had the profiler on by default */
      migrate_line(path, "profile_long_frames", "false");
      snprintf(g_val[O_PROFILE], sizeof g_val[0], "false");
      debugPrintf("[config] profile_long_frames true (the old default) -> false\n");
    }
    if (ver < 3) {
      migrate_line(path, "version", "3");
      snprintf(g_val[O_VERSION], sizeof g_val[0], "3");
    }
    int missing = 0;
    for (int i = 0; i < O_COUNT; i++)
      missing += !g_have[i];
    if (missing && (f = fopen(path, "a"))) {
      fprintf(f, "\n# Added by build %llu (new options, at their defaults):\n",
              (unsigned long long)DCR_BUILD);
      write_opts(f, 1);
      fclose(f);
      debugPrintf("[config] added %d new option%s to config.ini\n", missing, missing > 1 ? "s" : "");
    }
  } else {
    /* first start: carry over the flag files of earlier builds */
    if (exists("keep_locks")) {
      snprintf(g_val[O_UNLOCK], sizeof g_val[0], "false");
      snprintf(g_val[O_STARS], sizeof g_val[0], "false");
    }
    if (exists("hop_on_release"))
      snprintf(g_val[O_HOP], sizeof g_val[0], "false");
    if (exists("gltest"))
      snprintf(g_val[O_GLTEST], sizeof g_val[0], "true");
    if ((f = fopen(path, "w"))) {
      fputs("# Disney Crossy Road for Switch -- settings.\n"
            "# Changes apply the next time the game starts. Delete this file to get\n"
            "# the defaults back.\n",
            f);
      write_opts(f, 0);
      fclose(f);
      debugPrintf("[config] wrote config.ini with the defaults\n");
    }
  }

  g_cfg.unlock_all = as_bool(O_UNLOCK);
  g_cfg.max_stars = as_bool(O_STARS);
  g_cfg.hide_top_bar = as_bool(O_TOPBAR);
  g_cfg.free_store = as_bool(O_STORE);
  g_cfg.hop_on_press = as_bool(O_HOP);
  g_cfg.boost = as_bool(O_BOOST);
  g_cfg.short_wipe = as_bool(O_WIPE);
  g_cfg.gl_selftest = as_bool(O_GLTEST);
  g_cfg.profile = as_bool(O_PROFILE);
  g_cfg.boot_log = as_bool(O_BOOTLOG);
  g_cfg.log_sounds = as_bool(O_SOUNDS);
  g_cfg.sound_priority = as_bool(O_SNDPRIO);
  g_cfg.quiet_center = as_bool(O_QUIETCENTER);
  g_cfg.mix_48k = as_bool(O_MIX48);
  g_cfg.menu_controls = as_bool(O_MENUS);
  g_cfg.pretend_online = as_bool(O_ONLINE);

  const char *r = g_val[O_RES];
  int docked = appletGetOperationMode() == AppletOperationMode_Console;
  int h = !strcmp(r, "720") ? 720 : !strcmp(r, "1080") ? 1080 : !strcasecmp(r, "auto") ? (docked ? 1080 : 720) : 0;
  if (!h) {
    debugPrintf("[config] resolution = %s: not auto, 1080 or 720, using auto\n", r);
    h = docked ? 1080 : 720;
  }
  g_cfg.res_h = h;
  g_cfg.res_w = h * 16 / 9;
  dcr_window_set_size(g_cfg.res_w, g_cfg.res_h);

  debugPrintf("[config] unlocks %s, max stars %s, top bar %s, free purchases %s, hop on %s, %dx%d (%s, %s), "
              "CPU boost %s, short transitions %s, important sounds first %s, one theme-switch sound %s, 48 kHz %s, "
              "profiler %s, sound log %s\n",
              g_cfg.unlock_all ? "on" : "off", g_cfg.max_stars ? "on" : "off",
              g_cfg.hide_top_bar ? "hidden" : "shown", g_cfg.free_store ? "on" : "off",
              g_cfg.hop_on_press ? "press" : "release",
              g_cfg.res_w, g_cfg.res_h, r, docked ? "docked" : "handheld", g_cfg.boost ? "on" : "off",
              g_cfg.short_wipe ? "on" : "off", g_cfg.sound_priority ? "on" : "off",
              g_cfg.quiet_center ? "on" : "off", g_cfg.mix_48k ? "on" : "off", g_cfg.profile ? "on" : "off",
              g_cfg.log_sounds ? "on" : "off");
}

/* "section.key" for the port's C# (dcr_mod.c): a boolean as 1/0, a number as
 * itself, `dflt` for an option this build does not have. */
int dcr_config_value(const char *key, int dflt) {
  const char *dot = key ? strchr(key, '.') : NULL;
  if (!dot)
    return dflt;
  for (int i = 0; i < O_COUNT; i++) {
    if (strlen(k_opts[i].section) != (size_t)(dot - key) || strncasecmp(key, k_opts[i].section, (size_t)(dot - key)) ||
        strcasecmp(dot + 1, k_opts[i].key))
      continue;
    const char *v = g_val[i];
    if (!strcasecmp(v, "true") || !strcasecmp(v, "yes") || !strcasecmp(v, "on"))
      return 1;
    if (!strcasecmp(v, "false") || !strcasecmp(v, "no") || !strcasecmp(v, "off"))
      return 0;
    char *end;
    long n = strtol(v, &end, 10);
    return end != v ? (int)n : dflt;
  }
  return dflt;
}
