/* dcr_config.h -- the user's settings, from <game folder>/config.ini (dcr_config.c).
 * Free of libnx so dcr_ilpatch.c keeps building on the host for its test. */
#ifndef DCR_USER_CONFIG_H
#define DCR_USER_CONFIG_H

typedef struct {
  int unlock_all;   /* [game] unlock_all_characters */
  int max_stars;    /* [game] max_character_stars */
  int hide_top_bar; /* [game] hide_top_bar */
  int hop_on_press; /* [controls] hop_on_press */
  int res_w, res_h; /* [display] resolution: 1080 / 720 / auto */
  int boost;        /* [performance] boost_cpu_when_loading */
  int short_wipe;   /* [performance] shorter_transitions */
  int gl_selftest;  /* [debug] gl_selftest */
  int profile;      /* [debug] profile_long_frames */
  int boot_log;     /* [debug] boot_log_on_screen */
  int free_store;   /* [game] free_purchases */
  int log_sounds;   /* [debug] log_sounds */
  int sound_priority; /* [audio] important_sounds_first */
  int quiet_center;   /* [audio] one_theme_switch_sound */
  int mix_48k;        /* [audio] mix_at_48khz */
  int menu_controls;  /* [controls] menu_controls */
  int pretend_online; /* [online] pretend_online */
} DcrConfig;

/* Read config.ini (writing it with the defaults, or adding missing options,
 * first). Early in main(); the defaults hold until then. */
void dcr_config_load(void);
const DcrConfig *dcr_config(void);

#endif
