// TestDriver.cs -- a test aid for runs without hands (an emulator): with a
// file named test_mp in the game folder, the title screen opens a local
// multiplayer room by itself (coin mode; the file's first word picks another),
// waits 25 s for the test script to seat players (`<t> press a p2`), and
// starts the match (with "hold" in the file: when the script presses down for
// player 2, if player 1's Play has not started it by then). test_chars walks the
// character select (DuckTales, then Sorcerer's Apprentice Mickey) and plays
// him; test_sorcerer starts a run as him straight from the title. Absent
// files (every normal launch): nothing happens.
using System.IO;
using goDuel;
using Shakespeare;
using UnityEngine;

namespace DcrMod
{
    // the title menu's listener would open its screens; the test drives the room itself
    class Quiet : NetworkListener
    {
        public void onWillConnect() { }
        public void onConnected(bool result) { Log.Line("test: connected " + result); }
        public void onMessageSent(bool result) { }
    }

    public static class TestDriver
    {
        static int step;
        static float t0, titleSince;
        static string lastTop;

        public static void Install()
        {
            // test_char: the game starts with this character current (its first mesh preload is that character's)
            if (File.Exists(Native.Root + "/test_char"))
            {
                string id = "";
                try { id = File.ReadAllText(Native.Root + "/test_char").Trim(); } catch (System.Exception) { }
                if (id.Length > 0) { PlayerPrefs.SetString("CurrentCharacter", id); Log.Line("test: starting as character " + id); }
            }
            if (File.Exists(Native.Root + "/test_profile")) Loader.OnFrame(ProfileTest);
            if (File.Exists(Native.Root + "/test_board")) { Loader.OnFrame(BoardTest); return; }
            if (File.Exists(Native.Root + "/test_tasks")) { Loader.OnFrame(TasksTest); return; }
            if (File.Exists(Native.Root + "/test_modes")) { Loader.OnFrame(ModesTest); return; }
            if (File.Exists(Native.Root + "/test_chars"))
            {
                Log.Line("test: character select driver on (test_chars)");
                Loader.OnFrame(Chars);
                return;
            }
            if (File.Exists(Native.Root + "/test_sorcerer"))
            {
                Log.Line("test: Sorcerer run driver on (test_sorcerer)");
                Loader.OnFrame(SorcererRun);
                return;
            }
            if (!File.Exists(Native.Root + "/test_mp")) return;
            Log.Line("test: local multiplayer driver on (test_mp)");
            Loader.OnFrame(Frame);
        }

        // test_board: the title's leaderboard button, once the title has settled
        static float settleSince;
        static bool boardOpened;
        // the title menu has been up for 4 s
        static bool TitleSettled()
        {
            if (GameController.instance == null || Time.realtimeSinceStartup < 40f) return false;
            var top = MenuPad.TopGroup();
            string tn = top != null ? top.name.ToLowerInvariant() : "";
            if (tn != "start of game") { settleSince = 0f; return false; }
            if (settleSince == 0f) { settleSince = Time.realtimeSinceStartup; return false; }
            return Time.realtimeSinceStartup - settleSince >= 4f;
        }

        // test_tasks: the title's daily missions, turned to their prize info (test_script.txt presses B)
        static int tasksStep;
        static float tasksAt;
        static void TasksTest()
        {
            if (tasksStep == 0)
            {
                if (!TitleSettled()) return;
                var go = GameObject.Find("Daily Missions Button");
                var b = go != null ? go.GetComponent<UnityEngine.UI.Selectable>() : null;
                Log.Line("test: daily missions " + (b != null ? "opened" : "(no button)"));
                if (b != null) MenuPad.Press(b);
                tasksStep = 1; tasksAt = Time.realtimeSinceStartup;
            }
            else if (tasksStep == 1 && Time.realtimeSinceStartup - tasksAt > 5f)
            {
                var dm = Object.FindObjectOfType<DailyMissionsPanel>();
                Log.Line("test: the prize info " + (dm != null && dm.InfoButton != null ? "shown" : "(no panel)"));
                if (dm != null && dm.InfoButton != null) MenuPad.Press(dm.InfoButton);
                tasksStep = 2; tasksAt = Time.realtimeSinceStartup;
            }
            else if (tasksStep == 2 && Time.realtimeSinceStartup - tasksAt > 3f)
            {
                var dm = Object.FindObjectOfType<DailyMissionsPanel>();
                Log.Line("test: prize info side " + (dm != null ? dm.showingSide2.ToString() : "-"));
                tasksStep = 3; tasksAt = Time.realtimeSinceStartup;
            }
            else if (tasksStep == 3 && Time.realtimeSinceStartup - tasksAt > 1f)
            {
                var dm = Object.FindObjectOfType<DailyMissionsPanel>();
                var top = MenuPad.TopGroup();
                Log.Line("test: then side2 " + (dm != null ? dm.showingSide2.ToString() : "-") + ", top " + (top != null ? top.name : "-"));
                tasksAt = Time.realtimeSinceStartup + 4f;
            }
        }

        static void BoardTest()
        {
            var gc = GameController.instance;
            if (boardOpened || !TitleSettled()) return;
            boardOpened = true;
            GlobalController.instance.hasPlayedBefore = true;
            Log.Line("test: the leaderboard");
            gc.ButtonLeaderboard();
        }

        // test_sorcerer: the first launch leaves the first run's tutorial behind and
        // selects him (saved; the tutorial always plays Mickey); the next one starts a
        // run as him from the title (test_script.txt hops)
        static string runId;
        static void SorcererRun()
        {
            var gc = GameController.instance;
            var g = GlobalController.instance;
            if (gc == null || g == null || Time.realtimeSinceStartup < 40f) return;
            if (runId == null)
            {
                // the figurine: test_sorcerer's first word, or the Sorcerer
                try { runId = File.ReadAllText(Native.Root + "/test_sorcerer").Trim(); } catch (System.Exception) { }
                if (string.IsNullOrEmpty(runId)) runId = Sorcerer.Id;
            }
            float t = Time.realtimeSinceStartup;
            switch (step)
            {
                case 0:
                    if (!g.hasPlayedBefore)
                    {
                        g.hasPlayedBefore = true;
                        GameStateController.save();
                        g.SelectCharacter(runId, false);
                        Log.Line("test: tutorial done, " + runId + " selected and saved -- launch again");
                        step = 9;
                        return;
                    }
                    if (g.currentCharacter == null || g.currentCharacter.id != runId)
                    {
                        g.SelectCharacter(runId, true); // a new round with it (its world)
                        Log.Line("test: " + runId + " selected, round restarting");
                        step = 1; t0 = t + 8f;
                        return;
                    }
                    step = 1; t0 = t;
                    break;
                case 1: // the Sorcerer: spells at the title (the obstacles either side of him); others: a run
                    if (t - t0 < 3f) return;
                    if (runId == Sorcerer.Id) Sorcerer.TestMode = true;
                    else if (!gc.isGameStarted) gc.StartGameRound();
                    Log.Line("test: " + (runId == Sorcerer.Id ? "spells at the title" : "a run") + " as " + g.currentCharacter.id);
                    step = 2;
                    break;
            }
        }

        // test_chars: the character select, DuckTales, then Mickey & Friends at 809
        static void Chars()
        {
            var gc = GameController.instance;
            if (gc == null || Time.realtimeSinceStartup < 40f) return;
            if (step == 0 && !TitleSettled()) return;
            float t = Time.realtimeSinceStartup;
            switch (step)
            {
                case 0:
                    Log.Line("test: opening the character select");
                    gc.ButtonCharacterSelect();
                    step = 1; t0 = t;
                    // "pad" in the file: only that; test_script.txt does the rest
                    try { if (File.ReadAllText(Native.Root + "/test_chars").Contains("pad")) step = 99; } catch (System.Exception) { }
                    break;
                case 1:
                    if (t - t0 < 6f) return;
                    foreach (var u in Object.FindObjectsOfType<CharacterSelectionUniverseScrollHorizontal>())
                        if (u.gameObject.activeInHierarchy) { u.SelectUniverse(12, true); Log.Line("test: DuckTales"); }
                    step = 2; t0 = t;
                    break;
                case 2:
                    if (t - t0 < 10f) return;
                    foreach (var u in Object.FindObjectsOfType<CharacterSelectionUniverseScrollHorizontal>())
                        if (u.gameObject.activeInHierarchy) { u.SelectUniverse(2, true); Log.Line("test: Mickey & Friends"); }
                    step = 3; t0 = t;
                    break;
                case 3:
                    if (t - t0 < 4f) return;
                    var row = gc.characterMeshes != null ? gc.characterMeshes.GetCurrentRow() : null;
                    if (row != null) { row.ScrollToCharacter("809", true); Log.Line("test: 809"); }
                    step = 4; t0 = t;
                    break;
                case 4: // a run as him (test_script.txt hops)
                    if (t - t0 < 4f) return;
                    GlobalController.instance.hasPlayedBefore = true; // past the first run's tutorial (it plays Mickey)
                    var csb = Object.FindObjectOfType<CharacterSelectionButtonController>();
                    if (csb != null && csb.PlayButton != null && csb.PlayButton.gameObject.activeInHierarchy)
                    {
                        MenuPad.Press(csb.PlayButton);
                        Log.Line("test: playing " + (GlobalController.instance != null && GlobalController.instance.currentCharacter != null ? GlobalController.instance.currentCharacter.id : "?"));
                    }
                    step = 5;
                    break;
            }
        }

        // test_profile: the profile picker at the title, the pick's name and icon, and a save of leaderboard.txt
        static bool profileDone;
        static void ProfileTest()
        {
            if (profileDone || GameController.instance == null || Time.realtimeSinceStartup < 45f) return;
            profileDone = true;
            string uid = Profiles.Pick(null);
            Log.Line("test: profile " + (uid ?? "(none)") + ", name " + Profiles.Nickname(uid) + ", icon " + Profiles.IconUrl(uid) +
                     ", all: " + Native.ProfileList());
            Profiles.Record(-99, uid ?? Profiles.Guest, "802", 1);
            Log.Line("test: leaderboard.txt " + (File.Exists(Native.Root + "/leaderboard.txt") ? "written" : "MISSING"));
        }

        // test_modes: the title's multiplayer button, for the mode screen's layout (needs a save past the tutorial)
        static int modesStep;
        static void ModesTest()
        {
            if (GameController.instance == null || Time.realtimeSinceStartup < 45f) return;
            if (modesStep == 0)
            {
                modesStep = 1;
                GlobalController.instance.hasPlayedBefore = true;
                var menu = UnityEngine.Object.FindObjectOfType<StartOfGameMenu>();
                if (menu != null && menu.buttonMultiplayer != null) menu.buttonMultiplayer.onClick.Invoke();
                Log.Line("test: the multiplayer button" + (menu == null ? " (no start menu)" : ""));
            }
        }

        // test_mp's first word: coins (the default), crown, lastone, hop
        static string testWord;
        static string TestWord()
        {
            if (testWord != null) return testWord;
            testWord = "";
            try { testWord = File.ReadAllText(Native.Root + "/test_mp").Trim().ToLowerInvariant(); } catch (System.Exception) { }
            return testWord;
        }

        static MultiplayerMode TestMode()
        {
            string w = TestWord();
            if (w.StartsWith("crown")) return MultiplayerMode.THE_CROWN;
            if (w.StartsWith("last")) return MultiplayerMode.LAST_MAN_STANDING;
            if (w.StartsWith("hop")) return MultiplayerMode.FURTHEST_CROSSER;
            return MultiplayerMode.COIN_COLLECTION;
        }

        static void Frame()
        {
            var gc = GameController.instance;
            var mc = MultiplayerController.Instance();
            switch (step)
            {
                case 0: // the title, settled (its start-up screens done: they would replace the room)
                    if (gc == null || gc.isGameStarted || Time.realtimeSinceStartup < 40f) return;
                    var top = MenuPad.TopGroup();
                    string tn = top != null ? top.name : "(none)";
                    if (tn != lastTop) { lastTop = tn; Log.Line("test: top group " + tn); }
                    if (tn.ToLowerInvariant() != "start of game" && tn != "StartOfGameTV") { titleSince = 0f; return; }
                    if (titleSince == 0f) titleSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - titleSince < 4f) return;
                    var net = SingletonMonobehaviour<MultiplayerNetwork>.Instance;
                    if (net != null) { net.EnableMutiplayerNetwork(true); net.EnableLobbyManager(true); }
                    if (NetworkManager.Instance() == null || MultiplayerUIController.Instance() == null) return;
                    GlobalController.instance.hasPlayedBefore = true; // no first-run tutorial in the reloads
                    Log.Line("test: connecting");
                    MultiplayerUIController.Instance().connect(new Quiet());
                    step = 1; t0 = Time.realtimeSinceStartup;
                    break;
                case 1: // connected: create a coin-mode room
                    if (Time.realtimeSinceStartup - t0 < 3f) return;
                    GoGameManager.instance.MultiMode = TestMode();
                    if (mc != null) mc.gameSelection = MultiGameSelection.CreateRoom;
                    var lm = SingletonMonobehaviour<LobbyManagerNew>.Instance;
                    if (lm == null) return;
                    Log.Line("test: creating a room at " + Time.realtimeSinceStartup.ToString("F1") + " s");
                    lm.CreateMatchRequest();
                    step = 2; t0 = Time.realtimeSinceStartup;
                    break;
                case 2: // in the room: players join, then start (with "hold" in test_mp: the script starts it)
                    if (mc == null || mc.gameStatus != MultiGameStatus.LOBBY) return;
                    if (TestWord().Contains("hold")) { if (!Pads.All[1].Pressed(Btn.Down)) return; } // the script's `press down p2`
                    else if (Time.realtimeSinceStartup - t0 < 25f) return;
                    Log.Line("test: starting with " + mc.NetPlayers.Count + " player(s)");
                    MessageHelper.SendStartMatchRequest(mc.CurMatchId);
                    step = 3;
                    break;
            }
        }
    }
}
