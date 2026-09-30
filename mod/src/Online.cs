// Online.cs -- the game believes it is online and logged in, and a server of
// its own answers it (no network on this port).
//
// * Online: CheckOnline.UpdateOnlineState is only ever passed true (the one
//   place the "offline" flag changes). internetReachability stays
//   NotReachable, so the game's DNS/ping check returns at once every round.
//   With it: no "reconnect to the internet" popup, and the prize machine,
//   daily missions, weekend challenges, the ticket machine, free gifts and the
//   coin/pixel/ticket drops in runs (all gated on it) come back.
// * Server: every request to webprod.disneycrossyroad.gogame.net goes through
//   CloudController.sendRequest; the hook works out the reply here (Route)
//   and queues it for that URL in the port's WWW (jni_www.c), then lets the
//   game send it as usual. Replies follow the rules the game's parsers need:
//   always HTTP 200 JSON, never an error (an error freezes every request for
//   30 s), never code 1010/1026/1027 (1026 would wipe the save), no "links",
//   no character lists (they overwrite the save's figurines).
// * Tickets are the server's in this game (never saved on the device): the
//   balance is kept in the game's own save (key dcrOffTickets) and spent and
//   granted here as the server did; ticket packs from the (free) store are
//   credited.
// * Cloud time is the Switch's clock (CloudTime), so events, daily missions,
//   the ticket machine and lives work.
// * Weekend challenges: the game's 101 events all ended in 2018; one of them
//   is moved onto the current week, a different one each week.
// * Facebook is "logged in" (IL patch of SNManager.IsLoggedIn in
//   dcr_ilpatch.c) without its SDK: a local profile, no invites, no logout --
//   so the leaderboards (answered here) and the daily login reward work, and
//   no Facebook login can hang the game.
// * Rewarded ads: always available and "watched" at once, with their reward.
// Config: [online] pretend_online.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Shakespeare;
using UnityEngine;

namespace DcrMod
{
    public static class Online
    {
        public static bool On;
        const string Host = "https://webprod.disneycrossyroad.gogame.net";

        public static void Install()
        {
            if (Native.Config("online.pretend_online", 1) == 0)
            {
                Log.Line("online: off in config.ini (the game stays offline)");
                return;
            }
            On = true;
            var M = typeof(Online);
            Hook.Install(typeof(CheckOnline), "UpdateOnlineState", new[] { typeof(bool) }, M, "UpdateOnlineState", "Orig_UpdateOnlineState");
            Hook.Install(typeof(CloudController), "sendRequest", new[] { typeof(CloudRequest) }, M, "SendRequest", "Orig_SendRequest");
            Hook.Install(typeof(CloudTime), "UpdateNetworkTimeOffset", null, M, "UpdateNetworkTimeOffset", null);
            Hook.Install(typeof(CloudTime), "bootstrapDidComplete", new[] { typeof(Action) }, M, "TimeBootstrapDidComplete", "Orig_TimeBootstrapDidComplete");
            // Facebook, without its SDK
            Hook.Install(typeof(SNManager), "GetCurrentFacebookUser", null, M, "GetCurrentFacebookUser", null);
            Hook.Install(typeof(SNManager), "Logout", null, M, "Nothing_SN", null);
            Hook.Install(typeof(SNManager), "InviteFriends", null, M, "Nothing_SN", null);
            Hook.Install(typeof(SNManager), "RequestListFriends", null, M, "Nothing_SN", null);
            Hook.Install(typeof(GameController), "GetUserMe", new[] { typeof(Action<SNManager.PlayerInfo>) }, M, "GetUserMe", null);
            Hook.Install(typeof(NewOptions), "panelWillMoveIn", null, M, "OptionsWillMoveIn", "Orig_OptionsWillMoveIn");
            Hook.Install(typeof(NewOptions), "LogoutFacebook", null, M, "Nothing_Options", null);
            Hook.Install(typeof(InviteFriendBanner), "CanShowBanner", null, M, "False_Invite", null);
            // sharing opens the phone's share sheet (nothing here): the Switch has its capture button
            Hook.Install(typeof(GameController), "ButtonShare", null, M, "Nothing_Game", null);
            Hook.Install(typeof(GameController), "ButtonShareChallengeEnd", null, M, "Nothing_Game", null);
            Hook.Install(typeof(GameController), "ShareCharacter", new[] { typeof(string) }, M, "Nothing_GameStr", null);
            Hook.Install(typeof(GameController), "ShareNewCharacter", new[] { typeof(FigurineUnlockData) }, M, "Nothing_GameFig", null);
            // ads
            Hook.Install(typeof(AdManager), "CanPlayVideoAd", null, M, "CanPlayVideoAd", null);
            Hook.Install(typeof(AdManager), "PlayVideoAd", new[] { typeof(AdManager.AdShowLocation), typeof(Action<bool>) }, M, "PlayVideoAd", null);
            Hook.Install(typeof(AdManager), "PlayPregameInterstitialAd", null, M, "Nothing_Ad", null);
            Hook.Install(typeof(AdManager), "PlayVideoInterstitialAd", null, M, "Nothing_Ad", null);
            // weekend challenges, Christmas, ticket packs
            Hook.Install(typeof(EventManager), "CheckCurrentAndNextEvent", null, M, "CheckCurrentAndNextEvent", "Orig_CheckCurrentAndNextEvent");
            Hook.Install(typeof(SeasonalCheckController), "IsCurrentlyWithinSeason", new[] { typeof(SpecialSeason) }, M, "IsCurrentlyWithinSeason", null);
            Hook.Install(typeof(SeasonalCheckController), "IsExpired", new[] { typeof(SpecialSeason) }, M, "IsSeasonExpired", null);
            Hook.Install(typeof(BuyTicketsContainer), "BuyTicketsSuccess", new[] { typeof(string) }, M, "BuyTicketsSuccess", "Orig_BuyTicketsSuccess");
            Loader.OnFrame(TicketBank);
        }

        // ------------------------------------------------------------ online
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_UpdateOnlineState(CheckOnline self, bool online) { throw new InvalidOperationException("hook stub"); }
        static void UpdateOnlineState(CheckOnline self, bool online) { Orig_UpdateOnlineState(self, true); }

        // ------------------------------------------------------------ server
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Orig_SendRequest(CloudController self, CloudRequest r) { throw new InvalidOperationException("hook stub"); }

        static int served;
        static bool SendRequest(CloudController self, CloudRequest r)
        {
            if (r == null || r.url == null || !r.url.StartsWith(Host, StringComparison.Ordinal))
                return Orig_SendRequest(self, r);
            self.setWaitForCloud(0f); // never "the server looks unhealthy"
            // logged in already if a login token is saved: no round trip
            if (r.requiresAuthentication && string.IsNullOrEmpty(self.accessToken) && !string.IsNullOrEmpty(self.loginToken))
            {
                self.accessToken = self.loginToken;
                self.tokenAddedTime = DateTime.UtcNow.AddHours(1);
            }
            bool willSend = !r.requiresAuthentication ||
                (!string.IsNullOrEmpty(self.accessToken) && (self.tokenAddedTime - DateTime.UtcNow).TotalSeconds >= 30);
            if (!willSend)
                return Orig_SendRequest(self, r); // the game logs in first; this request stays queued
            string path = r.url.Substring(Host.Length);
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            string body;
            try { body = Route(self, path, RequestJson(r)); }
            catch (Exception e)
            {
                Loader.Report(e);
                body = NotFound;
            }
            if (served++ < 60)
                Log.Line("server: " + path + " -> " + (body.Length > 160 ? body.Substring(0, 160) + "..." : body));
            Native.QueueWww(r.url, body);
            return Orig_SendRequest(self, r);
        }

        static string RequestJson(CloudRequest r)
        {
            byte[] b = r.payload != null ? r.payload() : null;
            string s = b == null ? "" : Encoding.UTF8.GetString(b);
            return s.StartsWith("data=", StringComparison.Ordinal) ? WWW.UnEscapeURL(s.Substring(5)) : s;
        }

        const string NotFound = "{\"status\":404,\"code\":404}";
        static string Ok(string data) { return data == null ? "{\"status\":200,\"code\":200}" : "{\"status\":200,\"code\":200,\"data\":" + data + "}"; }
        static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
        static string Num(long n) { return n.ToString(CultureInfo.InvariantCulture); }
        static string TimeText(DateTime utc) { return utc.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture); }
        static long Ms(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        static Dictionary<string, object> Decode(string json)
        {
            try { return GenericsJSONParser.JsonDecode(json) as Dictionary<string, object>; }
            catch (Exception) { return null; }
        }
        static string Str(Dictionary<string, object> d, string k)
        {
            object o;
            return d != null && d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "";
        }

        static string PlayerId()
        {
            string id = GameStateController.playerStringForKey("playerId", "");
            if (string.IsNullOrEmpty(id)) id = GameStateController.playerStringForKey("dcrOffPlayerId", "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString();
                GameStateController.setPlayerStringForKey("dcrOffPlayerId", id);
            }
            return id;
        }
        static string Token() { return "switch-" + PlayerId(); }

        static string Route(CloudController cc, string path, string json)
        {
            int bal = cc.tickets;
            switch (path)
            {
                case "/api/common/register":
                    return Ok("{\"accessToken\":" + Q(Token()) + ",\"refreshToken\":" + Q(Token()) + ",\"playerId\":" + Q(PlayerId()) + ",\"ticket\":" + Num(RestoreTickets()) + "}");
                case "/api/games/users/token/check":
                    return Ok("{\"playerId\":" + Q(PlayerId()) + ",\"ticket\":" + Num(RestoreTickets()) + "}");
                case "/api/common/token/guest/refresh":
                case "/api/common/token/facebook/refresh":
                    return Ok("{\"accessToken\":" + Q(string.IsNullOrEmpty(cc.loginToken) ? Token() : cc.loginToken) + ",\"refreshToken\":" + Q(Token()) + "}");
                case "/api/games/startup":
                    return Startup(json);
                case "/api/common/time/":
                    return Ok("{\"time\":" + Q(TimeText(DateTime.UtcNow)) + "}");
                case "/api/common/versions/":
                    return Ok(Versions);
                case "/api/common/maintenance":
                    return "{\"status\":200,\"code\":200,\"data\":[]}";
                case "/api/games/characters/read":
                case "/api/games/characters/upsertNew":
                    return Ok("{\"characters\":[]}");
                case "/api/games/currency/claim":
                    return Ok("{\"currencies\":[]}");
                case "/api/games/noah/inbox":
                case "/api/games/gopay/inbox":
                case "/api/games/notification/config":
                case "/api/games/redeem":
                case "/api/games/leaderboard/claim/":
                    return Ok("{\"list\":[]}");
                case "/api/games/products/purchase":
                    {
                        string id = Str(Decode(json), "productID");
                        int cost;
                        if (!int.TryParse(id.Substring(id.LastIndexOf('_') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out cost))
                            cost = 0;
                        if (bal < cost)
                            return "{\"status\":200,\"code\":210}";
                        return Ok("{\"productCost\":" + Num(cost) + ",\"ticket\":" + Num(bal - cost) + "}");
                    }
                case "/api/games/rewards/missions":
                    {
                        bool hard = Str(Decode(json), "difficulty").IndexOf("Hard", StringComparison.OrdinalIgnoreCase) >= 0;
                        int n = hard ? rng.Next(4, 9) : rng.Next(2, 6);
                        return Ok("{\"reward\":" + Num(n) + ",\"ticket\":" + Num(bal + n) + "}");
                    }
                case "/api/games/tickets/free":
                    return Ok("{\"ticketsRedeemed\":10,\"ticket\":" + Num(bal + 10) + "}");
                case "/api/games/daily/reward/list":
                    return DailyList();
                case "/api/games/daily/reward/claim":
                    return DailyClaim(json);
                case "/api/games/leaderboard/":
                    return Leaderboard(json);
                case "/api/games/challenge2/list":
                    return Challenges(json, false);
                case "/api/games/challenge2/friend/list":
                case "/api/games/challenge/friend/list":
                    return Challenges(json, true);
                case "/api/games/challenge2/reward/claim":
                    return Ok("{}");
                case "/api/games/characters/tickets/claim":
                case "/api/games/currency/upsert":
                case "/api/games/leaderboard/score/":
                case "/api/games/challenge2/end":
                case "/api/common/network/tracking":
                case "/api/games/player/push/info":
                case "/api/games/tickets":
                    return Ok(null);
            }
            return NotFound; // announcements (/api/common/message/<lang>) and everything else
        }

        const string Versions = "{\"supported\":true,\"url\":\"\",\"configVersion\":0,\"configUrl\":\"\",\"redemptionOn\":false}";
        static readonly System.Random rng = new System.Random();

        // the batched boot call: one reply per name the request lists
        static string Startup(string json)
        {
            var sb = new StringBuilder("{\"status\":200,\"code\":200,\"data\":{\"list\":[");
            var req = Decode(json);
            object lo;
            var list = req != null && req.TryGetValue("list", out lo) ? lo as List<object> : null;
            bool first = true;
            if (list != null)
                foreach (object o in list)
                {
                    string name = Str(o as Dictionary<string, object>, "name");
                    if (name.Length == 0) continue;
                    string item;
                    if (name.StartsWith("/api/common/versions")) item = "{\"status\":200,\"code\":200,\"data\":[" + Versions + "]}";
                    else if (name.StartsWith("/api/common/social/facebook/login")) item = "{\"status\":200,\"code\":200,\"data\":[{\"reward\":0}]}";
                    else if (name.StartsWith("/api/common/time")) item = "{\"status\":200,\"code\":200,\"data\":[{\"time\":" + Q(TimeText(DateTime.UtcNow)) + "}]}";
                    else if (name.StartsWith("/api/games/characters/read")) item = "{\"status\":200,\"code\":200,\"data\":[{\"characters\":[]}]}";
                    else if (name.StartsWith("/api/games/currency/claim")) item = "{\"status\":200,\"code\":200,\"data\":[{\"currencies\":[]}]}";
                    else if (name.StartsWith("/api/games/noah/inbox") || name.StartsWith("/api/games/gopay/inbox")) item = "{\"status\":200,\"code\":200,\"data\":[{\"list\":[]}]}";
                    else if (name.StartsWith("/api/games/player/push/info")) item = "{\"status\":200,\"code\":200}";
                    else item = NotFound; // update/force, maintenance, message/<lang>
                    sb.Append(first ? "" : ",").Append("{\"name\":").Append(Q(name)).Append(",\"data\":").Append(item).Append('}');
                    first = false;
                }
            return sb.Append("]}}").ToString();
        }

        // ------------------------------------------------------------ tickets
        static bool ticketsRestored;
        static int ticketsSaved = int.MinValue;
        static int RestoreTickets()
        {
            ticketsRestored = true;
            return Math.Max(0, GameStateController.playerIntForKey("dcrOffTickets", 0));
        }

        static void TicketBank()
        {
            if (!ticketsRestored || Loader.FrameCount % 30 != 0) return;
            var cc = SingletonMonobehaviour<CloudController>.Instance;
            if (cc == null) return;
            int t = cc.tickets;
            if (t != ticketsSaved && t >= 0)
            {
                ticketsSaved = t;
                GameStateController.setPlayerIntForKey("dcrOffTickets", t);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_BuyTicketsSuccess(BuyTicketsContainer self, string itemId) { throw new InvalidOperationException("hook stub"); }
        static void BuyTicketsSuccess(BuyTicketsContainer self, string itemId)
        {
            Orig_BuyTicketsSuccess(self, itemId);
            var cc = SingletonMonobehaviour<CloudController>.Instance;
            if (cc != null) cc.tickets += self.ticketCount; // the real server credited packs from the receipt
        }

        // ------------------------------------------------------------ daily login
        static string DailyList()
        {
            int cur = GameStateController.playerIntForKey("dcrOffDailyId", 0);
            int next = cur == 14 ? 8 : cur + 1;
            int b = next <= 7 ? 1 : 8;
            int[] type = { 1, 2, 1, 3, 2, 1, 3 };
            int[] amt = { 50, 30, 100, 5, 60, 200, 15 };
            var sb = new StringBuilder("{\"currentId\":" + Num(cur) + ",\"rewards\":[");
            for (int i = 0; i < 7; i++)
                sb.Append(i > 0 ? "," : "").Append("{\"rewardId\":" + Num(b + i) + ",\"giftType\":" + Num(type[i]) + ",\"reward\":" + Num(amt[i] * (b == 8 ? 2 : 1)) + ",\"isDouble\":true}");
            return Ok(sb.Append("]}").ToString());
        }

        static string DailyClaim(string json)
        {
            int cur = GameStateController.playerIntForKey("dcrOffDailyId", 0);
            int next = cur == 14 ? 8 : cur + 1;
            GameStateController.setPlayerIntForKey("dcrOffDailyId", next);
            int[] amt = { 50, 30, 100, 5, 60, 200, 15 };
            int n = amt[(next - 1) % 7] * (next >= 8 ? 2 : 1);
            bool normal = Str(Decode(json), "isNormal") != "0";
            return Ok("{\"rewardId\":" + Num(next) + ",\"reward\":" + Q(Num(n)) + ",\"isDouble\":" + (normal ? "false" : "true") + "}");
        }

        // ------------------------------------------------------------ leaderboards
        // The leaderboard: this Switch's own scores only -- every profile's best,
        // and the best played without one; no made-up players, no week. Its three
        // lists (Profiles.cs): Overall here (the game's Top 100), By World and
        // Challenge below. Each list's ids are its own ("g-", "w-", "c-"): the
        // screen ranks a list by the Top 100's ranks of the same ids, and copies a
        // row with the game's own player id back into the save as a new run.
        static string Leaderboard(string json)
        {
            string worldId = Str(Decode(json), "worldId");
            if (LocalMP.On && Profiles.IsMultiplayerWorld(worldId)) return SessionBoard(false);
            var sb = new StringBuilder("{\"ownScore\":0,\"endAt\":0,\"list\":[");
            bool first = true;
            foreach (var r in Profiles.Overall())
            {
                sb.Append(first ? "" : ",").Append(Entry("g-" + r.Who, r.Name, r.Avatar, KnownChar(r.CharId), r.Score, 0));
                first = false;
            }
            sb.Append("],\"rankInfo\":[]}");
            return Ok(sb.ToString());
        }

        // a character the screen can draw (a score kept from a character since removed: the current one)
        static string KnownChar(string charId)
        {
            var cm = CharacterManager.Instance;
            if (!string.IsNullOrEmpty(charId) && cm != null && cm.GetCharacterByIdOrNull(charId, false) != null) return charId;
            var c = GlobalController.instance != null ? GlobalController.instance.currentCharacter : null;
            return c != null ? c.id : "802";
        }

        static string Entry(string id, string name, string avatar, string charId, int score, int time)
        {
            return "{\"playerId\":" + Q(id) + ",\"playerName\":" + Q(name) + ",\"avatarUrl\":" + Q(avatar ?? "") + ",\"time\":" + Num(time)
                + ",\"charId\":" + Q(charId) + ",\"score\":" + Num(score) + "}";
        }

        // Challenges: friends = the other profiles on this Switch (their best is the
        // score to beat), or a rival while there are none; the daily random one = a rival
        // the multiplayer board: this session's players and their wins (LocalMP.cs
        // styles it; "seat-N" ids tell it whose row is whose, none is "mine")
        static string SessionBoard(bool asChallenges)
        {
            var rows = new List<string>();
            foreach (var r in LocalMP.Standings())
            {
                string id = "seat-" + (r.Seat + 1);
                string avatar = Profiles.On ? Profiles.IconUrl(r.Uid) : "";
                string cid = string.IsNullOrEmpty(r.CharId) ? "802" : r.CharId;
                rows.Add(asChallenges ? Challenge(id, r.Name, avatar, cid, r.Wins) : Entry(id, r.Name, avatar, cid, r.Wins, r.Wins + r.Losses));
            }
            if (asChallenges) rows.Reverse(); // read back to front, as in Challenges
            string sb = string.Join(",", rows.ToArray());
            DateTime monday = DateTime.UtcNow.Date.AddDays(7 - (((int)DateTime.UtcNow.DayOfWeek + 6) % 7));
            if (asChallenges)
                return Ok("{\"remainTime\":" + Num(Ms(DateTime.UtcNow.Date.AddDays(1))) + ",\"totalTimePlayToday\":0,\"challengeList\":[" + sb + "]}");
            return Ok("{\"ownScore\":0,\"endAt\":" + Num(Ms(monday)) + ",\"list\":[" + sb + "],\"rankInfo\":[]}");
        }

        // By World (the game's Friends list): one world's bests (L/R on the screen
        // browse the worlds). Challenge (its random challenges): the current Weekend
        // Challenge's bests -- the same whatever world is browsed.
        static string Challenges(string json, bool friends)
        {
            string worldId = Str(Decode(json), "worldId");
            if (LocalMP.On && Profiles.IsMultiplayerWorld(worldId)) return SessionBoard(true);
            DateTime tomorrow = DateTime.UtcNow.Date.AddDays(1);
            var rows = friends ? Profiles.Scores(Profiles.BoardWorld(Profiles.WorldKey(worldId))) : Profiles.ChallengeScores();
            var list = new List<string>();
            foreach (var r in rows)
                list.Add(Challenge((friends ? "w-" : "c-") + r.Who, r.Name, r.Avatar, KnownChar(r.CharId), r.Score));
            // the screen reads a "challengeList" back to front (LeaderboardChallengeRandomListResponse
            // starts from its last entry): the best goes last to be shown first
            list.Reverse();
            return Ok("{\"remainTime\":" + Num(Ms(tomorrow)) + ",\"totalTimePlayToday\":0,\"challengeList\":[" + string.Join(",", list.ToArray()) + "]}");
        }

        static string Challenge(string id, string name, string avatar, string charId, int score)
        {
            return "{\"playerId\":" + Q(id) + ",\"name\":" + Q(name) + ",\"avatar\":" + Q(avatar ?? "") + ",\"characterId\":" + Q(charId)
                + ",\"score\":" + Num(score) + ",\"status\":\"challenge\",\"rewardScore\":0,\"rewardType\":\"c\",\"percentScore\":0}";
        }

        // ------------------------------------------------------------ cloud time
        static void UpdateNetworkTimeOffset(CloudTime self) { self.checkTimeDidSucceed(DateTime.UtcNow); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_TimeBootstrapDidComplete(CloudTime self, Action completion) { throw new InvalidOperationException("hook stub"); }
        static void TimeBootstrapDidComplete(CloudTime self, Action completion)
        {
            Orig_TimeBootstrapDidComplete(self, completion);
            PlayerPrefs.SetInt("ntpTimeOffset", 0);
            self.checkCloudTime();
        }

        // ------------------------------------------------------------ Facebook
        static SNManager.PlayerInfo fakeUser;
        static SNManager.PlayerInfo GetCurrentFacebookUser(SNManager self)
        {
            if (fakeUser == null)
                fakeUser = new SNManager.PlayerInfo { name = "Player", avatar = "" };
            if (Profiles.On)
            {
                var ch = GlobalController.instance != null ? GlobalController.instance.currentCharacter : null;
                fakeUser.name = Profiles.OwnName(ch != null ? ch.id : null); // the profile's nickname, or the figurine's name
                fakeUser.avatar = Profiles.IconUrl(Profiles.Active);
            }
            var cc = SingletonMonobehaviour<CloudController>.Instance;
            fakeUser.playerId = cc != null ? cc.PlayerID : PlayerId();
            if (GlobalController.instance != null)
            {
                fakeUser.coin = GlobalController.instance.coins;
                fakeUser.pixel = GlobalController.instance.pixels;
            }
            fakeUser.ticket = cc != null ? cc.tickets : 0;
            return fakeUser;
        }
        static void Nothing_SN(SNManager self) { }
        static void GetUserMe(GameController self, Action<SNManager.PlayerInfo> cb) { if (cb != null) cb(GetCurrentFacebookUser(null)); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_OptionsWillMoveIn(NewOptions self) { throw new InvalidOperationException("hook stub"); }
        static void OptionsWillMoveIn(NewOptions self)
        {
            Orig_OptionsWillMoveIn(self);
            if (self.inviteFriendButton != null) self.inviteFriendButton.gameObject.SetActive(false);
            if (self.buttonAchievements != null) self.buttonAchievements.gameObject.SetActive(false); // Google Play Games
        }
        static void Nothing_Options(NewOptions self) { }
        static bool False_Invite(InviteFriendBanner self) { return false; }
        static void Nothing_Game(GameController self) { }
        static void Nothing_GameStr(GameController self, string s) { }
        static void Nothing_GameFig(GameController self, FigurineUnlockData d) { }

        // ------------------------------------------------------------ ads
        static bool CanPlayVideoAd(AdManager self) { return true; }
        static void Nothing_Ad(AdManager self) { }

        static void PlayVideoAd(AdManager self, AdManager.AdShowLocation where, Action<bool> callback)
        {
            self.isReadyForEndOfGameAd = false;
            var ns = NotificationServer.instance;
            switch (where)
            {
                case AdManager.AdShowLocation.EventHub: ns.postNotification("MissionAdWatched"); break;
                case AdManager.AdShowLocation.ContinueNormalRun: ns.postNotification("AdToReviveWatched"); break;
                case AdManager.AdShowLocation.PrizeMachine: ns.postNotification("WatchedAdsToRoll"); break;
                case AdManager.AdShowLocation.EndOfGame:
                case AdManager.AdShowLocation.EndOfGamePixels:
                    {
                        bool coins = where == AdManager.AdShowLocation.EndOfGame;
                        ns.postNotification("AdWatched");
                        if (GlobalController.instance != null)
                            GlobalController.instance.AdWatched(coins ? self.FreeCoinReward : self.FreePixelReward, coins);
                        break;
                    }
            }
            if (callback != null) callback(true);
            var am = SingletonMonobehaviour<AudioManager>.Instance;
            if (am != null) am.SetMasterVolumeLevel(1f);
        }

        // ------------------------------------------------------------ weekend challenges
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_CheckCurrentAndNextEvent(EventManager self) { throw new InvalidOperationException("hook stub"); }
        static readonly Dictionary<EventManager.Event, KeyValuePair<DateTime, DateTime>> eventDates = new Dictionary<EventManager.Event, KeyValuePair<DateTime, DateTime>>();
        static int lastWeek = int.MinValue;

        static void CheckCurrentAndNextEvent(EventManager self)
        {
            try
            {
                if (CloudTime.cloudTimeIsReady() && self.events != null && self.events.Count > 0)
                {
                    DateTime now = CloudTime.utcTimeOnline().ToLocalTime();
                    DateTime monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
                    int week = (int)Math.Floor((monday - new DateTime(2018, 9, 3)).TotalDays / 7.0);
                    if (week != lastWeek)
                    {
                        lastWeek = week;
                        foreach (var e in self.events)
                            if (!eventDates.ContainsKey(e)) eventDates[e] = new KeyValuePair<DateTime, DateTime>(e.StartTime, e.EndTime);
                        foreach (var kv in eventDates) { kv.Key.StartTime = kv.Value.Key; kv.Key.EndTime = kv.Value.Value; }
                        var usable = new List<EventManager.Event>();
                        foreach (var e in self.events)
                        {
                            if (e.Universe == null || e.UnlockCharacterIds == null) continue;
                            bool ok = true;
                            foreach (string id in e.UnlockCharacterIds)
                                if (CharacterManager.Instance.GetCharacterByIdOrNull(id, false) == null) { ok = false; break; }
                            if (ok) usable.Add(e);
                        }
                        usable.Sort((a, b) => eventDates[a].Key.CompareTo(eventDates[b].Key));
                        if (usable.Count > 0)
                        {
                            int n = usable.Count;
                            var cur = usable[((week % n) + n) % n];
                            var nxt = usable[(((week + 1) % n) + n) % n];
                            cur.StartTime = monday; cur.EndTime = monday.AddDays(7);
                            if (nxt != cur) { nxt.StartTime = monday.AddDays(7); nxt.EndTime = monday.AddDays(14); }
                            Log.Line("events: this week's weekend challenge is event " + cur.Id + " (" + usable.Count + " in rotation)");
                        }
                    }
                }
            }
            catch (Exception e) { Loader.Report(e); }
            Orig_CheckCurrentAndNextEvent(self);
        }

        // Christmas every year: December and the first days of January
        static bool IsCurrentlyWithinSeason(SeasonalCheckController self, SpecialSeason season)
        {
            if (!CloudTime.cloudTimeIsReady()) return false;
            DateTime n = CloudTime.utcTimeOnline().ToLocalTime();
            return n.Month == 12 || (n.Month == 1 && n.Day < 12);
        }
        static bool IsSeasonExpired(SeasonalCheckController self, SpecialSeason season)
        {
            return CloudTime.cloudTimeIsReady() && !IsCurrentlyWithinSeason(self, season);
        }
    }
}
