// LocalMP.cs -- the game's online multiplayer, played by up to four people on
// one Switch.
//
// Disney Crossy Road: SEA has a 4-player online mode (coin collection and The
// Crown) against a match server (TCP + protobuf, goDuel.NetworkManager). Its
// design is already one shared screen: every client scrolls one camera over
// one world made from the server's random list, and each client runs its own
// player while the others are puppets moved by server messages. Here:
// * THE SERVER is in the game: NetworkManager.Connect / sendMessage /
//   CloseConnection are replaced; requests are answered here and the replies
//   go into the manager's own message list, as if they came off the socket.
// * EVERY SEAT IS A LOCAL PLAYER: all players are real PlayerControllers
//   (their own collisions, deaths, coins), each fed by its own controller.
//   The game assumes one local player in a few places; around each player's
//   frame, kill and coin handling, the "current player" is switched to that
//   player (MultiplayerController.CurNetPlayer, GameController.playerController),
//   and the physics pass tests every player, with a trigger owned by the
//   player who entered it.
// * ONE CAMERA for the group: it keeps the game's own slow scroll and is pulled
//   along by the player in front; whoever falls 3.5 rows behind is caught
//   ("too slow", as online) and comes back 5 s later near the others. Nobody
//   can hop off the top of the screen. Players can push each other.
// * THE LOBBY is the game's own waiting room, opened straight from a mode's
//   button (on one Switch there is nobody to quick-play or share a room code
//   with). The others press A on their controllers to join -- the player list
//   shows the empty seats and how to take one -- and B to leave; anyone's Y
//   opens the game's own character select on their controller (only Play in
//   it, and their number, big), X the Switch's profile picker (Profiles.cs).
//   Player 1 picks the world with L/R -- its theme card from the character
//   select sits under the world's name on the sign -- adds controllers with +
//   (the Switch's controller screen, which also comes up first: one sideways
//   Joy-Con a player works) and presses Play. The legend and cards are Ui.cs.
// * FOUR MODES: the game's Coin Crazy and Snatch and Run (the Crown: its
//   button waited for a server answer that came before the screen existed),
//   and two the game named but never made, built on Coin Crazy's match. Both
//   are knock-outs: no coins, no timer, one life each; the match ends when one
//   player is left (the score is seconds survived, the timer's place says how
//   many are left). LAST ONE STANDING -- the road speeds up and up; HOP
//   NON-STOP -- the road runs with whoever is in front, so whoever falls
//   behind drops off the bottom of the screen and is out.
// * + pauses a match (the timer stops); B then ends it (the results).
// * The Crown costs nothing; coins collected in a match are added to the save,
//   Crown places give tickets.
// * THE SESSION: every match with two or more players is a win for the top
//   score(s) and a loss for the rest, per player number, until the game
//   closes. The multiplayer leaderboard (the mode screen's podium, and after
//   each match's Next) shows those wins, each row with its player's number.
// Config: [multiplayer] local_multiplayer.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using goDuel;
using Shakespeare;
using proto;
using UnityEngine;

namespace DcrMod
{
    public static class LocalMP
    {
        public static bool On;
        static bool session;          // connected to the local server
        const int Seats = 4;
        const int CountdownMs = 7000; // scene load + the game's 5 s of pre-spawned traffic
        static int ClassicMs = 120000, CrownMs = 120000;
        const float LeadRows = 3.5f, CatchUp = 3.0f, TopRows = 7f;

        class Seat
        {
            public bool In;
            public string CharId;
            public string Uid;        // a Switch profile, or null
            public int Slot;          // controller slot
        }
        enum Rules { Coins, Crown, LastOne, HopNonStop }
        static Rules rules = Rules.Coins;
        static readonly Seat[] seats = new Seat[Seats];
        static LocalMP() { for (int i = 0; i < Seats; i++) seats[i] = new Seat { Slot = i }; }

        // the results are up over the match's scene: menus for the controller
        public static bool ResultsUp
        {
            get
            {
                var mc = session ? MultiplayerController.Instance() : null;
                return mc != null && mc.gameStatus == MultiGameStatus.END_GAME;
            }
        }

        public static bool InMatch
        {
            get
            {
                if (!session) return false;
                var mc = MultiplayerController.Instance();
                return mc != null && (mc.gameStatus == MultiGameStatus.PLAYING || mc.gameStatus == MultiGameStatus.COUNT_DOWN || mc.gameStatus == MultiGameStatus.END_GAME);
            }
        }

        public static void Install()
        {
            if (Native.Config("multiplayer.local_multiplayer", 1) == 0) return;
            On = true;
            int secs = Native.Config("multiplayer.match_seconds", 120);
            if (secs < 30) secs = 30;
            if (secs > 900) secs = 900;
            ClassicMs = CrownMs = secs * 1000;
            var M = typeof(LocalMP);
            Hook.Install(typeof(NetworkManager), "Connect", new[] { typeof(NetworkListener), typeof(bool) }, M, "Connect", null);
            Hook.Install(typeof(NetworkManager), "sendMessage", new[] { typeof(WrapMessage), typeof(bool), typeof(int) }, M, "SendMessage", null);
            Hook.Install(typeof(NetworkManager), "CloseConnection", null, M, "CloseConnection", null);
            Hook.Install(typeof(GameController), "IsMultiplayerAvailable", null, M, "IsMultiplayerAvailable", null);
            Hook.Install(typeof(StartOfGameMenu), "panelWillMoveIn", null, M, "StartMenuWillMoveIn", "Orig_StartMenuWillMoveIn");
            Hook.Install(typeof(BaseGameMode), "createPlayerAt", new[] { typeof(Vector3[]) }, M, "CreatePlayers", "Orig_CreatePlayers");
            Hook.Install(typeof(PlayerController), "Update", null, M, "PlayerUpdate", "Orig_PlayerUpdate");
            Hook.Install(typeof(PlayerController), "KillMultiplayer", new[] { typeof(DeathType), typeof(GameObject) }, M, "KillMultiplayer", "Orig_KillMultiplayer");
            Hook.Install(typeof(PlayerController), "checkPushOtherPlayer", new[] { typeof(object) }, M, "CheckPushOtherPlayer", "Orig_CheckPushOtherPlayer");
            Hook.Install(typeof(PlayerController), "UpdateCameraMultiplayer", null, M, "UpdateCameraMultiplayer", "Orig_UpdateCameraMultiplayer");
            Hook.Install(typeof(CollectCoinMode), "UnpackDieResponse", new[] { typeof(DieResponse) }, M, "UnpackDieResponse", "Orig_UnpackDieResponse");
            Hook.Install(typeof(CrossyPhysics), "Update", null, M, "PhysicsUpdate", "Orig_PhysicsUpdate");
            Hook.Install(typeof(WorldManager), "HopRemoveLane", null, M, "HopRemoveLane", "Orig_HopRemoveLane");
            Hook.Install(typeof(WorldManager), "GetAllFreeSlotLane", new[] { typeof(Vector3) }, M, "GetAllFreeSlotLane", "Orig_GetAllFreeSlotLane");
            Hook.Install(typeof(MultiPlayerHUD), "showWaitingBanner", null, M, "ShowWaitingBanner", "Orig_ShowWaitingBanner");
            Hook.Install(typeof(CrownMode), "ClaimToStartMode", null, M, "ClaimToStartMode", null);
            Hook.Install(typeof(GoGameManager), "OnApplicationPause", new[] { typeof(bool) }, M, "AppPause", "Orig_AppPause");
            Hook.Install(typeof(GoGameManager), "OnApplicationFocus", new[] { typeof(bool) }, M, "AppFocus", "Orig_AppFocus");
            Hook.Install(typeof(GameController), "TouchAction", new[] { typeof(int) }, M, "TouchAction", "Orig_TouchAction");
            Hook.Install(typeof(ScoreBoardUI), "getCharacterHeadIndex", new[] { typeof(int) }, M, "HeadIndex", "Orig_HeadIndex");
            Hook.Install(typeof(MultiResultController), "UpdatePlayerResult", null, M, "ResultClassic", "Orig_ResultClassic");
            Hook.Install(typeof(MultiResultController), "UpdatePlayerResultInTheCrownMode", null, M, "ResultCrown", "Orig_ResultCrown");
            Hook.Install(typeof(MultiplayerSelectionModeScreen), "Start", null, M, "ModeScreenStart", "Orig_ModeScreenStart");
            Hook.Install(typeof(MultiplayerSelectionModeScreen), "OnEnable", null, M, "ModeScreenEnable", "Orig_ModeScreenEnable");
            Hook.Install(typeof(MultiplayerSelectionModeScreen), "CheckToEnableTheCrown", new[] { typeof(object) }, M, "CrownCheck", "Orig_CrownCheck");
            Hook.Install(typeof(MultiPlayerSelectionScreen), "OnJoinButtonClick", null, M, "JoinRoom", null);
            Hook.Install(typeof(GlobalController), "InitialiseCharacter", null, M, "InitialiseCharacter", "Orig_InitialiseCharacter");
            Hook.Install(typeof(CollectCoinMode), "InitCoin", null, M, "InitCoin", "Orig_InitCoin");
            Hook.Install(typeof(CollectCoinMode), "OnCoinDestroy", new[] { typeof(NetCoinPickUp), typeof(bool) }, M, "OnCoinDestroy", "Orig_OnCoinDestroy");
            Hook.Install(typeof(MultiPlayerRoomScreenController), "UpdateWaitingRoomInfo", null, M, "RoomInfo", "Orig_RoomInfo");
            Hook.Install(typeof(MultiplayerGameInfoUI), "OnEnable", null, M, "GameInfo", "Orig_GameInfo");
            Hook.Install(typeof(GameController), "ButtonPause", null, M, "ButtonPause", "Orig_ButtonPause");
            Hook.Install(typeof(WorldManager), "GenerateLanesMultiPlayer", null, M, "GenerateLanes", "Orig_GenerateLanes");
            Hook.Install(typeof(GameController), "ButtonCharacterSelected", new[] { typeof(string), typeof(int), typeof(bool) }, M, "CharacterSelected", "Orig_CharacterSelected");
            Hook.Install(typeof(GameController), "ButtonCharacterClose", null, M, "CharacterClose", "Orig_CharacterClose");
            Hook.Install(typeof(MultiResultController), "OnNextButtonClick", null, M, "ResultsNext", "Orig_ResultsNext");
            Hook.Install(typeof(MultiplayerUIController), "ActiveUIByStatus", null, M, "ActiveUi", "Orig_ActiveUi");
            Hook.Install(typeof(GameController), "NormalBootFlow", null, M, "BootFlow", "Orig_BootFlow");
            Hook.Install(typeof(LeaderboardMultiplayerScreen), "GetTop100Users", null, M, "BoardShown", "Orig_BoardShown");
            Hook.Install(typeof(LeaderboardMultiplayerScreen), "CloseLeaderboard", null, M, "CloseBoard", "Orig_CloseBoard");
            Loader.OnFrame(Frame);
        }

        // ============================================================ the connection
        static void Connect(NetworkManager self, NetworkListener listener, bool v6)
        {
            // pair controllers first (the Switch's own screen; skipped in a scripted run)
            int n = Native.ControllerApplet(1, Seats);
            Log.Line("local multiplayer: " + (n < 0 ? "controller screen unavailable" : n + " controller(s)"));
            Pads.Update(0f);
            session = true;
            for (int i = 0; i < Seats; i++) seats[i].In = false;
            if (Profiles.On) seats[0].Uid = Profiles.Active; // player 1: the leaderboard's profile
            self.networkListener = listener;
            if (listener != null) listener.onWillConnect();
            self.IsConnected = true;
            // the ping clock starts now (the game starts it only after its own
            // onConnected handlers, and drops a connection whose clock reads 0)
            if (self.pingWorker != null) self.pingWorker.OnConnected();
            self.networkResult = NetworkResult.CONNECTED; // NetworkManager.Update: onConnected(true), master data
        }

        static void CloseConnection(NetworkManager self)
        {
            self.IsConnected = false;
            if (self.lockMap != null) self.lockMap.Clear();
            if (self.unlockMap != null) self.unlockMap.Clear();
            if (self.pingWorker != null) self.pingWorker.OnDisConnected();
            session = false;
            ended = false;
            if (paused) Pause(false);
            Native.SetGameSlot(-1);
            MenuPad.Suspended = false;
            if (chooser >= 0) { chooser = -1; Pads.GameOverride = null; RestoreChooserButtons(); }
            Furnish(false);
            ChooserUi(false);
            SignLines(SingletonMonobehaviour<MultiWaitingRoom3DTextInfoController>.Instance, true);
        }

        static bool IsMultiplayerAvailable(GameController self) { return true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_StartMenuWillMoveIn(StartOfGameMenu self) { throw new InvalidOperationException("hook stub"); }
        static void StartMenuWillMoveIn(StartOfGameMenu self)
        {
            Orig_StartMenuWillMoveIn(self);
            // the game showed the button only while a daily-mission figurine was left to win
            if (self.buttonMultiplayer != null && GoGameManager.Instance().GameMode != GameMode.MULTIPLAYER)
                self.buttonMultiplayer.gameObject.SetActive(true);
            // the network objects exist before the button is pressed (the connect needs them)
            var net = SingletonMonobehaviour<MultiplayerNetwork>.Instance;
            if (net != null && NetworkManager.Instance() == null) net.EnableMutiplayerNetwork(true);
        }

        // replies go where the socket's messages went: processed in NetworkManager.Update
        static void Send(int type, object body)
        {
            var nm = NetworkManager.Instance();
            if (nm == null) return;
            var w = new WrapMessage();
            w.command = type <= 1 ? 0 : 1;
            w.type = type;
            if (body != null) w.data = NetworkManager.toData(body, false);
            lock (nm.msgLocker) nm.messages.Add(w);
        }

        static long Now() { return NetworkManager.CurrentTimeMillis(); }

        // ============================================================ the server
        static string mode = "Classic";
        static bool ended;
        static long matchEnd;
        static readonly HashSet<int> liveCoins = new HashSet<int>();
        static int nextCoin = 1;
        static int crownHolder = -1;
        static readonly HashSet<int> groundItems = new HashSet<int>(); // crown item types 1, 2 on the ground
        static readonly System.Random rng = new System.Random();

        static void SendMessage(NetworkManager self, WrapMessage msg, bool lockRequest, int unlockType)
        {
            if (msg == null) return;
            try { OnRequest(msg); }
            catch (Exception e) { Loader.Report(e); }
        }

        static PlayerInfo Info(int seat)
        {
            var mc = MultiplayerController.Instance();
            var t = mc != null ? mc.GetNetPlayer(seat) : null;
            var p = new PlayerInfo();
            p.playerIdInGame = seat;
            p.playerName = SeatName(seat);
            p.characterId = seats[seat].CharId ?? "";
            p.point = t != null ? t.Score : 0;
            return p;
        }

        // A player: their profile's nickname, else their figurine's name (in the
        // game's language). The match's scoreboard and results lead with the
        // number ("P2 Sulley"): they show no colour or number of their own.
        static string BaseName(int seat)
        {
            string nk = Profiles.On ? Profiles.Nickname(seats[seat].Uid) : null;
            if (!string.IsNullOrEmpty(nk)) return nk;
            string charId = seats[seat].CharId;
            string name = string.IsNullOrEmpty(charId) ? null : Language.Get(charId);
            return string.IsNullOrEmpty(name) || name == charId ? "Player " + (seat + 1) : name;
        }

        static string SeatName(int seat)
        {
            string b = BaseName(seat);
            return b.StartsWith("Player ") ? b : "P" + (seat + 1) + " " + b;
        }

        static void OnRequest(WrapMessage msg)
        {
            switch (msg.type)
            {
                case 0: // ping
                    Send(0, null);
                    break;
                case 1: // master data: the Crown is free (ticket cap reached = no entry fee)
                    {
                        var r = new MasterDataResponse();
                        r.code = 0; r.coinMax = 100000; r.coinEarn = 0; r.ticketMax = 0; r.ticketEarn = 0;
                        Send(1, r);
                        break;
                    }
                case 1000: // quick join / join by code / "next": the local room
                    {
                        var q = NetworkManager.parseData<MatchRequest>(msg.data);
                        OpenRoom(q.characterId, q.mode);
                        break;
                    }
                case 1006: // create room
                    {
                        var q = NetworkManager.parseData<CreateMatchRequest>(msg.data);
                        OpenRoom(q.characterId, q.mode);
                        break;
                    }
                case 1004: // player 1 left the room (for the modes): the others keep their seats for the next
                    MenuPad.Suspended = false;
                    Native.SetGameSlot(-1);
                    break;
                case 1007: // start -- not from the press that picked a character (it only closed the select)
                    if (chooser >= 0 || Loader.FrameCount - chooserClosedAt < 40)
                    {
                        Log.Line("local multiplayer: start ignored (a character was being picked)");
                        break;
                    }
                    StartMatch();
                    break;
                case 1001: // move: pushes only (every player is local)
                    Move(NetworkManager.parseData<MoveRequest>(msg.data));
                    break;
                case 1002: // die
                    Die(NetworkManager.parseData<DieRequest>(msg.data));
                    break;
                case 1005: // pick a coin
                    PickCoin(NetworkManager.parseData<PickCoinRequest>(msg.data));
                    break;
                case 1008: // generate a coin
                    {
                        var q = NetworkManager.parseData<GenerateCoinRequest>(msg.data);
                        if (q.oldCoin != null && q.oldCoin.info != null) liveCoins.Remove(NetCoinPickUp.getId(q.oldCoin.info));
                        if (q.newCoin == null || q.newCoin.pos == null) break;
                        var r = new GenerateCoinResponse();
                        r.newCoin = NewCoin(q.newCoin.pos, 0);
                        Send(1009, r);
                        break;
                    }
                case 1009: // pick a crown item
                    PickCrown(NetworkManager.parseData<PickCrownRequest>(msg.data));
                    break;
                case 1010: // generate a crown item: echoed
                    {
                        var q = NetworkManager.parseData<GenerateCrownRequest>(msg.data);
                        if (q.crown == null) break;
                        if (q.crown.type == 1 || q.crown.type == 2)
                        {
                            if (groundItems.Contains(q.crown.type)) break;
                            groundItems.Add(q.crown.type);
                        }
                        var r = new GenerateCrownResponse();
                        r.crown = q.crown;
                        Send(1011, r);
                        break;
                    }
                // 1003 respawn, 1011 set position: nothing to tell anyone
            }
        }

        static void OpenRoom(string charId, string m)
        {
            mode = string.IsNullOrEmpty(m) ? "Classic" : m;
            switch (GoGameManager.instance.MultiMode)
            {
                case MultiplayerMode.THE_CROWN: rules = Rules.Crown; break;
                case MultiplayerMode.LAST_MAN_STANDING: rules = Rules.LastOne; break;
                case MultiplayerMode.FURTHEST_CROSSER: rules = Rules.HopNonStop; break;
                default: rules = Rules.Coins; break;
            }
            if (!seats[0].In && Profiles.On) seats[0].Uid = Profiles.Active; // player 1: the leaderboard's profile
            seats[0].In = true;
            seats[0].CharId = string.IsNullOrEmpty(charId) ? seats[0].CharId : charId;
            seats[0].Slot = Pads.P1.Slot;
            var r = new MatchResponse();
            r.code = 0;
            long now = Now();
            r.serverTime = now - 1;
            r.randomStep = 2 * rng.Next(1, 2000) + 1;           // odd: the whole list is visited
            for (int i = 0; i < 4096; i++) r.randomList.Add(rng.Next(0, int.MaxValue));
            r.playerIdInGame = 0;
            r.endWaitingTime = now;
            r.matchId = "1234";
            r.hostId = 0;
            r.coinMax = 100000;
            r.matchDuration = Duration();
            for (int i = 0; i < Seats; i++)
                if (seats[i].In) r.playerList.Add(Info(i));
            Send(1000, r);
            signWorld = null;
            Unfurnish();
            focusPlay = true;
            resultsLeaving = false;
            Log.Line("local multiplayer: room open (" + rules + "), " + r.playerList.Count + " seated");
        }

        static void StartMatch()
        {
            if (roomLayer != null) roomLayer.Hide();
            if (chooserLayer != null) chooserLayer.Hide();
            var mc = MultiplayerController.Instance();
            var r = new ReadyResponse();
            r.code = 0;
            r.startTime = Now() + CountdownMs;
            r.camVeloc = 0.5f;
            r.gameMode = mode;
            r.matchDuration = Duration();
            if (mc != null)
                foreach (var t in mc.NetPlayers) r.playerList.Add(Info(t.NetID));
            matchEnd = r.startTime + r.matchDuration;
            ended = false;
            liveCoins.Clear();
            crownHolder = -1;
            groundItems.Clear();
            resultsCredited = false;
            scoreboardShown = false;
            lastOneAt = 0;
            hud = null;
            matchStart = r.startTime;
            Send(1001, r);
            Log.Line("local multiplayer: " + r.playerList.Count + " player(s), " + rules + ", " + (r.matchDuration / 1000) + " s, world " + WorldName());
        }

        // the knock-outs have no timer: an hour stands for none (they end with one player left)
        static bool KnockOut { get { return rules == Rules.LastOne || rules == Rules.HopNonStop; } }
        static int Duration() { return KnockOut ? 3600000 : mode == "Crown" ? CrownMs : ClassicMs; }

        static CoinInfo NewCoin(PosInfo pos, int type)
        {
            int id = nextCoin++;
            if (nextCoin > 30000) nextCoin = 1;
            liveCoins.Add(id);
            var c = new CoinInfo();
            c.pos = pos;
            c.info = new byte[] { (byte)(id >> 8), (byte)(id & 255), (byte)type };
            return c;
        }

        static void PickCoin(PickCoinRequest q)
        {
            if (q.oldCoin == null || q.oldCoin.info == null || q.oldCoin.pos == null) return;
            int id = NetCoinPickUp.getId(q.oldCoin.info);
            if (!liveCoins.Remove(id)) return; // taken already (triggers can fire twice)
            var lp = MultiplayerController.ParsePosInfoToLocalPosInfo(q.oldCoin.pos);
            var mc = MultiplayerController.Instance();
            var t = mc.GetNetPlayer(lp.playerId);
            var r = new PickCoinResponse();
            r.player = new PlayerInfo();
            r.player.playerIdInGame = lp.playerId;
            r.player.point = (t != null ? t.Score : 0) + 1;
            r.oldCoin = q.oldCoin;
            int type = q.oldCoin.info.Length > 2 ? q.oldCoin.info[2] : 0;
            r.newCoin = type == 0 && q.newCoin != null && q.newCoin.pos != null ? NewCoin(q.newCoin.pos, 0) : null;
            Send(1007, r);
        }

        static void Die(DieRequest q)
        {
            if (q.pos == null) return;
            var lp = MultiplayerController.ParsePosInfoToLocalPosInfo(q.pos);
            var r = new DieResponse();
            r.pos = q.pos;
            r.dieType = q.dieType;
            if (rules == Rules.Coins)
                foreach (var c in q.coinList)
                    if (c != null && c.pos != null) r.coinList.Add(NewCoin(c.pos, 1));
            if (mode == "Crown" && crownHolder == lp.playerId)
            {
                var d = new CrownInfo();
                d.id = 0; d.type = 0; d.pos = q.pos;
                r.dropItem = d;
                crownHolder = -1;
            }
            Send(1003, r);
        }

        static void PickCrown(PickCrownRequest q)
        {
            if (q.crown == null || q.crown.pos == null) return;
            var lp = MultiplayerController.ParsePosInfoToLocalPosInfo(q.crown.pos);
            if (q.crown.type == 0)
            {
                if (crownHolder != -1) return;
                crownHolder = lp.playerId;
            }
            else if (q.crown.type == 1 || q.crown.type == 2)
            {
                if (!groundItems.Remove(q.crown.type)) return;
            }
            var mc = MultiplayerController.Instance();
            var t = mc.GetNetPlayer(lp.playerId);
            var r = new PickCrownResponse();
            r.player = new PlayerInfo();
            r.player.playerIdInGame = lp.playerId;
            r.player.point = t != null ? t.Score : 0;
            r.pickTime = Now();
            r.expiredTime = q.crown.type == 0 ? 0 : 5000;
            r.crown = q.crown;
            Send(1010, r);
        }

        // a push: the mover asked to move into another player's tile
        static void Move(MoveRequest q)
        {
            if (q.pos == null) return;
            var lp = MultiplayerController.ParsePosInfoToLocalPosInfo(q.pos);
            int input = 0, push = 0;
            TNetPackeHelper.UnpackInput((byte)lp.direction, ref input, ref push);
            var mc = MultiplayerController.Instance();
            var mover = mc.GetNetPlayer(lp.playerId);
            var pc = mover != null ? mover.ObjectController : null;
            if (pc == null || !pc.blockInput) return; // an ordinary move: nobody to tell
            var r = new PushResponse();
            PlayerController victim = null;
            if (push == 1)
                foreach (var t in mc.NetPlayers)
                {
                    var o = t.ObjectController;
                    if (o == null || o == pc || o.IsDead) continue;
                    if (Mathf.RoundToInt(o.TargetPosition.x) == lp.posX && Mathf.RoundToInt(o.TargetPosition.z) == lp.posY) { victim = o; break; }
                }
            if (victim != null)
            {
                r.success = true;
                var pi = new PushInfo();
                pi.playerId = victim.storedNetID;
                pi.direction = TNetPackeHelper.PackInput(input, 0);
                pi.step = 1;
                r.pushList.Add(pi);
            }
            else r.success = false;
            Send(1012, r);
        }

        static void EndMatch()
        {
            ended = true;
            var mc = MultiplayerController.Instance();
            var r = new EndMatchResponse();
            var byScore = new List<TNetPlayer>(mc.NetPlayers);
            byScore.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var t in mc.NetPlayers)
            {
                var p = new PlayerInfo();
                p.playerIdInGame = t.NetID;
                p.playerName = t.Name;
                p.characterId = t.CharID ?? "";
                p.point = t.Score;
                if (mode == "Crown")
                {
                    int place = byScore.IndexOf(t);
                    p.ticketWon = place == 0 ? 3 : place == 1 ? 2 : place == 2 ? 1 : 0;
                    p.ticketEarned = 0; p.ticketLeft = 100;
                }
                else
                {
                    p.coinWon = Math.Max(0, t.Score);
                    p.coinEarned = 0;
                }
                r.playerList.Add(p);
            }
            // the results footer is player 1's: it tells the team's coins (all go to the save)
            if (mode != "Crown")
            {
                int team = 0;
                foreach (var p in r.playerList) team += p.coinWon;
                foreach (var p in r.playerList)
                    if (p.playerIdInGame == 0) { p.coinWon = team; p.coinLeft = team + 500; }
            }
            Send(1005, r);
            Tally(mc);
            Log.Line("local multiplayer: match over");
        }

        // ============================================================ the frame
        static bool scoreboardShown, resultsCredited;
        static MultiGameStatus lastStatus = MultiGameStatus.NOT_AVAILABLE;
        static string lastGroup;

        static void Frame()
        {
            if (!session) return;
            var mc = MultiplayerController.Instance();
            if (mc == null) return;
            if (mc.gameStatus != lastStatus) { Log.Line("local multiplayer: status " + mc.gameStatus); lastStatus = mc.gameStatus; }
            var top = MenuPad.TopGroup();
            string group = top != null ? top.name : null;
            if (group != lastGroup) { lastGroup = group; Log.Line("local multiplayer: screen " + (group ?? "(none)")); }
            bool wipe = WipeController.instance != null && WipeController.instance.isWipeInProgress();
            if (group == "LeaderboardMultiplayer") StyleBoard(top);
            switch (mc.gameStatus)
            {
                case MultiGameStatus.LOBBY:
                    bool room = group == "MultiPlayerRoomScreen";
                    if (standingsNext && room)
                    {
                        // the session's wins, once the room is in (B there is the room again)
                        Furnish(false);
                        if (standingsAt == 0f) standingsAt = Time.realtimeSinceStartup;
                        else if (Time.realtimeSinceStartup - standingsAt > 0.6f)
                        {
                            standingsNext = false;
                            standingsAt = 0f;
                            GameController.instance.ShowLeaderboardMultiplayer();
                        }
                        break;
                    }
                    if (chooser >= 0 || room) Lobby(mc);
                    Furnish(room && chooser < 0 && !wipe);
                    ChooserUi(chooser >= 0 && group == "CharacterSelector" && !wipe);
                    break;
                case MultiGameStatus.PLAYING:
                    Native.SetGameSlot(-1);
                    bool held = PauseControls(mc);
                    MenuPad.Suspended = paused; // B belongs to the pause
                    if (held) break;
                    RouteInput(mc);
                    if (!scoreboardShown)
                    {
                        var sb = UnityEngine.Object.FindObjectOfType<ScoreBoardUI>();
                        if (sb != null && sb.m_toggleScoreboard != null) { sb.m_toggleScoreboard.SetActive(true); scoreboardShown = true; }
                    }
                    Rulebook(mc);
                    if (!ended && Now() >= matchEnd) EndMatch();
                    break;
                default:
                    MenuPad.Suspended = false;
                    if (paused) Pause(false);
                    Furnish(false);
                    ChooserUi(false);
                    break;
            }
            if (mc.gameStatus != MultiGameStatus.LOBBY) { Furnish(false); ChooserUi(false); }
        }

        // the room: other controllers join with A (or +) and leave with B (or -);
        // anyone's Y is the character select, X the profile picker
        static string signWorld;
        static int padsSeen = -1;
        static void Lobby(MultiplayerController mc)
        {
            if (chooser >= 0) { Choosing(); return; } // someone is in the character select
            bool changed = false;
            // player 1: the world (L/R), more controllers (+), a figurine (Y), a profile (X)
            var p1 = Pads.P1;
            if (rules != Rules.Crown && p1.Pressed(Btn.L | Btn.R | Btn.ZL | Btn.ZR))
            {
                int step = p1.Pressed(Btn.L | Btn.ZL) ? -1 : 1;
                StepWorld(step);
                StripStep(step);
                Native.Rumble(p1.Slot, 0.3f, 60);
            }
            if (p1.Pressed(Btn.Plus)) { AddControllers(); return; }
            if (p1.Pressed(Btn.Y)) { OpenChooser(0); return; }
            if (p1.Pressed(Btn.X) && PickProfile(0)) { Rename(mc, 0); changed = true; }
            for (int s = 1; s < Seats; s++)
            {
                var pad = Pads.All[s];
                var seat = seats[s];
                if (!pad.Connected)
                {
                    if (seat.In) { Leave(s); changed = true; }
                    continue;
                }
                if (!seat.In)
                {
                    if (!pad.Pressed(Btn.A | Btn.Plus)) continue;
                    seat.In = true;
                    seat.Slot = s;
                    if (seat.CharId == null) seat.CharId = RandomCharacter(null);
                    var r = new MatchUpdateResponse();
                    r.state = 0; r.hostId = 0; r.player = Info(s);
                    Send(1006, r);
                    Native.Rumble(s, 0.6f, 120);
                    changed = true;
                    continue;
                }
                if (pad.Pressed(Btn.B | Btn.Minus)) { Leave(s); changed = true; continue; }
                if (pad.Pressed(Btn.X) && PickProfile(s)) { Rename(mc, s); changed = true; continue; }
                if (pad.Pressed(Btn.Y)) { OpenChooser(s); return; }
            }
            // a controller came or went: the list's empty seats say how to take them
            int padMask = 0;
            for (int s = 1; s < Seats; s++) if (Pads.All[s].Connected) padMask |= 1 << s;
            if (padMask != padsSeen) { padsSeen = padMask; changed = true; }
            if (changed) RefreshRoom();
            // the sign: the world's name (the Crown: the mode's), its card under it (Furnish)
            string world = rules == Rules.Crown ? ModeName() : WorldName();
            if (world != signWorld)
            {
                signWorld = world;
                var sign = SingletonMonobehaviour<MultiWaitingRoom3DTextInfoController>.Instance;
                if (sign != null) sign.Update3DText(world, "", "", 0);
            }
        }

        // + in the room: the Switch's controller screen, to pair more controllers
        static void AddControllers()
        {
            int n = Native.ControllerApplet(1, Seats);
            Pads.Update(0f);
            Log.Line("local multiplayer: controller screen, " + (n < 0 ? "unavailable" : n + " controller(s)"));
            padsSeen = -1;
        }

        static void RefreshRoom()
        {
            var lm = SingletonMonobehaviour<LobbyManagerNew>.Instance;
            if (lm != null && lm.multiplayerRoomScreenController != null) lm.multiplayerRoomScreenController.UpdateWaitingRoomInfo();
        }

        // ------------------------------------------------------------ the character select, for one player
        // Y in the room: the game's own character select, driven by that player's
        // controller; its Play gives the seat that figurine and comes back here.
        static int chooser = -1;
        static CharacterSelectionButtonController chooserUi;
        static float chooserOpenedAt;
        static void OpenChooser(int s)
        {
            var gc = GameController.instance;
            if (gc == null) return;
            chooser = s;
            chooserOpenedAt = Time.realtimeSinceStartup;
            var pad = s == 0 ? Pads.P1 : Pads.All[seats[s].Slot];
            Native.SetGameSlot(pad.Slot);             // the game's own controller input: theirs
            Pads.GameOverride = pad;                   // and the port's menu controls
            MenuPad.HoldUntil = Loader.FrameCount + 20; // the Y that opened it is not a press in it
            gc.ButtonCharacterSelect();
            chooserUi = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<CharacterSelectionButtonController>())
                if (!c.isCreditsCharacterSelector) { chooserUi = c; break; }
            hiddenForChooser.Clear();
            Log.Line("local multiplayer: P" + (s + 1) + " picks a character");
        }

        public static bool ChoosingFigurine { get { return chooser >= 0; } }

        static void Choosing()
        {
            // a figurine to play, nothing else: the prize machine, random, info,
            // share and buy buttons go (the game puts some back as the carousel
            // turns), and so does its figurine count (the player's badge is there)
            var ui = chooserUi;
            if (ui != null)
            {
                HideForChooser(ui.PrizeButton); HideForChooser(ui.PlayRandomButton); HideForChooser(ui.ShareButton);
                HideForChooser(ui.InfoButton); HideForChooser(ui.BuyButton); HideForChooser(ui.LockButton);
                HideForChooser(ui.toyScanButton); HideForChooser(ui.toyScanInfoButton); HideForChooser(ui.cheatedUnlockBtn);
                if (ui.CharacterCountText != null && ui.CharacterCountText.gameObject.activeSelf) ui.CharacterCountText.gameObject.SetActive(false);
            }
            // once the carousel is up: centred on the seat's current figurine
            if (chooserOpenedAt > 0f && Time.realtimeSinceStartup - chooserOpenedAt > 0.6f)
            {
                chooserOpenedAt = 0f;
                var gc = GameController.instance;
                var row = gc != null && gc.characterMeshes != null ? gc.characterMeshes.GetCurrentRow() : null;
                string cur = seats[chooser].CharId;
                if (row != null && !string.IsNullOrEmpty(cur))
                {
                    try { row.ScrollToCharacter(cur, true); } catch (Exception) { }
                }
            }
        }

        static readonly List<GameObject> hiddenForChooser = new List<GameObject>();
        static void HideForChooser(UnityEngine.UI.Selectable b)
        {
            if (b == null || !b.gameObject.activeSelf) return;
            b.gameObject.SetActive(false);
            if (!hiddenForChooser.Contains(b.gameObject)) hiddenForChooser.Add(b.gameObject);
        }

        // the character select as the game left it, for player 1's own next visit
        static void RestoreChooserButtons()
        {
            foreach (var go in hiddenForChooser) if (go != null) go.SetActive(true);
            hiddenForChooser.Clear();
            if (chooserUi != null && chooserUi.CharacterCountText != null) chooserUi.CharacterCountText.gameObject.SetActive(true);
        }

        static void CloseChooser(string charId)
        {
            int s = chooser;
            chooser = -1;
            ChooserUi(false);
            RestoreChooserButtons();
            chooserUi = null;
            Pads.GameOverride = null;
            Native.SetGameSlot(-1);
            MenuPad.HoldUntil = Loader.FrameCount + 20;  // nor the A that picked, in the room
            chooserClosedAt = Loader.FrameCount;
            focusPlay = true;
            var gc = GameController.instance;
            if (gc != null) gc.CloseCharacterSelection(false);
            MenuController.instance.showUI("MultiPlayerRoomScreen");
            if (s < 0) return;
            if (charId != null)
            {
                seats[s].CharId = charId.Length > 0 ? charId : RandomCharacter(seats[s].CharId);
                var mc = MultiplayerController.Instance();
                if (mc != null) Rename(mc, s);
                Log.Line("local multiplayer: P" + (s + 1) + " plays " + seats[s].CharId);
            }
            RefreshRoom();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_CharacterSelected(GameController self, string characterId, int universeId, bool force) { throw new InvalidOperationException("hook stub"); }
        static int chooserClosedAt = -1000;
        static void CharacterSelected(GameController self, string characterId, int universeId, bool force)
        {
            if (chooser < 0)
            {
                // the same press, seen twice (the game's own handler and the Play button): it
                // picked a seat's character, it does not play the game's
                if (Loader.FrameCount - chooserClosedAt < 40) return;
                Orig_CharacterSelected(self, characterId, universeId, force);
                return;
            }
            CloseChooser(characterId ?? "");                   // the seat's, not the game's selection
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_CharacterClose(GameController self) { throw new InvalidOperationException("hook stub"); }
        static void CharacterClose(GameController self)
        {
            if (chooser < 0) { Orig_CharacterClose(self); return; }
            CloseChooser(null);                                // B: back to the room, no change
        }

        static void Rename(MultiplayerController mc, int s)
        {
            var t = mc.GetNetPlayer(s);
            if (t != null) { t.CharID = seats[s].CharId; t.Name = SeatName(s); }
        }

        // the Switch's profile picker for one seat (profiles other seats hold are greyed out)
        static bool PickProfile(int s)
        {
            if (!Profiles.On) return false;
            var taken = new List<string>();
            for (int i = 0; i < Seats; i++) if (i != s && seats[i].In && seats[i].Uid != null) taken.Add(seats[i].Uid);
            string uid = Profiles.Pick(taken);
            Pads.Update(0f);
            if (uid == null || uid == seats[s].Uid) return false;
            seats[s].Uid = uid;
            Log.Line("local multiplayer: P" + (s + 1) + " is " + (Profiles.Nickname(uid) ?? uid));
            return true;
        }

        // ------------------------------------------------------------ the world
        // Coin Crazy and the new modes can be played in any theme's world (the
        // game always used Mickey & Friends'); the Crown keeps its arena.
        static int worldIndex;            // maps[i]; 0 = Mickey & Friends (the game's own)
        static List<Universe> maps;
        static Universe matchWorld;
        static List<Universe> Maps()
        {
            if (maps != null) return maps;
            maps = new List<Universe>();
            var um = UniverseManager.Instance;
            if (um == null || um.universeList == null) return maps;
            foreach (var u in um.universeList)
                if (u != null && u.Id > 0 && !u.isHidden && WorldCharacter(u) != null) maps.Add(u);
            maps.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            int friends = maps.FindIndex(u => u.Id == 2);
            if (friends > 0) { var f = maps[friends]; maps.RemoveAt(friends); maps.Insert(0, f); }
            return maps;
        }
        static void StepWorld(int step)
        {
            int n = Maps().Count;
            if (n == 0) return;
            worldIndex = ((worldIndex + step) % n + n) % n;
        }

        static Universe ChosenWorld()
        {
            var l = Maps();
            return worldIndex >= 0 && worldIndex < l.Count ? l[worldIndex] : null;
        }

        static string WorldName()
        {
            var l = Maps();
            if (worldIndex < 0 || worldIndex >= l.Count) return "Multiplayer";
            string n = Language.Get(l[worldIndex].NameLocalisationId);
            return string.IsNullOrEmpty(n) ? l[worldIndex].name : n;
        }
        static string ModeName()
        {
            switch (rules)
            {
                case Rules.Crown: return Language.Get("GG MULTIPLAYER MODE CROWN MODE NAME");
                case Rules.LastOne: return Language.Get("GG MULTIPLAYER MODE LAST MAN STANDING");
                case Rules.HopNonStop: return Language.Get("GG MULTIPLAYER MODE FURTHEST CROSSER");
            }
            return Language.Get("GG MULTIPLAYER MODE COIN COLLECTION");
        }
        // the figurine whose world a theme is: one of its own that wears the theme's default look
        static Character WorldCharacter(Universe u)
        {
            var cm = CharacterManager.Instance;
            if (cm == null || cm.characterList == null) return null;
            string look = u.DefaultWorldPieceSwap != null && u.DefaultWorldPieceSwap.Length > 0 ? u.DefaultWorldPieceSwap[0] : null;
            Character any = null;
            foreach (var c in cm.characterList)
            {
                if (c == null || c.universe != u || c.prefab == null) continue;
                var cfg = c.characterConfig();
                if (cfg == null || cfg.isBundle) continue;
                string sw = c.worldPieceSwap != null && c.worldPieceSwap.Length > 0 ? c.worldPieceSwap[0] : null;
                if (string.IsNullOrEmpty(sw) || sw == look) return c;
                if (any == null) any = c;
            }
            return any;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_InitialiseCharacter(GlobalController self) { throw new InvalidOperationException("hook stub"); }
        static void InitialiseCharacter(GlobalController self)
        {
            bool mp = GoGameManager.Instance().GameMode == GameMode.MULTIPLAYER;
            if (!session || rules == Rules.Crown || !mp || worldIndex == 0)
            {
                matchWorld = null;
                Orig_InitialiseCharacter(self);
                return;
            }
            Universe u = ChosenWorld();
            Character c = u != null ? WorldCharacter(u) : null;
            if (c == null) { Orig_InitialiseCharacter(self); return; }
            matchWorld = u;
            self.currentCharacter = c;                   // the world, its traffic, its music (players keep their own figurines)
            NotificationServer.instance.postNotification("NewUniverse", c.universe.audioKey);
            self.PlayMusicIfSet();
            Log.Line("local multiplayer: the world of " + u.name);
        }

        static void Leave(int s)
        {
            seats[s].In = false;
            var r = new MatchUpdateResponse();
            r.state = 1; r.hostId = 0; r.player = Info(s);
            Send(1006, r);
        }

        static List<string> Unlocked()
        {
            var l = new List<string>();
            var cm = CharacterManager.Instance;
            if (cm == null || cm.characterList == null) return l;
            foreach (var c in cm.characterList)
            {
                if (c == null || !c.IsUnlocked() || c.prefab == null) continue;
                var cfg = c.characterConfig();
                if (cfg == null || cfg.isBundle) continue;
                l.Add(c.id);
            }
            l.Sort((a, b) =>
            {
                var ca = cm.GetCharacterByIdOrNull(a, false); var cb = cm.GetCharacterByIdOrNull(b, false);
                return (ca != null ? ca.GetSortOrder() : 0).CompareTo(cb != null ? cb.GetSortOrder() : 0);
            });
            return l;
        }
        static string NextCharacter(string cur, int step)
        {
            var l = Unlocked();
            if (l.Count == 0) return cur;
            int i = cur == null ? -1 : l.IndexOf(cur);
            i = ((i + step) % l.Count + l.Count) % l.Count;
            return l[i];
        }
        static string RandomCharacter(string not)
        {
            var l = Unlocked();
            if (l.Count == 0) return "802";
            string c;
            int guard = 0;
            do { c = l[rng.Next(l.Count)]; } while (c == not && guard++ < 8);
            return c;
        }

        // each seat's controller hops its own player (hop on press, as the port does
        // for one player); forward is refused off the top of the screen
        static void RouteInput(MultiplayerController mc)
        {
            var gc = GameController.instance;
            if (gc == null || gc.cameraPlayerParent == null) return;
            float camZ = gc.cameraPlayerParent.position.z;
            bool crown = GoGameManager.instance.MultiMode == MultiplayerMode.THE_CROWN;
            foreach (var t in mc.NetPlayers)
            {
                var pc = t.ObjectController;
                if (pc == null || pc.IsDead || t.NetID < 0 || t.NetID >= Seats) continue;
                var pad = t.NetID == 0 ? Pads.P1 : Pads.All[seats[t.NetID].Slot];
                uint press = pad.Down | (pad.Nav & ~pad.NavRepeat);
                int dir = 0;
                if ((press & (Btn.Up | Btn.A)) != 0) dir = 1;
                else if ((press & Btn.Down) != 0) dir = 2;
                else if ((press & Btn.Left) != 0) dir = 3;
                else if ((press & Btn.Right) != 0) dir = 4;
                if (dir == 0) continue;
                if (dir == 1 && !crown && pc.TargetPosition.z + 1f > camZ + TopRows) continue;
                pc.CmdTouchAction(dir);
            }
        }

        // player 1 is fed by the routing above in a local match, not by the game's input
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_TouchAction(GameController self, int dir) { throw new InvalidOperationException("hook stub"); }
        static void TouchAction(GameController self, int dir)
        {
            if (InMatch) return;
            Orig_TouchAction(self, dir);
        }

        // ============================================================ players
        static TNetPlayer NetOf(PlayerController pc)
        {
            var mc = MultiplayerController.Instance();
            if (mc == null) return null;
            foreach (var t in mc.NetPlayers)
                if (t.ObjectController == pc) return t;
            return null;
        }

        // run `a` as if `pc` were the game's one local player
        struct Ctx
        {
            public TNetPlayer net;
            public PlayerController pc;
        }
        static Ctx Enter(PlayerController pc, TNetPlayer t)
        {
            var mc = MultiplayerController.Instance();
            var gc = GameController.instance;
            var saved = new Ctx { net = mc.m_curNetPlayer, pc = gc != null ? gc.playerController : null };
            if (t != null) mc.m_curNetPlayer = t;
            if (gc != null && pc != null) gc.playerController = pc;
            return saved;
        }
        static void Leave(Ctx saved)
        {
            var mc = MultiplayerController.Instance();
            var gc = GameController.instance;
            mc.m_curNetPlayer = saved.net;
            if (gc != null) gc.playerController = saved.pc;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_CreatePlayers(BaseGameMode self, Vector3[] list) { throw new InvalidOperationException("hook stub"); }
        static void CreatePlayers(BaseGameMode self, Vector3[] list)
        {
            if (!session) { Orig_CreatePlayers(self, list); return; }
            var mc = MultiplayerController.Instance();
            var gc = GameController.instance;
            bool crown = list.Length <= 4;
            int[] classic = { 2, 4, 1, 5 }; // x -1, 1, -2, 2: around the middle
            PlayerController p1 = null;
            foreach (var t in mc.NetPlayers)
            {
                int k = t.NetID < 0 ? 0 : t.NetID % Seats;
                Vector3 pos = crown ? list[k % list.Length] : list[classic[k] % list.Length];
                string charId = !string.IsNullOrEmpty(t.CharID) ? t.CharID : "802";
                if (CharacterManager.Instance.GetCharacterByIdOrNull(charId, false) == null) charId = "802";
                var pc = gc.SpawnPlayerMultiplayer(charId, pos, t.NetID, true);
                if (pc == null) continue;
                pc.name = "Player " + t.NetID;
                pc.IsLocalPlayer = true;
                t.IsLocalPlayer = true;
                t.ObjectController = pc;
                var al = pc.GetComponent<AudioListener>();
                if (al != null) al.enabled = t.NetID == 0;
                if (t.NetID == 0) p1 = pc;
            }
            if (p1 == null && mc.NetPlayers.Count > 0) p1 = mc.NetPlayers[0].ObjectController;
            if (p1 != null) gc.SetPlayerController(p1);
            gc.IsLocalPlayerFound = true;
            Log.Line("local multiplayer: " + mc.NetPlayers.Count + " players spawned");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_PlayerUpdate(PlayerController self) { throw new InvalidOperationException("hook stub"); }
        static void PlayerUpdate(PlayerController self)
        {
            if (!InMatch || !self.IsLocalPlayer) { Orig_PlayerUpdate(self); return; }
            var saved = Enter(self, NetOf(self));
            try { Orig_PlayerUpdate(self); }
            catch (NullReferenceException) { if (!self.IsDeadUnspawn) throw; } // a knocked-out body the lanes have left behind
            finally { Leave(saved); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_KillMultiplayer(PlayerController self, DeathType type, GameObject killer) { throw new InvalidOperationException("hook stub"); }
        static void KillMultiplayer(PlayerController self, DeathType type, GameObject killer)
        {
            if (!InMatch) { Orig_KillMultiplayer(self, type, killer); return; }
            var t = NetOf(self);
            if (KnockOut && !self.IsDead) self.IsDeadUnspawn = true; // out for good: no respawn routine
            var saved = Enter(self, t);
            try { Orig_KillMultiplayer(self, type, killer); }
            finally { Leave(saved); }
            if (t != null && t.NetID >= 0 && t.NetID < Seats)
                Native.Rumble(t.NetID == 0 ? Pads.P1.Slot : seats[t.NetID].Slot, 0.8f, 200);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_UnpackDieResponse(CollectCoinMode self, DieResponse r) { throw new InvalidOperationException("hook stub"); }
        static void UnpackDieResponse(CollectCoinMode self, DieResponse r)
        {
            if (!InMatch || r == null || r.pos == null) { Orig_UnpackDieResponse(self, r); return; }
            var lp = MultiplayerController.ParsePosInfoToLocalPosInfo(r.pos);
            var t = MultiplayerController.Instance().GetNetPlayer(lp.playerId);
            var saved = Enter(t != null ? t.ObjectController : null, t); // the dier already lost the coins
            try { Orig_UnpackDieResponse(self, r); }
            finally { Leave(saved); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Orig_CheckPushOtherPlayer(PlayerController self, object obj) { throw new InvalidOperationException("hook stub"); }
        static bool CheckPushOtherPlayer(PlayerController self, object obj)
        {
            if (!InMatch) return Orig_CheckPushOtherPlayer(self, obj);
            var go = obj as GameObject;
            var o = go != null ? go.GetComponent<PlayerController>() : null;
            return o != null && o != self && !o.IsDead;
        }

        // the group's camera: the game's pace, pulled along by the leader; once a frame
        static int camFrame = -1;
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_UpdateCameraMultiplayer(PlayerController self) { throw new InvalidOperationException("hook stub"); }
        static void UpdateCameraMultiplayer(PlayerController self)
        {
            if (!InMatch) { Orig_UpdateCameraMultiplayer(self); return; }
            var mc = MultiplayerController.Instance();
            if (GoGameManager.instance.MultiMode == MultiplayerMode.THE_CROWN) return;
            var gc = GameController.instance;
            Transform cam = gc.cameraPlayerParent;
            if (cam == null) return;
            if (mc.gameStatus == MultiGameStatus.PLAYING && camFrame != Time.frameCount)
            {
                camFrame = Time.frameCount;
                // Last One Standing: faster and faster; Hop Non-Stop: a little faster,
                // and it runs with the leader (who falls behind falls off the screen)
                float elapsed = Math.Max(0f, (Now() - matchStart) / 1000f);
                float speed = self.m_speedCamera, leadRows = LeadRows, catchUp = CatchUp;
                if (rules == Rules.LastOne) speed *= 1f + elapsed / 45f;
                else if (rules == Rules.HopNonStop) { speed *= 1f + elapsed / 120f; leadRows = 1.5f; catchUp = 8f; }
                float z = cam.position.z + speed * Time.deltaTime;
                float lead = float.MinValue;
                foreach (var t in mc.NetPlayers)
                    if (t.ObjectController != null && !t.ObjectController.IsDead)
                        lead = Math.Max(lead, t.ObjectController.transform.position.z);
                if (lead > float.MinValue && lead - leadRows > z)
                    z = Mathf.MoveTowards(z, lead - leadRows, catchUp * Time.deltaTime);
                cam.position = new Vector3(cam.position.x, cam.position.y, z);
            }
            if (!self.hasJustRespawned && !self.IsDead && self.transform.position.z <= cam.position.z - 3.5f)
                self.Kill(DeathType.TooSlow);
        }

        // lanes leave the list only once they are well behind the camera (the game
        // dropped one per forward hop: four players emptied it)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_HopRemoveLane(WorldManager self) { throw new InvalidOperationException("hook stub"); }
        static void HopRemoveLane(WorldManager self)
        {
            if (!InMatch) { Orig_HopRemoveLane(self); return; }
            var gc = GameController.instance;
            if (gc == null || gc.cameraPlayerParent == null || self.ActiveLanes == null) return;
            float camZ = gc.cameraPlayerParent.position.z;
            while (self.ActiveLanes.Count > 20 && self.ActiveLanes[0] != null && self.ActiveLanes[0].transform.position.z < camZ - 6f)
                self.ActiveLanes.RemoveAt(0);
        }

        // respawn tiles: on screen, and not where another player stands
        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<Vector3> Orig_GetAllFreeSlotLane(WorldManager self, Vector3 p) { throw new InvalidOperationException("hook stub"); }
        static List<Vector3> GetAllFreeSlotLane(WorldManager self, Vector3 p)
        {
            var all = Orig_GetAllFreeSlotLane(self, p);
            if (!InMatch || all == null || all.Count == 0) return all;
            bool crown = GoGameManager.instance.MultiMode == MultiplayerMode.THE_CROWN;
            var mc = MultiplayerController.Instance();
            var ok = new List<Vector3>();
            foreach (var v in all)
            {
                if (!crown && (v.z - p.z < 0f || v.z - p.z > 5f)) continue;
                bool taken = false;
                foreach (var t in mc.NetPlayers)
                {
                    var o = t.ObjectController;
                    if (o != null && Mathf.RoundToInt(o.TargetPosition.x) == Mathf.RoundToInt(v.x) && Mathf.RoundToInt(o.TargetPosition.z) == Mathf.RoundToInt(v.z)) { taken = true; break; }
                }
                if (!taken) ok.Add(v);
            }
            return ok.Count > 0 ? ok : all;
        }

        // physics for every player: triggers owned by who entered them, hazards per player
        static readonly Dictionary<IPlayerTriggerable, PlayerController> owner = new Dictionary<IPlayerTriggerable, PlayerController>();
        static readonly List<PlayerController> alive = new List<PlayerController>();
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_PhysicsUpdate(CrossyPhysics self) { throw new InvalidOperationException("hook stub"); }
        static void PhysicsUpdate(CrossyPhysics self)
        {
            if (!InMatch) { owner.Clear(); Orig_PhysicsUpdate(self); return; }
            var mc = MultiplayerController.Instance();
            alive.Clear();
            foreach (var t in mc.NetPlayers)
                if (t.ObjectController != null) alive.Add(t.ObjectController);
            if (alive.Count == 0) return;

            var tr = self.triggers;
            for (int i = 0; i < tr.Length; i++)
            {
                var trig = tr[i];
                if (trig == null) continue;
                if (!trig.HasTriggered())
                {
                    Vector2 size = trig.GetTriggerSize();
                    Vector3 at = trig.GetTriggerPosition();
                    foreach (var p in alive)
                    {
                        if (p.IsDead || p.isMakingASuperHop) continue;
                        if (!CrossyPhysics.DoBoxesIntersect(p.transform.position, 0.1f, 0.1f, at, size.x, size.y)) continue;
                        owner[trig] = p;
                        var saved = Enter(p, NetOf(p));
                        try { trig.OnPlayerEntered(p); }
                        finally { Leave(saved); }
                        break;
                    }
                }
                else if (!trig.HasExited())
                {
                    PlayerController p;
                    if (!owner.TryGetValue(trig, out p) || p == null) p = alive[0];
                    Vector2 size = trig.GetTriggerSize();
                    Vector3 at = trig.GetTriggerPosition();
                    if (!CrossyPhysics.DoBoxesIntersect(p.transform.position, 0.1f, 0.1f, at, size.x, size.y))
                    {
                        owner.Remove(trig);
                        var saved = Enter(p, NetOf(p));
                        try { trig.OnPlayerExited(p); }
                        finally { Leave(saved); }
                    }
                }
            }

            foreach (var p in alive)
            {
                if (p.IsDead) continue;
                var saved = Enter(p, NetOf(p));
                try { Hazards(self, p); }
                finally { Leave(saved); }
            }
            for (int k = 0; k < self.boulders.Length; k++) self.TestBoulderAgainstWorld(self.boulders[k]);
            for (int n = 0; n < self.chargers.Length; n++) self.TestChargerAgainstWorld(self.chargers[n]);
            for (int s = 0; s < self.stampedes.Length; s++) self.TestStampedeAgainstWorld(self.stampedes[s]);
        }

        static void Hazards(CrossyPhysics self, PlayerController p)
        {
            Vector3 at = p.TargetPosition;
            var car = self.GetCarWithinBox(at, 0.29f, 0.52f);
            if (car != null) { p.Kill(DeathType.Car, car.gameObject); return; }
            var train = self.GetTrainWithinBox(at, 0.29f, 0.52f);
            if (train != null) { p.Kill(DeathType.Train, train.gameObject); return; }
            var limo = self.GetLimoWithinBox(at, 0.29f, 0.52f);
            if (limo != null) { p.Kill(DeathType.Car, limo.gameObject); return; }
            var st = self.GetStampedeWithinBox(at, 0.29f, 0.52f);
            if (st != null && !st.HasDodged()) { p.Kill(DeathType.Stampede, st.gameObject); return; }
            var fire = self.GetFireWithinBox(at, 0.29f, 0.52f);
            if (fire != null && fire.IsBurning) { p.Kill(DeathType.Fire, fire.gameObject); return; }
            for (int j = 0; j < self.cyclones.Length; j++)
            {
                var c = self.cyclones[j];
                if (CrossyPhysics.DoBoxesIntersect(at, 0.29f, 0.52f, c.transform.position, c.colliderSizeX, c.colliderSizeZ)) { p.Kill(DeathType.Cyclone, c.gameObject); return; }
            }
            for (int k = 0; k < self.boulders.Length; k++) self.TestBoulderAgainstPlayers(self.boulders[k]);
            for (int l = 0; l < self.snakes.Length; l++) self.TestSnakeAgainstPlayers(self.snakes[l]);
            for (int m = 0; m < self.walkers.Length; m++) self.TestWalkerAgainstPlayers(self.walkers[m]);
            for (int n = 0; n < self.chargers.Length; n++) self.TestChargerAgainstPlayers(self.chargers[n]);
            if (!p.isMakingASuperHop)
                for (int q = 0; q < self.pendulums.Length; q++) self.TestPendulumAgainstPlayers(self.pendulums[q]);
            for (int r = 0; r < self.mermaids.Length; r++) self.TestMermaidAgainstPlayer(self.mermaids[r]);
            for (int d = 0; d < self.derbyCars.Length; d++) self.TestDerbyCar(self.derbyCars[d]);
        }

        // ============================================================ HUD, crown, pauses, results
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ShowWaitingBanner(MultiPlayerHUD self) { throw new InvalidOperationException("hook stub"); }
        static void ShowWaitingBanner(MultiPlayerHUD self)
        {
            if (!InMatch) Orig_ShowWaitingBanner(self); // one player's death does not cover everyone's screen
        }

        static bool ClaimToStartMode(CrownMode self) { return false; } // free

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_AppPause(GoGameManager self, bool p) { throw new InvalidOperationException("hook stub"); }
        static void AppPause(GoGameManager self, bool p) { if (!session) Orig_AppPause(self, p); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_AppFocus(GoGameManager self, bool f) { throw new InvalidOperationException("hook stub"); }
        static void AppFocus(GoGameManager self, bool f) { if (!session) Orig_AppFocus(self, f); }

        // the head badges keep the player numbers (the game wrote the rank there)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MultiplayerHeadIndex Orig_HeadIndex(ScoreBoardUI self, int id) { throw new InvalidOperationException("hook stub"); }
        static MultiplayerHeadIndex HeadIndex(ScoreBoardUI self, int id) { return InMatch ? null : Orig_HeadIndex(self, id); }

        // the results' Next (and B): back to the room, the session's wins first
        static bool standingsNext, resultsLeaving;
        static float standingsAt;
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ResultsNext(MultiResultController self) { throw new InvalidOperationException("hook stub"); }
        static void ResultsNext(MultiResultController self)
        {
            if (session)
            {
                if (resultsLeaving) return;                // the scene is loading already
                resultsLeaving = true;
                standingsNext = true;
            }
            Orig_ResultsNext(self);
        }

        // B where the game had none (MenuPad): the results' is Next (their X leaves
        // multiplayer altogether), the session board's goes back where it came from
        public static bool Back(string group)
        {
            if (!session) return false;
            // in a match B is the pause's (PauseControls), never the game's way out
            var st = MultiplayerController.Instance();
            if (st != null && (st.gameStatus == MultiGameStatus.COUNT_DOWN || st.gameStatus == MultiGameStatus.PLAYING)) return true;
            if (group == "MultiPlayerResult")
            {
                var r = UnityEngine.Object.FindObjectOfType<MultiResultController>();
                if (r == null) return false;
                r.OnNextButtonClick();
                return true;
            }
            if (group == "LeaderboardMultiplayer") { CloseBoard(null); return true; }
            return false;
        }

        // After a match's Next the game reloads its scene, shows the title and, once
        // that has slid in, asks for the room again: the title flashed up between
        // the results and the room (it looked like B/Next went to the home menu).
        // Here the room is asked for straight away.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_BootFlow(GameController self) { throw new InvalidOperationException("hook stub"); }
        static void BootFlow(GameController self)
        {
            var gg = GlobalController.instance;
            var mc = MultiplayerController.Instance();
            // a match's scene: the title booted under the countdown (its buttons could flash, and take presses)
            if (session && mc != null && (mc.gameStatus == MultiGameStatus.COUNT_DOWN || mc.gameStatus == MultiGameStatus.PLAYING) &&
                GoGameManager.Instance().GameMode == GameMode.MULTIPLAYER)
            {
                Log.Line("local multiplayer: no title under the countdown");
                return;
            }
            if (session && gg != null && gg.ShouldShowMultiplayerSelectionPanelOnLoad && mc != null && mc.gameSelection != MultiGameSelection.QuickPlay)
            {
                var net = SingletonMonobehaviour<MultiplayerNetwork>.Instance;
                if (net != null) { net.EnableMutiplayerNetwork(true); net.EnableLobbyManager(true); }
                var lm = SingletonMonobehaviour<LobbyManagerNew>.Instance;
                if (lm != null)
                {
                    gg.ShouldShowMultiplayerSelectionPanelOnLoad = false;
                    lm.SendMatchRequest(mc.CurMatchId);
                    Log.Line("local multiplayer: straight back to the room");
                    return;
                }
            }
            Orig_BootFlow(self);
        }

        // no sharing on a Switch: the results' share button goes
        static void HideShare(Component under)
        {
            if (under == null) return;
            foreach (var b in under.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                if (b.name == "ShareButton") b.gameObject.SetActive(false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ResultClassic(MultiResultController self) { throw new InvalidOperationException("hook stub"); }
        static void ResultClassic(MultiResultController self)
        {
            Orig_ResultClassic(self);
            if (session) HideShare(self);
            if (!session || resultsCredited) return;
            resultsCredited = true;
            int coins = 0;
            foreach (var t in MultiplayerController.Instance().NetPlayers)
                if (t.NetID == 0) coins = Math.Max(0, t.CoinWon); // player 1's line carries the team's total
            if (coins > 0 && GlobalController.instance != null) GlobalController.instance.UpdateCoins(coins);
            Log.Line("local multiplayer: " + coins + " coins collected, added to the save");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ResultCrown(MultiResultController self) { throw new InvalidOperationException("hook stub"); }
        static void ResultCrown(MultiResultController self)
        {
            Orig_ResultCrown(self);
            if (session) HideShare(self);
            if (!session || resultsCredited) return;
            resultsCredited = true;
            int tickets = 0;
            foreach (var t in MultiplayerController.Instance().NetPlayers)
            {
                tickets += Math.Max(0, t.TicketWon);
                // the multiplayer leaderboard (Snatch and Run): seats with a profile, and player 1
                if (Profiles.On && t.NetID >= 0 && t.NetID < Seats)
                {
                    string who = seats[t.NetID].Uid ?? (t.NetID == 0 ? Profiles.Guest : null);
                    if (who != null) Profiles.RecordMultiplayer(who, t.CharID, t.Score);
                }
            }
            var cc = SingletonMonobehaviour<CloudController>.Instance;
            if (tickets > 0 && cc != null) cc.tickets += tickets;
            Log.Line("local multiplayer: " + tickets + " tickets won, added");
        }

        // ============================================================ the new modes
        static long matchStart, lastOneAt;

        static MultiPlayerHUD hud;
        static void Rulebook(MultiplayerController mc)
        {
            if (!KnockOut) return;
            // the score is seconds survived; the match ends a moment after one is left
            int alive = 0;
            long secs = Math.Max(0, (Now() - matchStart) / 1000);
            foreach (var t in mc.NetPlayers)
            {
                var pc = t.ObjectController;
                if (pc == null || pc.IsDead || pc.IsDeadUnspawn) continue;
                alive++;
                t.Score = (int)secs;
            }
            // no timer: the players still in, where it was
            if (hud == null) hud = UnityEngine.Object.FindObjectOfType<MultiPlayerHUD>();
            if (hud != null && hud.m_timerText != null)
            {
                hud.m_timerText.text = alive + " LEFT";
                hud.m_timerText.color = Color.white;
            }
            bool over = alive == 0 || (alive == 1 && mc.NetPlayers.Count > 1);
            if (!over) { lastOneAt = 0; return; }
            if (lastOneAt == 0) lastOneAt = Now();
            else if (!ended && Now() - lastOneAt > 1500)
            {
                foreach (var t in mc.NetPlayers)
                    if (t.ObjectController != null && !t.ObjectController.IsDead) t.Score += 1; // the survivor on top, even on the same second
                EndMatch();
            }
        }

        // the road ahead: the game built it for Coin Crazy and the Crown only (the
        // other modes it named were never finished), so a knock-out's camera ran
        // off the end of the world into black
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_GenerateLanes(WorldManager self) { throw new InvalidOperationException("hook stub"); }
        static void GenerateLanes(WorldManager self)
        {
            var m = GoGameManager.instance.MultiMode;
            if (m != MultiplayerMode.LAST_MAN_STANDING && m != MultiplayerMode.FURTHEST_CROSSER) { Orig_GenerateLanes(self); return; }
            if (self.camTransform != null && self.currentZ < self.camTransform.position.z + 21f)
                self.GenerateLane();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_InitCoin(CollectCoinMode self) { throw new InvalidOperationException("hook stub"); }
        static void InitCoin(CollectCoinMode self)
        {
            if (session && rules != Rules.Coins) return; // Last One Standing and Hop Non-Stop have no coins
            Orig_InitCoin(self);
        }

        // a coin leaving: replaced by another only while a Coin Crazy match runs (the
        // game also did it as the world was torn down after the results: exceptions)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_OnCoinDestroy(CollectCoinMode self, NetCoinPickUp coin, bool hasTriggered) { throw new InvalidOperationException("hook stub"); }
        static void OnCoinDestroy(CollectCoinMode self, NetCoinPickUp coin, bool hasTriggered)
        {
            var mc = MultiplayerController.Instance();
            if (session && (mc == null || mc.gameStatus != MultiGameStatus.PLAYING || rules != Rules.Coins)) return;
            try { Orig_OnCoinDestroy(self, coin, hasTriggered); }
            catch (NullReferenceException) { } // the world is going (a scene change)
        }

        // ============================================================ pause
        static bool paused;
        static long pausedAt;

        // + (any seated player) pauses; then + resumes and B ends the match. true while paused
        static bool PauseControls(MultiplayerController mc)
        {
            bool plus = false, b = false;
            for (int s = 0; s < Seats; s++)
            {
                if (!seats[s].In) continue;
                var pad = s == 0 ? Pads.P1 : Pads.All[seats[s].Slot];
                if (pad.Pressed(Btn.Plus)) plus = true;
                if (pad.Pressed(Btn.B)) b = true;
            }
            if (!paused)
            {
                if (plus && !ended) Pause(true);
                return paused;
            }
            if (plus) { Pause(false); return false; }
            if (b)
            {
                Pause(false);
                if (!ended) { Log.Line("local multiplayer: ended from the pause"); EndMatch(); }
                return false;
            }
            return true;
        }

        static void Pause(bool on)
        {
            var mc = MultiplayerController.Instance();
            if (on)
            {
                paused = true;
                pausedAt = Now();
                Time.timeScale = 0f;
                AudioListener.pause = true;
            }
            else
            {
                paused = false;
                long d = Math.Max(0, Now() - pausedAt); // the clock stood still
                if (mc != null) { mc.ServerEndTime += d; mc.ServerStartTime += d; }
                matchEnd += d;
                matchStart += d;
                Time.timeScale = 1f;
                AudioListener.pause = false;
                MenuPad.Suspended = false;
            }
            PauseUi(on);
        }

        // a dimmed screen: PAUSED in the game's pixel lettering, the two choices under it
        static Ui.Layer pauseLayer;
        static void PauseUi(bool on)
        {
            if (!on) { if (pauseLayer != null) pauseLayer.Show(false); return; }
            if (pauseLayer == null)
            {
                pauseLayer = new Ui.Layer("Pause");
                var dim = Ui.Node(pauseLayer.Rt, "Dim");
                Ui.Fill(dim);
                var img = dim.gameObject.AddComponent<UnityEngine.UI.Image>();
                img.color = new Color(0f, 0f, 0f, 0.6f);
                img.raycastTarget = false;
                var title = Ui.Label(pauseLayer.Rt, "PAUSED", Ui.Pixel, 96, Color.white);
                title.alignment = TextAnchor.MiddleCenter;
                Ui.Outlined(title, 4f, 8f);
                title.rectTransform.sizeDelta = new Vector2(900f, 140f);
                Ui.Place(title.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0f, 50f));
                var h = Ui.Hints(pauseLayer.Rt, 36f, "+", "Resume", "B", "End the match");
                Ui.Place(h, new Vector2(0.5f, 0.5f), new Vector2(0f, -70f));
            }
            pauseLayer.Show(true);
        }

        // + in a local match is this pause; the game's (a single-player screen) threw there
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ButtonPause(GameController self) { throw new InvalidOperationException("hook stub"); }
        static void ButtonPause(GameController self)
        {
            if (session && GoGameManager.Instance().GameMode == GameMode.MULTIPLAYER) return;
            Orig_ButtonPause(self);
        }

        // ============================================================ menus
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ModeScreenStart(MultiplayerSelectionModeScreen self) { throw new InvalidOperationException("hook stub"); }
        static void ModeScreenStart(MultiplayerSelectionModeScreen self)
        {
            Orig_ModeScreenStart(self);
            CrownOpen(self);
            try { ModeButtons(self); }
            catch (Exception e) { Loader.Report(e); }
            if (!On) return;
            // a mode opens its room: no quick play / join / create screen on one Switch
            if (self.CollectionBtn != null)
            {
                self.CollectionBtn.onClick.RemoveAllListeners();
                self.CollectionBtn.onClick.AddListener(() => EnterRoom(MultiplayerMode.COIN_COLLECTION));
            }
            if (self.TheCrownBtn != null)
            {
                self.TheCrownBtn.onClick.RemoveAllListeners();
                self.TheCrownBtn.onClick.AddListener(() => EnterRoom(MultiplayerMode.THE_CROWN));
            }
            try { ModeNavigation(self); }
            catch (Exception e) { Loader.Report(e); }
        }

        // The mode screen's controller moves, as the buttons sit:
        //   [Snatch and Run] [podium]
        //   [   Collect the Coins   ]
        //   [Last One]    [Hop Non-Stop]
        // (the automatic ones went from the bottom row up to Snatch and Run, and
        // from the podium down into the bottom row)
        static void ModeNavigation(MultiplayerSelectionModeScreen self)
        {
            UnityEngine.UI.Selectable crown = self.TheCrownBtn, board = self.MultiplayerLeaderboardBtn, coins = self.CollectionBtn, last = null, hop = null;
            var row = coins != null && coins.transform.parent != null ? coins.transform.parent.Find("DcrMoreModes") : null;
            if (row != null)
            {
                var l = row.Find("Dcr" + MultiplayerMode.LAST_MAN_STANDING);
                var h = row.Find("Dcr" + MultiplayerMode.FURTHEST_CROSSER);
                last = l != null ? l.GetComponent<UnityEngine.UI.Selectable>() : null;
                hop = h != null ? h.GetComponent<UnityEngine.UI.Selectable>() : null;
            }
            Link(crown, null, coins, null, board);
            Link(board, null, coins, crown, null);
            Link(coins, crown, last ?? hop, null, null);
            Link(last, coins, null, null, hop);
            Link(hop, coins, null, last, null);
        }

        static void Link(UnityEngine.UI.Selectable s, UnityEngine.UI.Selectable up, UnityEngine.UI.Selectable down, UnityEngine.UI.Selectable left, UnityEngine.UI.Selectable right)
        {
            if (s == null) return;
            var n = new UnityEngine.UI.Navigation();
            n.mode = UnityEngine.UI.Navigation.Mode.Explicit;
            n.selectOnUp = up; n.selectOnDown = down; n.selectOnLeft = left; n.selectOnRight = right;
            s.navigation = n;
        }

        // what the quick-play screen's buttons did, from the mode's own button
        static float enteringAt = -10f;
        static void EnterRoom(MultiplayerMode m)
        {
            if (Time.realtimeSinceStartup - enteringAt < 1.5f) return; // one press, one room
            enteringAt = Time.realtimeSinceStartup;
            GoGameManager.instance.MultiMode = m;
            var net = SingletonMonobehaviour<MultiplayerNetwork>.Instance;
            if (net != null) { net.EnableMutiplayerNetwork(true); net.EnableLobbyManager(true); }
            var mc = MultiplayerController.Instance();
            if (mc != null) mc.gameSelection = MultiGameSelection.CreateRoom;
            var lm = SingletonMonobehaviour<LobbyManagerNew>.Instance;
            if (lm != null) lm.CreateMatchRequest();
            Log.Line("local multiplayer: " + m + " room");
        }

        // out of the room (its X, player 1's B): the modes again, where the game
        // went to its quick-play screen
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ActiveUi(MultiplayerUIController self) { throw new InvalidOperationException("hook stub"); }
        static void ActiveUi(MultiplayerUIController self)
        {
            var mc = MultiplayerController.Instance();
            if (session && mc != null && mc.gameStatus == MultiGameStatus.NOT_AVAILABLE)
            {
                MenuController.instance.showUI("MultiplayerSelectionModeScreen");
                return;
            }
            Orig_ActiveUi(self);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_ModeScreenEnable(MultiplayerSelectionModeScreen self) { throw new InvalidOperationException("hook stub"); }
        static void ModeScreenEnable(MultiplayerSelectionModeScreen self)
        {
            Orig_ModeScreenEnable(self);
            CrownOpen(self);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_CrownCheck(MultiplayerSelectionModeScreen self, object ok) { throw new InvalidOperationException("hook stub"); }
        static void CrownCheck(MultiplayerSelectionModeScreen self, object ok)
        {
            Orig_CrownCheck(self, ok);
            CrownOpen(self); // the server's answer put the tags back
        }

        // Snatch and Run: open (free, and no price or "free" tag on any mode: nothing costs anything here)
        static void CrownOpen(MultiplayerSelectionModeScreen self)
        {
            if (!On) return;
            if (self.TheCrownBtn != null) self.TheCrownBtn.interactable = true;
            if (self.FreePriceTextObj != null) self.FreePriceTextObj.SetActive(false);
            if (self.Price != null) self.Price.gameObject.SetActive(false);
            NoTags(self.TheCrownBtn);
            NoTags(self.CollectionBtn);
        }

        // a mode button's price and "free" tags: every Text but its name
        static void NoTags(UnityEngine.UI.Button b)
        {
            if (b == null) return;
            foreach (var t in b.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                if (!t.gameObject.name.StartsWith("Text")) t.gameObject.SetActive(false);
        }

        // Last One Standing and Hop Non-Stop: a third row like the first (Snatch and
        // Run's row), each button a copy of Snatch and Run's with its own name
        static void ModeButtons(MultiplayerSelectionModeScreen self)
        {
            var coin = self.CollectionBtn;
            var crown = self.TheCrownBtn;
            if (coin == null || crown == null) return;
            var group = coin.transform.parent;
            var rowSrc = crown.transform.parent;
            if (group == null || rowSrc == null || group.Find("DcrMoreModes") != null) return;
            var row = UnityEngine.Object.Instantiate(rowSrc.gameObject, group, false);
            row.name = "DcrMoreModes";
            for (int i = row.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(row.transform.GetChild(i).gameObject);
            row.transform.SetSiblingIndex(coin.transform.GetSiblingIndex() + 1);
            ModeButton(self, row.transform, "GG MULTIPLAYER MODE LAST MAN STANDING", MultiplayerMode.LAST_MAN_STANDING);
            ModeButton(self, row.transform, "GG MULTIPLAYER MODE FURTHEST CROSSER", MultiplayerMode.FURTHEST_CROSSER);
            Log.Line("local multiplayer: Last One Standing and Hop Non-Stop on the mode screen");
        }

        static void ModeButton(MultiplayerSelectionModeScreen self, Transform parent, string key, MultiplayerMode m)
        {
            var go = UnityEngine.Object.Instantiate(self.TheCrownBtn.gameObject, parent, false);
            go.name = "Dcr" + m;
            var b = go.GetComponent<UnityEngine.UI.Button>();
            b.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            b.onClick.AddListener(() => EnterRoom(m));
            b.interactable = true;
            // its shadows are Snatch and Run's size (300 wide) while two of these share
            // a row: they stuck out either side as a dark halo -- they follow the face now
            var face = go.transform.Find("ButtonBG") as RectTransform;
            foreach (var n in new[] { "ShadowNormal", "ShadowHighlight", "ShadowPressed" })
            {
                var sh = go.transform.Find(n) as RectTransform;
                if (sh == null || face == null) continue;
                Vector3 drop = sh.localPosition - face.localPosition; // how far below the face it sits
                sh.anchorMin = face.anchorMin; sh.anchorMax = face.anchorMax; sh.pivot = face.pivot;
                sh.sizeDelta = face.sizeDelta;
                sh.anchoredPosition = face.anchoredPosition + new Vector2(drop.x, drop.y);
            }
            foreach (var lt in go.GetComponentsInChildren<LocalizedText>(true)) UnityEngine.Object.DestroyImmediate(lt);
            foreach (var t in go.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                if (t.gameObject.name.StartsWith("Text")) t.text = Language.Get(key);
                else t.gameObject.SetActive(false); // the price and "free" tags
            }
        }

        // "Join room" asked for a friend's room code; on one Switch there is the one room
        static void JoinRoom(MultiPlayerSelectionScreen self)
        {
            MultiplayerController.Instance().gameSelection = MultiGameSelection.CreateRoom;
            SingletonMonobehaviour<LobbyManagerNew>.Instance.CreateMatchRequest();
        }

        // the waiting room's player list: names without the number (the list has
        // it, in its colour), and a row for each seat still free saying how to take it
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_RoomInfo(MultiPlayerRoomScreenController self) { throw new InvalidOperationException("hook stub"); }
        static void RoomInfo(MultiPlayerRoomScreenController self)
        {
            var before = new HashSet<MuiltiPlayerRoomScreenPlayerInfo>();
            if (self.playerListTrans != null)
                foreach (var r in self.playerListTrans.GetComponentsInChildren<MuiltiPlayerRoomScreenPlayerInfo>()) before.Add(r);
            Orig_RoomInfo(self);
            if (!session) return;
            BoardButton(self);
            // the how-to-play screen goes back to the room (it went to the title screen)
            var info = MenuController.instance != null && MenuController.instance.panelHelper != null ? MenuController.instance.panelHelper.panelWithName("MultiPlayerInfo") : null;
            if (info != null) info.goBackToUI = "MultiPlayerRoomScreen";
            if (self.howToPlayText4 != null)
                self.howToPlayText4.text = "Everyone plays on this Switch: one controller each, up to " + Seats + " players.";
            try { RoomRows(self, before); }
            catch (Exception e) { Loader.Report(e); }
        }

        // Next to Play: the session's wins (the multiplayer leaderboard), where the
        // game had its share button -- that button, with the title's podium on it
        static Sprite podium;
        static void BoardButton(MultiPlayerRoomScreenController self)
        {
            UnityEngine.UI.Button share = null;
            foreach (var b in self.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                if (b.name == "ShareButton") { share = b; break; }
            if (share == null) return;
            if (!share.gameObject.activeSelf) share.gameObject.SetActive(true);
            if (!boardWired.Contains(share.GetInstanceID()))
            {
                share.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                share.onClick.AddListener(() => { var gc = GameController.instance; if (gc != null) gc.ShowLeaderboardMultiplayer(); });
                boardWired.Add(share.GetInstanceID());
            }
            if (podium == null)
                foreach (var b in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                {
                    if (b == null || b.name != "Leaderboard Button" || !b.gameObject.scene.IsValid()) continue;
                    var ic = b.transform.Find("ButtonBG/Icon");
                    var im = ic != null ? ic.GetComponent<UnityEngine.UI.Image>() : null;
                    if (im != null && im.sprite != null) { podium = im.sprite; break; }
                }
            var icon = share.transform.Find("ButtonBG/Icon");
            var img = icon != null ? icon.GetComponent<UnityEngine.UI.Image>() : null;
            if (img != null && podium != null && img.sprite != podium) { img.sprite = podium; img.preserveAspect = true; }
        }
        static readonly HashSet<int> boardWired = new HashSet<int>();

        static void RoomRows(MultiPlayerRoomScreenController self, HashSet<MuiltiPlayerRoomScreenPlayerInfo> before)
        {
            if (self.playerListTrans == null || self.playerTemplate == null) return;
            foreach (var row in self.playerListTrans.GetComponentsInChildren<MuiltiPlayerRoomScreenPlayerInfo>())
            {
                int order;
                if (before.Contains(row) || row.orderText == null || !int.TryParse(row.orderText.text, out order)) continue;
                if (order < 1 || order > Seats || row.playerNameText == null) continue;
                row.playerNameText.text = BaseName(order - 1);
                FitText(row.playerNameText);
            }
            bool offered = false;                              // one "add a controller" row is enough
            for (int s = 1; s < Seats; s++)
            {
                if (seats[s].In) continue;
                bool pad = Pads.All[s].Connected;
                if (!pad && offered) continue;
                if (!pad) offered = true;
                EmptySeat(self, s, pad);
            }
        }

        static void FitText(UnityEngine.UI.Text t)
        {
            if (t.resizeTextForBestFit) return;
            t.resizeTextMaxSize = t.fontSize;
            t.resizeTextMinSize = Math.Max(10, t.fontSize / 2);
            t.resizeTextForBestFit = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
        }

        // a free seat: its number, dimmed, and "(A) Join" -- or, with no controller
        // for it, "(+) Add Player" (player 1's +: the Switch's controller screen)
        static void EmptySeat(MultiPlayerRoomScreenController self, int s, bool connected)
        {
            var row = UnityEngine.Object.Instantiate(self.playerTemplate);
            row.transform.SetParent(self.playerListTrans);
            row.transform.localScale = self.playerTemplate.transform.localScale;
            row.UpdatePlayerInfo(s + 1, "", false);
            if (row.background != null) { var c = row.background.color; c.a = 0.35f; row.background.color = c; }
            if (row.orderText != null) { var c = row.orderText.color; c.a = 0.55f; row.orderText.color = c; }
            var name = row.playerNameText;
            if (name == null) return;
            name.enabled = false;
            var nrt = name.rectTransform;
            var hint = Ui.Node(nrt.parent, "DcrSeatHint");
            hint.anchorMin = nrt.anchorMin; hint.anchorMax = nrt.anchorMax; hint.pivot = nrt.pivot;
            hint.anchoredPosition = nrt.anchoredPosition; hint.sizeDelta = nrt.sizeDelta;
            hint.localScale = nrt.localScale;
            int size = name.resizeTextForBestFit ? Math.Min(name.fontSize, name.resizeTextMaxSize) : name.fontSize;
            var hl = hint.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            hl.childAlignment = TextAnchor.MiddleLeft;
            hl.spacing = size * 0.3f;
            hl.childControlWidth = hl.childControlHeight = true;
            hl.childForceExpandWidth = hl.childForceExpandHeight = false;
            float g = Mathf.Max(18f, size * 1.05f);
            Ui.Glyph(hint, connected ? "A" : "+", g);
            var t = Ui.Label(hint, connected ? "Join" : "Add Player", name.font, size, new Color(1f, 1f, 1f, 0.85f));
            // never past the list's edge: the words shrink to the room left
            float room = nrt.rect.width > 1f ? nrt.rect.width - g - hl.spacing : 0f;
            if (room > 20f)
            {
                var le = t.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                le.preferredWidth = room;
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Truncate;
                t.resizeTextForBestFit = true;
                t.resizeTextMaxSize = size;
                t.resizeTextMinSize = Math.Max(10, size / 2);
            }
        }

        // the mode's rules on the game's how-to-play screen
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_GameInfo(MultiplayerGameInfoUI self) { throw new InvalidOperationException("hook stub"); }
        static void GameInfo(MultiplayerGameInfoUI self)
        {
            Orig_GameInfo(self);
            var m = GoGameManager.Instance().MultiMode;
            if (m == MultiplayerMode.LAST_MAN_STANDING)
            {
                self.str1.text = Language.Get("GG MULTIPLAYER MODE LAST MAN STANDING FAQ");
                self.str2.text = "One life each, no timer.";
                self.str3.text = "The road speeds up and up.";
                self.str4.text = Language.Get("GG MULTIPLAYER HOWTOPLAY TEXT 3");
            }
            else if (m == MultiplayerMode.FURTHEST_CROSSER)
            {
                self.str1.text = Language.Get("GG MULTIPLAYER MODE LAST MAN STANDING FAQ");
                self.str2.text = "The road runs with whoever is in front.";
                self.str3.text = "Fall off the screen and you're out.";
                self.str4.text = Language.Get("GG MULTIPLAYER HOWTOPLAY TEXT 3");
            }
        }

        // ============================================================ the session
        // Wins and losses per player number since the game started (matches of
        // two or more). The multiplayer leaderboard shows the wins.
        static readonly int[] wins = new int[Seats], losses = new int[Seats];

        static void Tally(MultiplayerController mc)
        {
            if (mc.NetPlayers.Count < 2) return;
            int top = int.MinValue;
            foreach (var t in mc.NetPlayers) top = Math.Max(top, t.Score);
            foreach (var t in mc.NetPlayers)
            {
                if (t.NetID < 0 || t.NetID >= Seats) continue;
                if (t.Score == top) wins[t.NetID]++; else losses[t.NetID]++;
            }
        }

        public class Standing { public int Seat, Wins, Losses; public string Uid, Name, CharId; }

        // everyone seated or who has played, most wins first
        public static List<Standing> Standings()
        {
            var l = new List<Standing>();
            for (int s = 0; s < Seats; s++)
                if (s == 0 || seats[s].In || wins[s] + losses[s] > 0)
                    l.Add(new Standing { Seat = s, Wins = wins[s], Losses = losses[s], Uid = seats[s].Uid, Name = BaseName(s), CharId = seats[s].CharId });
            l.Sort((a, b) => a.Wins != b.Wins ? b.Wins.CompareTo(a.Wins) : a.Losses != b.Losses ? a.Losses.CompareTo(b.Losses) : a.Seat.CompareTo(b.Seat));
            return l;
        }

        public static int SeatOfBoardId(string id)
        {
            int n;
            if (id == null || !id.StartsWith("seat-") || !int.TryParse(id.Substring(5), out n)) return -1;
            return n >= 1 && n <= Seats ? n - 1 : -1;
        }

        // ------------------------------------------------------------ the session board
        // The game's weekly multiplayer leaderboard, as this session's: its title,
        // one list (no Top 100 / Friends tabs, no countdown, no pinned own row),
        // and on every row the player's number in their colour -- the avatar
        // itself for a player without a profile, a corner tag on a profile's icon.
        static void StyleBoard(PanelGroup top)
        {
            LeaderboardMultiplayerScreen ls = null;
            foreach (var p in top.panels)
                if (p != null && (ls = p.GetComponent<LeaderboardMultiplayerScreen>()) != null) break;
            if (ls == null) return;
            var c = ls.transform.Find("Leaderboard Contents");
            if (c == null) return;
            Off(c, "VerticalLayout/Title/GameObject/EndInTime");
            Off(c, "VerticalLayout/Buttons");
            Off(c, "VerticalLayout/Me");
            Off(c, "Header/Reward");
            var title = c.Find("VerticalLayout/Title/GameObject/TimeList");
            var tt = title != null ? title.GetComponent<UnityEngine.UI.Text>() : null;
            if (tt != null && tt.text != "Session Wins")
            {
                var lt = title.GetComponent<LocalizedText>();
                if (lt != null) lt.enabled = false;
                tt.text = "Session Wins";
            }
        }

        static void Off(Transform root, string path)
        {
            var t = root.Find(path);
            if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_BoardShown(LeaderboardMultiplayerScreen self) { throw new InvalidOperationException("hook stub"); }
        static void BoardShown(LeaderboardMultiplayerScreen self)
        {
            Orig_BoardShown(self);
            if (!session) return;
            try { BoardRows(self); }
            catch (Exception e) { Loader.Report(e); }
        }

        static void BoardRows(LeaderboardMultiplayerScreen self)
        {
            var disp = self._listPlayersDisplay;
            var info = self._listPlayers;
            if (disp == null || info == null) return;
            for (int i = 0; i < disp.Count && i < info.Count; i++)
            {
                var d = disp[i];
                int seat = SeatOfBoardId(info[i].userID);
                if (d == null || seat < 0) continue;
                // ties share a place (the game numbered the list)
                int rank = 1;
                foreach (var o in info) if (o.point > info[i].point) rank++;
                if (d.rank != null) d.rank.text = self.convertRankText(rank);
                try { self.changeColorBasedRank(d, rank - 1); } catch (Exception) { }
                // the bar the game filled with the world's internal name: a profile's
                // character, or (the name is the character's already) its theme
                if (d.worldName != null) d.worldName.text = !string.IsNullOrEmpty(info[i].avatarURL) ? Profiles.CharacterName(info[i].characterId) : ThemeOf(info[i].characterId);
                Badge(d, seat, !string.IsNullOrEmpty(info[i].avatarURL));
            }
        }

        static string ThemeOf(string charId)
        {
            var cm = CharacterManager.Instance;
            var c = cm != null && !string.IsNullOrEmpty(charId) ? cm.GetCharacterByIdOrNull(charId, false) : null;
            if (c == null || c.universe == null) return "";
            string n = Language.Get(c.universe.NameLocalisationId);
            return string.IsNullOrEmpty(n) || n == c.universe.NameLocalisationId ? c.universe.name : n;
        }

        static void Badge(LeaderboardPlayerDisplay d, int seat, bool profile)
        {
            if (d.avatar == null) return;
            var frame = d.avatar.transform.parent != null ? d.avatar.transform.parent : d.avatar.transform; // its round mask
            var old = frame.Find("DcrBadge");
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);
            var b = Ui.Node(frame, "DcrBadge");
            UnityEngine.UI.Text t;
            if (!profile)
            {
                d.avatar.texture = Ui.Solid(Ui.Player(seat));
                Ui.Fill(b);
                t = Ui.Label(b, "P" + (seat + 1), Ui.Pixel, 80, Color.white);
                Ui.Outlined(t, 3f, 5f);
                Ui.Fill(t.rectTransform);
                t.rectTransform.offsetMin = new Vector2(6f, 6f);
                t.rectTransform.offsetMax = new Vector2(-6f, -6f);
            }
            else
            {
                b.anchorMin = new Vector2(0f, 0f);
                b.anchorMax = new Vector2(0.6f, 0.36f);
                b.offsetMin = b.offsetMax = Vector2.zero;
                var bg = Ui.Round(b, "Tag", Ui.Player(seat));
                Ui.Fill(bg.rectTransform);
                t = Ui.Label(bg.rectTransform, "P" + (seat + 1), Ui.Pixel, 40, Color.white);
                Ui.Outlined(t, 2f, 0f);
                Ui.Fill(t.rectTransform);
                t.rectTransform.offsetMin = new Vector2(4f, 3f);
                t.rectTransform.offsetMax = new Vector2(-4f, -3f);
            }
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.resizeTextForBestFit = true;
            t.resizeTextMinSize = 8;
            t.resizeTextMaxSize = t.fontSize;
        }

        // its X (and B): back where it was opened -- the room after a match, else the modes
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_CloseBoard(LeaderboardMultiplayerScreen self) { throw new InvalidOperationException("hook stub"); }
        static void CloseBoard(LeaderboardMultiplayerScreen self)
        {
            if (!session) { if (self != null) Orig_CloseBoard(self); return; }
            var mc = MultiplayerController.Instance();
            bool room = mc != null && mc.gameStatus == MultiGameStatus.LOBBY;
            MenuController.instance.showUI(room ? "MultiPlayerRoomScreen" : "MultiplayerSelectionModeScreen");
        }

        // ============================================================ the room's furniture
        // Along the sign's black band, under the world's name: every theme's own
        // card from the character select in a row, filling the band, the chosen
        // one in front between two arrows (the game's Play button, one turned
        // round), the others dimmed; player
        // 1's L and R slide the row round. At the bottom, the legend. The sign's
        // room-code, count and clock lines go (the row and the player list say
        // what they said); its title keeps the world's name.
        static Ui.Layer roomLayer;
        static RectTransform strip, legend, arrowL, arrowR;
        static readonly List<RectTransform> stripCards = new List<RectTransform>();
        static readonly List<CanvasGroup> stripFades = new List<CanvasGroup>();
        static float stripAt, stripTo;       // the row's scroll, in cards (unwrapped)
        static float cardPulse, cardW = 110f;
        // built at these sizes, then the whole row scaled so the cards fill the band
        // but for a thin black edge (Fill of its width across)
        const float CardH = 100f, CardGap = 16f, ArrowH = 66f, Front = 1.08f, Fill = 0.84f;

        static void Furnish(bool show)
        {
            if (!show) { if (roomLayer != null && roomLayer.Shown) roomLayer.Show(false); return; }
            var sign = SingletonMonobehaviour<MultiWaitingRoom3DTextInfoController>.Instance;
            SignLines(sign, false);
            if (roomLayer == null) roomLayer = new Ui.Layer("Room");
            if (legend == null)
            {
                var l = new List<string>();
                if (rules != Rules.Crown) { l.Add("L R"); l.Add("World"); }
                l.Add("Y"); l.Add("Character");
                if (Profiles.On) { l.Add("X"); l.Add("Profile"); }
                l.Add("B"); l.Add("Leave");
                // between the player list and the Session Wins / Play buttons
                legend = Ui.Hints(roomLayer.Rt, 26f, l.ToArray());
                Ui.Place(legend, new Vector2(0.5f, 0f), new Vector2(-8f, 16f));
            }
            bool worlds = rules != Rules.Crown;
            if (worlds && strip == null)
            {
                try { BuildStrip(); }
                catch (Exception e) { Loader.Report(e); }
            }
            if (strip != null)
            {
                if (strip.gameObject.activeSelf != worlds) strip.gameObject.SetActive(worlds);
                if (worlds) LayStrip(sign);
            }
            if (focusPlay && !MenuPad.Suspended && Loader.FrameCount >= MenuPad.HoldUntil && (Pads.AnyHeld() & Btn.A) == 0)
            {
                // back from a character select: player 1's A is Play again, not whatever it last touched
                focusPlay = false;
                var rc = UnityEngine.Object.FindObjectOfType<MultiPlayerRoomScreenController>();
                if (rc != null && rc.playButton != null && rc.playButton.gameObject.activeInHierarchy)
                    KlicktockInput.InputManager.UpdateEventSystemSelectedObject(rc.playButton.gameObject);
            }
            roomLayer.Show(true);
        }
        static bool focusPlay;

        // the room changed rules (another mode): its legend and row are made again
        static void Unfurnish()
        {
            if (legend != null) { UnityEngine.Object.Destroy(legend.gameObject); legend = null; }
        }

        static void SignLines(MultiWaitingRoom3DTextInfoController sign, bool on)
        {
            if (sign == null) return;
            if (sign.roomCodeTitleRootObj != null && sign.roomCodeTitleRootObj.activeSelf != on) sign.roomCodeTitleRootObj.SetActive(on);
            if (sign.codeRootObj != null && sign.codeRootObj.activeSelf != on) sign.codeRootObj.SetActive(on);
            if (sign.countDownRootObj != null && sign.countDownRootObj.activeSelf != on) sign.countDownRootObj.SetActive(on);
        }

        static void BuildStrip()
        {
            GameObject cardTemplate = null;
            foreach (var c in Resources.FindObjectsOfTypeAll<CreateUniverseCards>())
                if (c != null && c.FirstUniverseCard != null && c.gameObject.scene.IsValid()) { cardTemplate = c.FirstUniverseCard; break; }
            var rc = UnityEngine.Object.FindObjectOfType<MultiPlayerRoomScreenController>();
            if (cardTemplate == null) { Log.Line("local multiplayer: no theme card to copy"); return; }
            strip = Ui.Node(roomLayer.Rt, "Worlds");
            Ui.Place(strip, new Vector2(0.5f, 0.5f), Vector2.zero);
            strip.sizeDelta = Vector2.zero;
            var size = Ui.NaturalSize(cardTemplate, new Vector2(180f, 110f));
            stripCards.Clear();
            stripFades.Clear();
            float widest = 0f;
            foreach (var u in Maps())
            {
                var slot = Ui.Node(strip, "Slot " + u.name);
                Ui.Place(slot, new Vector2(0.5f, 0.5f), Vector2.zero);
                stripFades.Add(slot.gameObject.AddComponent<CanvasGroup>());
                var card = Ui.Copy(cardTemplate, slot, typeof(UniverseCard));
                card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
                card.sizeDelta = size;
                card.anchoredPosition = Vector2.zero;
                card.localRotation = Quaternion.identity;
                card.localScale = Vector3.one;
                var uc = card.GetComponentInChildren<UniverseCard>(true);
                if (uc != null)
                {
                    uc.SetupCard(u);
                    // the logo says it: no name label over the card, no event tag under it
                    NotificationServer.instance.removeObserver(uc.gameObject, "EventsAreEnabled");
                    if (uc.CardText != null) uc.CardText.gameObject.SetActive(false);
                    if (uc.eventPlugPanel != null) uc.eventPlugPanel.SetActive(false);
                }
                // sized, spaced and centred by its face (its pictures reach past its
                // layout box; its drop shadow hangs below the band's middle, as on a button)
                var face = card.Find("ButtonBG") as RectTransform;
                var vr = face != null ? Ui.RectIn(face, card) : Ui.DrawnRect(card);
                float k = CardH / Mathf.Max(1f, vr.height);
                card.localScale = new Vector3(k, k, 1f);
                card.anchoredPosition = -vr.center * k;
                slot.sizeDelta = new Vector2(vr.width * k, CardH);
                widest = Mathf.Max(widest, vr.width * k);
                stripCards.Add(slot);
            }
            cardW = widest > 1f ? widest : CardH * size.x / size.y;
            // the arrows: the room's own Play button, and one facing the other way
            if (rc != null && rc.playButton != null)
            {
                var bs = Ui.NaturalSize(rc.playButton.gameObject, new Vector2(140f, 90f));
                float k = ArrowH / bs.y;
                arrowR = Ui.Copy(rc.playButton.gameObject, strip);
                arrowL = Ui.Copy(rc.playButton.gameObject, strip);
                foreach (var a in new[] { arrowL, arrowR })
                {
                    a.anchorMin = a.anchorMax = a.pivot = new Vector2(0.5f, 0.5f);
                    a.sizeDelta = bs;
                    a.localRotation = Quaternion.identity;
                    a.localScale = new Vector3(a == arrowL ? -k : k, k, 1f);
                    foreach (var t in a.GetComponentsInChildren<UnityEngine.UI.Text>(true)) t.gameObject.SetActive(false);
                }
            }
            int n = Maps().Count;
            stripAt = stripTo = n > 0 ? worldIndex : 0;
            Log.Line("local multiplayer: " + n + " worlds on the sign");
        }

        // one step of the row (L/R): the short way round
        static void StripStep(int step) { stripTo += step; cardPulse = 1f; }

        static void LayStrip(MultiWaitingRoom3DTextInfoController sign)
        {
            // where: the middle of the sign's band (the code line's own text, below the
            // title), tilted as the sign is
            Vector2 pos = new Vector2(0f, -10f);
            float ang = 0f;
            var cam = sign != null ? sign.cameraWipe : null;
            var tm = sign != null && sign.codeRootObj != null ? sign.codeRootObj.GetComponentInChildren<TextMesh>(true) : null;
            if (cam != null && tm != null && cam.isActiveAndEnabled)
            {
                var at = tm.transform;
                Vector3 p = cam.WorldToScreenPoint(at.position);
                Vector3 q = cam.WorldToScreenPoint(at.position + at.right);
                Vector2 local;
                if (p.z > 0f && RectTransformUtility.ScreenPointToLocalPointInRectangle(Ui.Root, p, null, out local))
                {
                    pos = local;
                    float a = Mathf.Atan2(q.y - p.y, q.x - p.x) * Mathf.Rad2Deg;
                    if (Mathf.Abs(a) < 40f) ang = a;
                }
            }
            Vector2 mid;
            float bandAng, across;
            if (Band(sign, out mid, out bandAng, out across) && across > 40f)
            {
                strip.anchoredPosition = mid;
                strip.localEulerAngles = new Vector3(0f, 0f, bandAng);
                float s = across * Fill / CardH;
                strip.localScale = new Vector3(s, s, 1f);
            }
            else
            {
                strip.anchoredPosition = pos + StripNudge(ang);
                strip.localEulerAngles = new Vector3(0f, 0f, ang);
                strip.localScale = Vector3.one;
            }
            // the row: eased to the chosen world, wrapped round
            float dt = Time.unscaledDeltaTime;
            stripAt = Mathf.Lerp(stripAt, stripTo, Mathf.Clamp01(dt * 12f));
            if (Mathf.Abs(stripAt - stripTo) < 0.002f) stripAt = stripTo;
            cardPulse = Mathf.MoveTowards(cardPulse, 0f, dt / 0.2f);
            int n = stripCards.Count;
            float arrowW = arrowR != null ? Mathf.Abs(arrowR.sizeDelta.x * arrowR.localScale.x) : 0f;
            float inner = cardW * Front * 0.5f + 14f + arrowW + 14f + cardW * 0.5f;   // centre to the first neighbour's centre
            for (int i = 0; i < n; i++)
            {
                float d = i - stripAt;
                d = ((d % n) + n) % n;
                if (d > n * 0.5f) d -= n;
                float ad = Mathf.Abs(d);
                float x = ad <= 1f ? ad * inner : inner + (ad - 1f) * (cardW + CardGap);
                var c = stripCards[i];
                c.anchoredPosition = new Vector2(Mathf.Sign(d) * x, 0f);
                float k = 1f + (Front - 1f) * Mathf.Clamp01(1f - ad) + 0.06f * cardPulse * Mathf.Clamp01(1f - ad);
                c.localScale = new Vector3(k, k, 1f);
                stripFades[i].alpha = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01(ad));
            }
            float ax = cardW * Front * 0.5f + 14f + arrowW * 0.5f;
            if (arrowL != null) arrowL.anchoredPosition = new Vector2(-ax, 0f);
            if (arrowR != null) arrowR.anchoredPosition = new Vector2(ax, 0f);
        }

        // The band: the sign's black board (OverlayBottom), a unit quad whose
        // middle is the band's middle; about two thirds of its height shows black
        // (measured on screen: its edges are covered or faded). Its middle at the
        // screen's centre line, its slope, and its width across, in canvas units.
        static Transform bandBlack;
        static bool bandLogged;
        const float BandShows = 0.66f;
        static bool Band(MultiWaitingRoom3DTextInfoController sign, out Vector2 mid, out float ang, out float across)
        {
            mid = Vector2.zero; ang = 0f; across = 0f;
            if (sign == null) return false;
            if (bandBlack == null)
                foreach (var mr in sign.GetComponentsInChildren<MeshRenderer>(true))
                    if (mr.name == "OverlayBottom") { bandBlack = mr.transform; break; }
            if (bandBlack == null || !bandBlack.gameObject.activeInHierarchy) return false;
            var cam = bandBlack.GetComponentInParent<Camera>() ?? sign.cameraWipe;
            if (cam == null || !cam.isActiveAndEnabled) return false;
            float y0, yTop, yBottom, slope, s2;
            if (!LineAtCentre(cam, bandBlack, 0f, out y0, out slope) || !LineAtCentre(cam, bandBlack, 0.5f, out yTop, out s2) ||
                !LineAtCentre(cam, bandBlack, -0.5f, out yBottom, out s2)) return false;
            ang = Mathf.Atan(slope) * Mathf.Rad2Deg;
            mid = new Vector2(0f, y0);
            across = (yTop - yBottom) * Mathf.Cos(ang * Mathf.Deg2Rad) * BandShows;
            if (!bandLogged) { bandLogged = true; Log.Line("local multiplayer: the sign's band at " + mid + ", " + ang.ToString("F1") + " deg, " + across.ToString("F0") + " across"); }
            return Mathf.Abs(ang) < 40f;
        }

        // a line across a quad (local y) on the canvas: its height at x = 0, and its slope
        static bool LineAtCentre(Camera cam, Transform quad, float localY, out float y, out float slope)
        {
            y = slope = 0f;
            var s0 = cam.WorldToScreenPoint(quad.TransformPoint(new Vector3(-0.01f, localY, 0f)));
            var s1 = cam.WorldToScreenPoint(quad.TransformPoint(new Vector3(0.01f, localY, 0f)));
            Vector2 a, b;
            if (s0.z <= 0f || s1.z <= 0f) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Ui.Root, s0, null, out a)) return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Ui.Root, s1, null, out b)) return false;
            if (Mathf.Abs(b.x - a.x) < 1f) return false;
            slope = (b.y - a.y) / (b.x - a.x);
            y = a.y + (0f - a.x) * slope;
            return true;
        }

        // (no band found) the band's middle was 42 units below the code line's anchor, across the band
        static Vector2 StripNudge(float ang)
        {
            const float down = 42f;
            float r = ang * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(r) * down, -Mathf.Cos(r) * down);
        }

        // ------------------------------------------------------------ whose character select
        // Bottom left while a player picks: their number, big, in the pixel
        // lettering and their colour, and what their buttons do.
        static Ui.Layer chooserLayer;
        static int chooserShown = -1;
        static void ChooserUi(bool show)
        {
            if (!show) { if (chooserLayer != null && chooserLayer.Shown) chooserLayer.Show(false); return; }
            if (chooserLayer == null) chooserLayer = new Ui.Layer("Chooser");
            if (chooserShown != chooser)
            {
                chooserLayer.Clear();
                chooserShown = chooser;
                var box = Ui.Node(chooserLayer.Rt, "Who");
                Ui.Place(box, new Vector2(0f, 0f), new Vector2(30f, 22f));
                var vl = box.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
                vl.childAlignment = TextAnchor.LowerLeft;
                vl.spacing = 4f;
                vl.childControlWidth = vl.childControlHeight = true;
                vl.childForceExpandWidth = vl.childForceExpandHeight = false;
                var fit = box.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>();
                fit.horizontalFit = fit.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
                var row = Ui.Node(box, "Row");
                var hl = row.gameObject.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                hl.childAlignment = TextAnchor.MiddleLeft;
                hl.spacing = 16f;
                hl.childControlWidth = hl.childControlHeight = true;
                hl.childForceExpandWidth = hl.childForceExpandHeight = false;
                Ui.PlayerTag(row, chooser, 88);
                var cap = Ui.Label(row, "Choose your\ncharacter", Ui.Bold, 28, Color.white);
                cap.lineSpacing = 0.9f;
                Ui.Outlined(cap, 2f, 0f);
                Ui.Hints(box, 28f, "A", "Choose", "B", "Back", "L R", "Theme");
            }
            chooserLayer.Show(true);
        }
    }
}
