// Profiles.cs -- the Switch's user profiles on the leaderboard and in
// multiplayer, the way a console game uses them -- and never in the way of a
// player who does not want one.
//
// THE LEADERBOARD. The first time the leaderboard is opened, the Switch's own
// profile picker comes up (B plays on without a profile, and it is not asked
// again). From then on the player's runs are that profile's: its nickname and
// icon on its row, its best score per theme. The leaderboard is this Switch's
// high scores -- every profile's best, and the best played without one
// ("No Profile"); nobody made up, no weekly reset -- in three lists:
//   Overall   the best on any world;
//   By World  one world's (L/R browse the worlds);
//   Challenge the current Weekend Challenge's: runs in its world while it
//             runs, its own challenge runs included; a new challenge starts
//             the list afresh.
// X brings the picker back (another profile), Y plays on without one.
//
// MULTIPLAYER. Seats show a profile's nickname when one is picked: player 1's
// is the leaderboard's; any player presses X in the waiting room for the
// picker (profiles other seats took are greyed out). Snatch and Run results go
// on the multiplayer leaderboard under those profiles.
//
// The scores are the port's own, in <game folder>/leaderboard.txt (plain
// text; delete it to start over). The game's save (coins, figurines) stays
// one for the Switch, as before. Icons: <game folder>/profiles/<uid>.jpg.
// Config: [game] profiles.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Shakespeare;
using UnityEngine;
using UnityEngine.UI;

namespace DcrMod
{
    public static class Profiles
    {
        public static bool On;
        public const string Guest = "guest";
        public static string Active;                 // a uid, or null: no profile
        static bool asked;
        // world (Universe.Id, or -100 the multiplayer board) -> who -> best
        class Best { public int Score; public string CharId; }
        static readonly Dictionary<int, Dictionary<string, Best>> best = new Dictionary<int, Dictionary<string, Best>>();
        static readonly Dictionary<string, string> nick = new Dictionary<string, string>(); // uid -> nickname (cached)
        static readonly Dictionary<string, string> icon = new Dictionary<string, string>(); // uid -> file URL
        const int MultiplayerWorld = -100;

        static string FilePath { get { return Native.Root + "/leaderboard.txt"; } }

        public static void Install()
        {
            if (Native.Config("game.profiles", 1) == 0) return;
            On = true;
            Load();
            var M = typeof(Profiles);
            Hook.Install(typeof(GameController), "ButtonLeaderboard", null, M, "ButtonLeaderboard", "Orig_ButtonLeaderboard");
            Hook.Install(typeof(LeaderboardController), "submitScore", new[] { typeof(string), typeof(string), typeof(int) }, M, "SubmitScore", "Orig_SubmitScore");
            Hook.Install(typeof(GameController), "EventGameOver", null, M, "EventGameOver", "Orig_EventGameOver");
            Loader.OnAfterInput(Frame);
        }

        // ------------------------------------------------------------ who
        public static string Who { get { return Active ?? Guest; } }

        public static string Nickname(string uid)
        {
            if (string.IsNullOrEmpty(uid) || uid == Guest) return null;
            string n;
            if (nick.TryGetValue(uid, out n) && !string.IsNullOrEmpty(n)) return n;
            n = Native.ProfileName(uid);
            if (!string.IsNullOrEmpty(n)) { nick[uid] = n; Save(); }
            return string.IsNullOrEmpty(n) ? (nick.TryGetValue(uid, out n) ? n : null) : n;
        }

        // the icon as a URL the game's WWW loads ("" = the default avatar)
        public static string IconUrl(string uid)
        {
            if (string.IsNullOrEmpty(uid) || uid == Guest) return "";
            string u;
            if (icon.TryGetValue(uid, out u)) return u;
            string path = Native.ProfileIcon(uid);
            if (string.IsNullOrEmpty(path)) { icon[uid] = ""; return ""; }
            int colon = path.IndexOf(':');
            if (colon > 0 && colon < 8) path = path.Substring(colon + 1); // sdmc:/... -> /...
            u = "file://" + path;
            icon[uid] = u;
            return u;
        }

        // the name a player without a profile goes by: the figurine's
        public static string CharacterName(string charId)
        {
            if (string.IsNullOrEmpty(charId)) return "Player";
            string n = Language.Get(charId);
            return string.IsNullOrEmpty(n) || n == charId ? "Player" : n;
        }

        public static string DisplayName(string who, string charId)
        {
            return Nickname(who) ?? CharacterName(charId);
        }

        // The system's profile picker: a uid, or null when the player backed out
        // (or there is no picker). `taken`: uids greyed out.
        public static string Pick(IEnumerable<string> taken)
        {
            var sb = new StringBuilder();
            if (taken != null)
                foreach (var t in taken)
                    if (!string.IsNullOrEmpty(t) && t != Guest) sb.Append(sb.Length > 0 ? "," : "").Append(t);
            string uid = Native.ProfilePick(sb.ToString());
            if (string.IsNullOrEmpty(uid) || uid == "-") return null;
            nick.Remove(uid); icon.Remove(uid); // fresh: the player may have changed them
            Nickname(uid);
            return uid;
        }

        static void SetActive(string uid)
        {
            Active = uid;
            asked = true;
            Save();
            Log.Line("profiles: playing as " + (uid == null ? "no profile" : Nickname(uid) ?? uid));
        }

        // ------------------------------------------------------------ scores
        public static void Record(int world, string who, string charId, int score)
        {
            if (score <= 0 || string.IsNullOrEmpty(who)) return;
            Dictionary<string, Best> w;
            if (!best.TryGetValue(world, out w)) best[world] = w = new Dictionary<string, Best>();
            Best b;
            if (w.TryGetValue(who, out b) && b.Score >= score) return;
            w[who] = new Best { Score = score, CharId = charId ?? "" };
            Save();
        }

        public static int BestOf(int world, string who, out string charId)
        {
            charId = "";
            Dictionary<string, Best> w;
            Best b;
            if (!best.TryGetValue(world, out w) || !w.TryGetValue(who, out b)) return 0;
            charId = b.CharId;
            return b.Score;
        }

        public struct Row { public string Who, Name, CharId, Avatar; public int Score, World; }

        public static string RowName(string who, string charId) { return Nickname(who) ?? (who == Guest ? "No Profile" : CharacterName(charId)); }

        // everyone on this Switch who has a score on this board, best first
        public static List<Row> Board(int world)
        {
            var l = new List<Row>();
            Dictionary<string, Best> w;
            if (!best.TryGetValue(world, out w)) return l;
            foreach (var kv in w)
                l.Add(new Row { Who = kv.Key, Name = RowName(kv.Key, kv.Value.CharId), CharId = kv.Value.CharId, Avatar = IconUrl(kv.Key), Score = kv.Value.Score, World = world });
            l.Sort((a, b) => b.Score.CompareTo(a.Score));
            return l;
        }

        // the leaderboard's lists
        public static List<Row> Scores(int world) { return Board(world); }

        public static List<Row> Overall()
        {
            var top = new Dictionary<string, Row>();
            foreach (var w in best)
            {
                if (w.Key <= 0) continue; // the multiplayer and challenge boards are not worlds
                foreach (var kv in w.Value)
                {
                    Row r;
                    if (top.TryGetValue(kv.Key, out r) && r.Score >= kv.Value.Score) continue;
                    top[kv.Key] = new Row { Who = kv.Key, Name = RowName(kv.Key, kv.Value.CharId), CharId = kv.Value.CharId, Avatar = IconUrl(kv.Key), Score = kv.Value.Score, World = w.Key };
                }
            }
            var l = new List<Row>(top.Values);
            l.Sort((a, b) => b.Score.CompareTo(a.Score));
            return l;
        }

        public static Row OverallOf(string who)
        {
            foreach (var r in Overall()) if (r.Who == who) return r;
            return new Row();
        }

        // ------------------------------------------------------------ the Weekend Challenge
        const int ChallengeBoard = -200;
        static string challengeId;           // the challenge the board's scores are from

        // the challenge running now (the title's Weekend Challenge), or null
        public static EventManager.Event LiveChallenge()
        {
            var em = EventManager.Instance;
            var ev = em != null ? em.GetCurrentEvent() : null;
            if (ev == null || ev.Universe == null) return null;
            var now = CloudTime.utcTimeOnline().ToLocalTime();
            return now >= ev.StartTime && now < ev.EndTime ? ev : null;
        }

        public static List<Row> ChallengeScores()
        {
            var ev = LiveChallenge();
            if (ev == null) return new List<Row>();
            ChallengeIs(ev);
            return Board(ChallengeBoard);
        }

        // a new challenge: its board starts empty
        static void ChallengeIs(EventManager.Event ev)
        {
            if (ev.Id == challengeId) return;
            challengeId = ev.Id;
            best.Remove(ChallengeBoard);
            Save();
        }

        // a finished run: its world's board, and the challenge's when it is the challenge's world
        static void RecordRun(int world, string charId, int score, bool challengeRun)
        {
            if (!challengeRun) Record(world, Who, charId, score);
            var ev = LiveChallenge();
            if (ev == null || ev.UniverseId != world) return;
            ChallengeIs(ev);
            Record(ChallengeBoard, Who, charId, score);
        }

        // the theme the By World list shows: the current character's, or one browsed to with L/R
        static int browseWorld;
        public static int BoardWorld(int requested) { return browseWorld > 0 && requested > 0 ? browseWorld : requested; }

        public static void RecordMultiplayer(string who, string charId, int points) { Record(MultiplayerWorld, who, charId, points); }
        public static bool IsMultiplayerWorld(string worldId) { return worldId == GlobalController.CROWN_WORLD_ID.ToString(); }
        public static int WorldKey(string worldId)
        {
            if (IsMultiplayerWorld(worldId)) return MultiplayerWorld;
            int w;
            return int.TryParse(worldId, NumberStyles.Integer, CultureInfo.InvariantCulture, out w) ? w : 0;
        }

        // ------------------------------------------------------------ the file
        static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                int version = 1;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split('\t');
                    if (p[0] == "version" && p.Length > 1) int.TryParse(p[1], out version);
                    else if (p[0] == "active" && p.Length > 1) Active = p[1] == "none" || p[1].Length != 32 ? null : p[1];
                    else if (p[0] == "asked") asked = true;
                    else if (p[0] == "challenge" && p.Length > 1) challengeId = p[1];
                    else if (p[0] == "name" && p.Length > 2) nick[p[1]] = p[2];
                    else if (p[0] == "score" && p.Length > 4)
                    {
                        int world, score;
                        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out world) ||
                            !int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out score)) continue;
                        Dictionary<string, Best> w;
                        if (!best.TryGetValue(world, out w)) best[world] = w = new Dictionary<string, Best>();
                        w[p[2]] = new Best { Score = score, CharId = p[3] };
                    }
                }
                // version 1 files may hold made-up "no profile" scores: the leaderboard
                // screen copied the rows it showed back into the save (the save's best
                // shown as the player's), so its guest scores are dropped once
                if (version < 2)
                {
                    int dropped = 0;
                    foreach (var w in best.Values) if (w.Remove(Guest)) dropped++;
                    Log.Line("profiles: leaderboard.txt from before version 2: " + dropped + " no-profile score(s) dropped");
                    Save();
                }
                Log.Line("profiles: " + (Active == null ? "no profile" : "profile " + (Nickname(Active) ?? Active)) + ", scores for " + best.Count + " board(s)");
            }
            catch (Exception e) { Log.Line("profiles: leaderboard.txt: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("# Disney Crossy Road for Switch -- the leaderboard: the best score of each\n");
                sb.Append("# Switch profile (and of play without one) on each board. Delete to start over.\n");
                sb.Append("version\t2\n");
                sb.Append("active\t").Append(Active ?? "none").Append('\n');
                if (!string.IsNullOrEmpty(challengeId)) sb.Append("challenge\t").Append(challengeId).Append('\n');
                if (asked) sb.Append("asked\n");
                foreach (var kv in nick) sb.Append("name\t").Append(kv.Key).Append('\t').Append(kv.Value.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
                foreach (var w in best)
                    foreach (var kv in w.Value)
                        sb.Append("score\t").Append(w.Key.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(kv.Key).Append('\t')
                          .Append(kv.Value.CharId).Append('\t').Append(kv.Value.Score.ToString(CultureInfo.InvariantCulture)).Append('\n');
                string tmp = FilePath + ".part";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e) { Log.Line("profiles: leaderboard.txt not saved: " + e.Message); }
        }

        // ------------------------------------------------------------ hooks
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ButtonLeaderboard(GameController self) { throw new InvalidOperationException("hook stub"); }
        static void ButtonLeaderboard(GameController self)
        {
            // the first time: whose leaderboard is it? (B: nobody's, and not asked again)
            if (!asked)
            {
                try { SetActive(Pick(null)); }
                catch (Exception e) { Loader.Report(e); asked = true; }
            }
            Orig_ButtonLeaderboard(self);
        }

        // every run's score: the save's per-character best (the game's), and the
        // player's best on the world's board (and the challenge's, in its world).
        // Only the game's own end of a run: the leaderboard screen's rows carry no
        // player id of the game's, so its copy-back of "your" row never comes here.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_SubmitScore(LeaderboardController self, string universeName, string charId, int score) { throw new InvalidOperationException("hook stub"); }
        static void SubmitScore(LeaderboardController self, string universeName, string charId, int score)
        {
            Orig_SubmitScore(self, universeName, charId, score);
            try
            {
                if (LocalMP.InMatch || GoGameManager.Instance().GameMode == GameMode.MULTIPLAYER) return; // results record those
                var um = UniverseManager.Instance;
                if (um == null || um.universeList == null) return;
                var u = Array.Find(um.universeList, x => x != null && x.name == universeName);
                if (u != null) RecordRun(u.Id, charId, score, false);
            }
            catch (Exception e) { Loader.Report(e); }
        }

        // a Weekend Challenge run's end: the game keeps these off its scores; the
        // challenge's board has them
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_EventGameOver(GameController self) { throw new InvalidOperationException("hook stub"); }
        static void EventGameOver(GameController self)
        {
            try
            {
                var c = GlobalController.instance != null ? GlobalController.instance.currentCharacter : null;
                if (c != null && c.universe != null) RecordRun(c.universe.Id, c.id, self.score, true);
            }
            catch (Exception e) { Loader.Report(e); }
            Orig_EventGameOver(self);
        }

        public static int OwnBest(int world, out string charId) { return BestOf(world, Who, out charId); }
        public static string OwnName(string charId) { return Nickname(Active) ?? CharacterName(charId); }

        // ------------------------------------------------------------ the leaderboard screen
        // The game's weekly leaderboard, as this Switch's high scores: its three
        // tabs are Overall (Top 100), By World (Friends) and Challenge; the line
        // under "High Scores" names what the list is of (all worlds, the world, or
        // the challenge's world and the time it has left). No pinned "you" row
        // (the list has everyone, the player too), no rewards, no daily counters;
        // the lists are plain tables (no challenge buttons); ties share a place;
        // each row's bar the character (and on Overall its world). At the top
        // left: who is playing, the profile pill (X, Y) and, on By World, L R.
        static LeaderboardScreen screen;
        static Ui.Layer boardLayer;
        static RectTransform hintBar, emptyNote;
        static string barFor;
        static bool boardUp;
        static object lastRows;
        static int lastContext = -1;

        static void Frame()
        {
            if (GameController.instance == null) return;
            var top = MenuPad.TopGroup();
            bool up = top != null && top.name == "Leaderboard";
            if (!up)
            {
                if (boardUp)
                {
                    boardUp = false;
                    browseWorld = 0; // the next visit starts at the current character's world
                    lastRows = null;
                    if (boardLayer != null) boardLayer.Show(false);
                }
                return;
            }
            if (screen == null)
                foreach (var p in top.panels)
                    if (p != null && (screen = p.GetComponent<LeaderboardScreen>()) != null) break;
            var ls = screen;
            if (ls == null || !ls.isActiveAndEnabled) return;
            boardUp = true;
            try
            {
                Style(ls);
                var rows = ls._listPlayersDisplay;
                if (rows != null && (!ReferenceEquals(rows, lastRows) || ls.context != lastContext))
                {
                    lastRows = rows;
                    lastContext = ls.context;
                    Rows(ls);
                }
                Furniture(ls);
            }
            catch (Exception e) { Loader.Report(e); }

            var pad = Pads.P1;
            string was = Active;
            int world = browseWorld;
            if (pad.Pressed(Btn.X))
            {
                string uid = Pick(Active != null ? new[] { Active } : null);
                if (uid != null) SetActive(uid);
            }
            else if (pad.Pressed(Btn.Y) && Active != null) SetActive(null);
            else if (ls.context == 1 && pad.Pressed(Btn.L | Btn.ZL | Btn.R | Btn.ZR)) StepWorld(pad.Pressed(Btn.L | Btn.ZL) ? -1 : 1);
            if (was != Active || world != browseWorld)
            {
                try { ls.getDataTop100Users(); } // the lists again
                catch (Exception e) { Loader.Report(e); }
            }
        }

        static int CurrentWorld()
        {
            var c = GlobalController.instance != null ? GlobalController.instance.currentCharacter : null;
            int cur = c != null && c.universe != null ? c.universe.Id : 0;
            return BoardWorld(cur);
        }

        static void StepWorld(int step)
        {
            var um = UniverseManager.Instance;
            if (um == null) return;
            var l = um.GetVisibleUniverses().FindAll(u => u != null && u.Id > 0);
            if (l.Count == 0) return;
            int i = l.FindIndex(u => u.Id == CurrentWorld());
            i = ((i < 0 ? 0 : i + step) % l.Count + l.Count) % l.Count;
            browseWorld = l[i].Id;
            Native.Rumble(Pads.P1.Slot, 0.25f, 40);
        }

        public static string WorldName(int id)
        {
            var um = UniverseManager.Instance;
            if (um == null || um.universeList == null) return "";
            var u = Array.Find(um.universeList, x => x != null && x.Id == id);
            if (u == null) return "";
            string n = Language.Get(u.NameLocalisationId);
            return string.IsNullOrEmpty(n) || n == u.NameLocalisationId ? u.name : n;
        }

        static void Style(LeaderboardScreen ls)
        {
            var c = ls.transform.Find("Leaderboard Contents");
            if (c == null) return;
            Off(c, "Header/Reward");
            Off(c, "VerticalLayout/HeaderInfo");
            Off(c, "VerticalLayout/Me");
            SetText(c.Find("VerticalLayout/Title/GameObject/TimeList"), "High Scores");
            TabName(c.Find("VerticalLayout/Buttons/Top100"), "Overall");
            TabName(c.Find("VerticalLayout/Buttons/Friend"), "By World");
            TabName(c.Find("VerticalLayout/Buttons/Challenge"), "Challenge");
            // all three lists spaced as the Overall one: the other two left room
            // under each row for its challenge button (hidden here: a table)
            var list = ls.pivotPlayer != null ? ls.pivotPlayer.GetComponent<VerticalLayoutGroup>() : null;
            if (list != null && (list.spacing != 180f || list.padding.bottom != 80 || list.padding.left != 0))
            {
                list.spacing = 180f;
                list.padding = new RectOffset(0, 0, 80, 80);
            }
            // the line under the title: what this list is of
            var line = c.Find("VerticalLayout/Title/GameObject/EndInTime");
            if (line == null) return;
            // one line of text (the game's clock and countdown beside it did not fit a long name)
            var content = line.Find("Content");
            Off(line, "Image");
            Off(line, "Text");
            var ev = LiveChallenge();
            string what;
            if (ls.context == 0) what = "All Worlds";
            else if (ls.context == 1) what = WorldName(CurrentWorld());
            else if (ev == null) what = "No challenge right now";
            else
            {
                var t = ev.EndTime - CloudTime.utcTimeOnline().ToLocalTime();
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                what = WorldName(ev.UniverseId) + "  \u00b7  " + (t.Days > 0 ? t.Days + "d " + t.Hours + "h" : t.Hours + "h " + t.Minutes + "m") + " left";
            }
            SetText(content, what);
            var ct = content != null ? content.GetComponent<Text>() : null;
            if (ct != null && ct.horizontalOverflow != HorizontalWrapMode.Overflow) ct.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        static void TabName(Transform tab, string s)
        {
            if (tab == null) return;
            SetText(tab.Find("ButtonBG/Text"), s);
            SetText(tab.Find("ShadowHighlight/ButtonLabel"), s);
        }

        static void Off(Transform root, string path)
        {
            var t = root.Find(path);
            if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
        }

        static void SetText(Transform t, string s)
        {
            var tx = t != null ? t.GetComponent<Text>() : null;
            if (tx == null || tx.text == s) return;
            var lt = t.GetComponent<LocalizedText>();
            if (lt != null) lt.enabled = false;
            tx.text = s;
        }

        static void Rows(LeaderboardScreen ls)
        {
            var disp = ls._listPlayersDisplay;
            var info = ls._listPlayers;
            if (disp == null || info == null) return;
            for (int i = 0; i < disp.Count && i < info.Count; i++)
            {
                var d = disp[i];
                var p = info[i];
                if (d == null || p == null) continue;
                int rank = 1;
                foreach (var o in info) if (o.point > p.point) rank++;
                if (d.rank != null) d.rank.text = ls.convertRankText(rank);
                try { ls.changeColorBasedRank(d, rank - 1); } catch (Exception) { }
                if (d.worldName != null)
                {
                    string ch = CharacterName(p.characterId);
                    if (ls.context == 0 && p.userID != null && p.userID.StartsWith("g-"))
                    {
                        int w = OverallOf(p.userID.Substring(2)).World;
                        string wn = w > 0 ? WorldName(w) : "";
                        d.worldName.text = wn.Length > 0 ? ch + "  ·  " + wn : ch;
                    }
                    else d.worldName.text = ch;
                    // a long name shrinks to its bar rather than being cut
                    if (!d.worldName.resizeTextForBestFit)
                    {
                        d.worldName.resizeTextMaxSize = d.worldName.fontSize;
                        d.worldName.resizeTextMinSize = Math.Max(10, d.worldName.fontSize * 3 / 5);
                        d.worldName.horizontalOverflow = HorizontalWrapMode.Wrap;
                        d.worldName.verticalOverflow = VerticalWrapMode.Truncate;
                        d.worldName.resizeTextForBestFit = true;
                    }
                }
                if (d.challengeButtonTransform != null) d.challengeButtonTransform.gameObject.SetActive(false); // a table, not challenges
                if (string.IsNullOrEmpty(p.avatarURL) && d.avatar != null) d.avatar.texture = Ui.Solid(new Color(0.62f, 0.7f, 0.82f)); // no profile
            }
        }

        // One bar at the bottom (the top has the title and its second line, which
        // can be long): who is playing, the profile buttons and, on By World, L R.
        // A word where a list is empty.
        static void Furniture(LeaderboardScreen ls)
        {
            if (boardLayer == null) boardLayer = new Ui.Layer("Leaderboard");
            bool byWorld = ls.context == 1;
            string want = (Active ?? "") + (byWorld ? "|w" : "");
            if (barFor != want || hintBar == null)
            {
                if (hintBar != null) UnityEngine.Object.Destroy(hintBar.gameObject);
                var l = new List<string>();
                l.Add(""); l.Add(Active != null ? "Playing as " + (Nickname(Active) ?? "a profile") : "No profile");
                if (Active == null) { l.Add("X"); l.Add("Pick Profile"); }
                else { l.Add("X"); l.Add("Change Profile"); l.Add("Y"); l.Add("No Profile"); }
                if (byWorld) { l.Add("L R"); l.Add("Change World"); }
                hintBar = Ui.Hints(boardLayer.Rt, 26f, l.ToArray());
                Ui.Place(hintBar, new Vector2(0.5f, 0f), new Vector2(0f, 14f));
                barFor = want;
            }
            var info = ls._listPlayers;
            bool empty = info == null || info.Count == 0;
            if (emptyNote == null)
            {
                var t = Ui.Label(boardLayer.Rt, "", Ui.Bold, 30, Color.white);
                t.alignment = TextAnchor.MiddleCenter;
                Ui.Outlined(t, 2f, 0f);
                emptyNote = t.rectTransform;
                emptyNote.sizeDelta = new Vector2(900f, 120f);
                Ui.Place(emptyNote, new Vector2(0.5f, 0.5f), new Vector2(0f, -60f));
            }
            var note = emptyNote.GetComponent<Text>();
            note.text = !empty ? ""
                : ls.context == 0 ? "No high scores yet."
                : ls.context == 1 ? "No high scores on this world yet."
                : LiveChallenge() == null ? "No Weekend Challenge is running right now."
                : "No scores in this challenge yet.\nRuns in its world count while it runs.";
            boardLayer.Show(true);
            MakeRoom(ls);
        }

        // The list ends above the bar: the screen's layout runs its list to the
        // bottom edge, and the bar floated over its last rows. Its bottom padding
        // grows to the bar's top (measured on screen: the two canvases differ).
        static int padBase = -1;
        static readonly Vector3[] corners = new Vector3[4];
        static void MakeRoom(LeaderboardScreen ls)
        {
            var vl = ls.transform.Find("Leaderboard Contents/VerticalLayout") as RectTransform;
            var g = vl != null ? vl.GetComponent<VerticalLayoutGroup>() : null;
            if (g == null || hintBar == null) return;
            if (padBase < 0) padBase = g.padding.bottom;
            hintBar.GetWorldCorners(corners);
            float barTop = corners[1].y; // an overlay canvas: world units are screen pixels
            var cv = vl.GetComponentInParent<Canvas>();
            Camera cam = cv != null && cv.renderMode != RenderMode.ScreenSpaceOverlay ? cv.worldCamera : null;
            vl.GetWorldCorners(corners);
            float bottom = RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y;
            float top = RectTransformUtility.WorldToScreenPoint(cam, corners[1]).y;
            if (vl.rect.height <= 0f || top <= bottom) return;
            float perUnit = (top - bottom) / vl.rect.height;
            int want = Mathf.Max(padBase, Mathf.CeilToInt((barTop + 8f * Screen.height / 720f - bottom) / perUnit));
            if (g.padding.bottom == want) return;
            g.padding.bottom = want;
            LayoutRebuilder.MarkLayoutForRebuild(vl);
        }
    }
}
