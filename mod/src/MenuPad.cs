// MenuPad.cs -- every menu driven by the controller, the way the game's own
// TV build does it, with what that build lacks filled in.
//
// The game carries an Apple TV / Fire TV controller UI: uGUI focus on every
// button with its own highlight (Animator "Highlighted" + a pulse), A presses
// the focused button, B presses the screen's back button (AndroidBackButton),
// and invisible focus rows turn the character carousel and the theme strip
// into rows the d-pad walks. It is switched on by the IL patch of
// InputManager.IsControllerInUse (source/dcr_ilpatch.c). Here:
// * touch keeps working: the game turns the touch input module off whenever a
//   controller is in use; it stays on (it never sees the pad);
// * focus moves when a direction is pressed (the game moved on release), and
//   repeats while it is held;
// * every screen opens with a sensible button focused (only 11 of the game's
//   108 screens name one; the rest waited 2 s and took any button);
// * rows the game made Horizontal- or Vertical-only (the title screen's top
//   and middle buttons, banners, the multiplayer screens) navigate freely;
// * character select: A on the carousel plays the figurine in the middle (or
//   goes to Buy), L/R (and ZL/ZR) change theme, Y plays a random figurine, X
//   opens its info; the carousel rows get the d-pad whenever they are focused
//   (the game's own flag missed a row activated by a theme change);
// * B presses the back button of the screen on top. The game sent B to the
//   first back button ever registered, which can sit on a panel that has moved
//   off screen (the leaderboard ignored B); where the game had no back button:
//   pause (resumes), the photo, the notification question (No), and the two
//   screens where Info won over Close;
// * lists (the leaderboards, their rewards): the left stick scrolls the list
//   on screen (the D-pad still walks the buttons), the right stick too, and a
//   button focused inside a list is scrolled into view.
// Config: [controls] menu_controls.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KlicktockInput;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DcrMod
{
    public static class MenuPad
    {
        public static bool On;
        // the port's menu controls wait until this frame (a screen just opened on a press)
        public static int HoldUntil;
        // local multiplayer's lobby takes the other controllers; it can also hold the menus
        public static bool Suspended;

        public static void Install()
        {
            if (Native.Config("controls.menu_controls", 1) == 0) return;
            On = true;
            var M = typeof(MenuPad);
            Hook.Install(typeof(GamePadReceiver), "setUnityInputModuleAvailability", new[] { typeof(bool) }, M, "KeepModules", null);
            Hook.Install(typeof(GamePadReceiver), "Action", new[] { typeof(ControllerActionType), typeof(float) }, M, "PadAction", null);
            Hook.Install(typeof(AndroidBackButton), "Back", new[] { typeof(bool) }, M, "Back", "Orig_Back");
            Loader.OnAfterInput(Frame);
        }

        // ------------------------------------------------------------ hooks
        static void KeepModules(GamePadReceiver self, bool enabled)
        {
            foreach (var m in UnityEngine.Object.FindObjectsOfType<StandaloneInputModule>())
                m.enabled = true;
        }

        // GamePadReceiver.Action: the game's menu navigation, moving on the press
        static bool PadAction(GamePadReceiver self, ControllerActionType t, float value)
        {
            if (self.inGame || Suspended) return false;
            switch (t)
            {
                case ControllerActionType.Up:
                case ControllerActionType.Down:
                    // with a list on screen the stick scrolls it (Frame); the D-pad walks
                    if (listShown && (Pads.AnyHeld() & (Btn.Up | Btn.Down)) == 0) return true;
                    if (value == 1f) Move(t);
                    return true;
                case ControllerActionType.Left:
                case ControllerActionType.Right:
                    if (value == 1f) Move(t);
                    return true;
                case ControllerActionType.Action:
                    // a screen just changed on this press (a character picked for a seat): not a press on the next one
                    if (Loader.FrameCount < HoldUntil) return true;
                    if (value == 1f) return false;
                    var gc = GameController.instance;
                    if (gc != null && gc.pauseController != null && gc.pauseController.isPaused())
                        gc.pauseController.Unpause();
                    return true;
            }
            return false;
        }

        static void Move(ControllerActionType t)
        {
            var es = EventSystem.current;
            if (es == null || es.currentSelectedGameObject == null) return;
            var d = new AxisEventData(es);
            d.moveDir = t == ControllerActionType.Up ? MoveDirection.Up : t == ControllerActionType.Down ? MoveDirection.Down
                      : t == ControllerActionType.Left ? MoveDirection.Left : MoveDirection.Right;
            ExecuteEvents.Execute(es.currentSelectedGameObject, d, ExecuteEvents.moveHandler);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Orig_Back(AndroidBackButton self, bool exit) { throw new InvalidOperationException("hook stub"); }
        static int backFrame = -1;
        static bool backResult;
        // One B press is one step back. The game calls Back on its own schedule
        // and Frame acts on the release too: without this a press could be taken
        // twice on neighbouring frames (the lobby, then the modes, then the title).
        static bool bLatched;
        static bool Back(AndroidBackButton self, bool exit)
        {
            if (Suspended) return true; // the lobby's B is its own
            if (backFrame != Loader.FrameCount)
            {
                bool again = bLatched;
                backFrame = Loader.FrameCount;
                bLatched = true;
                if (again) backResult = true; // the same press, seen again
                else
                {
                    var gc = GameController.instance;
                    bool menus = gc == null || !gc.isGameActive || !gc.isGameStarted || LocalMP.ResultsUp;
                    backResult = OverrideBack() || (menus && BackOfTopScreen(self));
                }
            }
            return backResult || Orig_Back(self, exit);
        }

        // The screen on top's own back button, when the game picked another one
        // (it asks the back buttons in the order they were registered and the
        // first takes it) or none. false: the game's choice stands.
        static bool BackOfTopScreen(AndroidBackButton picked)
        {
            var top = TopGroup();
            if (top == null) return false;
            if (picked != null && Under(picked.transform, top)) return false;
            // the back button of the panel drawn on top (a screen can share its group
            // with the one under it: the daily missions' gift screen sits over the
            // missions, whose X closed both); in it, a back arrow before a close X
            Button b = null;
            PanelMover bp = null;
            foreach (var p in top.panels)
            {
                if (!Showing(p)) continue;
                Button mine = null;
                foreach (var ab in p.GetComponentsInChildren<AndroidBackButton>(false))
                {
                    var bb = ab.GetComponent<Button>();
                    if (bb == null || !bb.IsInteractable()) continue;
                    if (mine == null || bb.name == "BackButton") mine = bb;
                }
                if (mine == null) continue;
                if (bp == null || Above(p, bp)) { b = mine; bp = p; }
            }
            if (b == null) return false;
            Press(b);
            return true;
        }

        // drawn over the other (siblings: the later one; otherwise the group's later panel)
        static bool Above(PanelMover a, PanelMover b)
        {
            if (a.transform.parent == b.transform.parent) return a.transform.GetSiblingIndex() > b.transform.GetSiblingIndex();
            return true;
        }

        // ------------------------------------------------------------ the frame
        static PanelGroup lastGroup;
        static int groupFrames;
        static readonly HashSet<int> navFixed = new HashSet<int>();
        static bool figFocus, uniFocus;
        static CharacterSelectionUniverseScrollHorizontal strip;
        static int stripSearchAt, glideTo = -1;
        static float stripSpeed;
        const float GlideSpeed = 14f; // the snap's easing rate (the game's 5 took a second)

        static void EndGlide()
        {
            if (strip != null && glideTo >= 0) strip.snapSpeed = stripSpeed;
            glideTo = -1;
        }

        // the left stick's left/right, as the game's input delivers it
        static void StickStep(int step)
        {
            var t = step < 0 ? ControllerActionType.Left : ControllerActionType.Right;
            if (InputManager.inst == null) return;
            InputManager.inst.Action(t, 1f);
            InputManager.inst.Action(t, 0f);
        }
        static CharacterSelectionSwipeNew lastRow;
        static bool stripPending;

        static void Frame()
        {
            if (Suspended) return;
            var pad = Pads.Game;
            var gc = GameController.instance;
            var es = EventSystem.current;
            if (es == null) return;
            if (gc == null)
            {
                BootScreen(es);
                return;
            }
            // B where no back button answered (the game calls Back on the release)
            if ((pad.Up & Btn.B) != 0 && backFrame != Loader.FrameCount && !bLatched)
            {
                backFrame = Loader.FrameCount;
                bLatched = true;
                if (!OverrideBack()) BackOfTopScreen(null);
            }
            // the press is spent once B is up and the game has had its say
            if (bLatched && (Pads.AnyHeld() & Btn.B) == 0 && Loader.FrameCount - backFrame > 8) bLatched = false;
            bool menus = !gc.isGameActive || !gc.isGameStarted || LocalMP.ResultsUp;
            listShown = false;
            if (!menus) { figFocus = uniFocus = false; lastRow = null; tutorialPlayHidden = false; FocusShade(null); return; }
            if (WipeController.instance != null && WipeController.instance.isWipeInProgress()) return;

            PanelGroup top = TopGroup();
            if (top != lastGroup) { lastGroup = top; groupFrames = 0; }
            groupFrames++;
            if (top == null) return;

            FixNavigation(top);
            GameObject sel = es.currentSelectedGameObject;
            if (!IsValid(sel, top))
            {
                bool gameDoesIt = top.selectablesToFocus != null && top.selectablesToFocus.Length > 0 && groupFrames < 4;
                if (!gameDoesIt)
                {
                    Selectable d = DefaultFor(top);
                    if (d != null)
                    {
                        InputManager.UpdateEventSystemSelectedObject(d.gameObject);
                        sel = d.gameObject;
                    }
                }
            }

            ScrollLists(top, sel, pad);

            // held directions repeat (the carousel row scrolls by itself while held)
            uint rep = pad.NavRepeat;
            if (listShown && (pad.Held & (Btn.Up | Btn.Down)) == 0) rep &= ~(Btn.Up | Btn.Down); // the stick scrolls
            if (rep != 0 && InputManager.inst != null && !(sel != null && sel.name == "Character Move Focus"))
            {
                var t = (rep & Btn.Up) != 0 ? ControllerActionType.Up : (rep & Btn.Down) != 0 ? ControllerActionType.Down
                      : (rep & Btn.Left) != 0 ? ControllerActionType.Left : ControllerActionType.Right;
                InputManager.inst.Action(t, 1f);
                InputManager.inst.Action(t, 0f);
            }

            string g = top.name == null ? "" : top.name.ToLowerInvariant();
            TutorialPlay(gc, g);
            FocusShade(sel);
            if (Loader.FrameCount < HoldUntil) return;
            if (g == "characterselector" || g == "characterselectorcredits")
                CharacterSelect(top, sel, pad, g == "characterselectorcredits");
            else { figFocus = uniFocus = false; lastRow = null; EndGlide(); }
            if (g == "start of game" && pad.Pressed(Btn.Y))
                gc.ButtonCharacterSelect();
        }

        // The title's event box, which the game draws no differently with the
        // focus: its colour (the event's own, which EventStartButton paints on its
        // background and interior) goes darker while it has the focus, as the
        // game's own buttons do, and back to exactly that colour after.
        static EventStartButton shaded;
        static Color shadedFrom;
        static void FocusShade(GameObject sel)
        {
            EventStartButton want = sel != null && sel.name == "Event Button" && sel.activeInHierarchy ? sel.GetComponent<EventStartButton>() : null;
            if (want == shaded) return;
            if (shaded != null) Paint(shaded, shadedFrom);
            shaded = want;
            if (shaded == null) return;
            shadedFrom = shaded.buttonBackground != null ? shaded.buttonBackground.canvasRenderer.GetColor() : Color.white;
            Paint(shaded, new Color(shadedFrom.r * 0.62f, shadedFrom.g * 0.62f, shadedFrom.b * 0.62f, shadedFrom.a));
        }

        static void Paint(EventStartButton b, Color c)
        {
            if (b == null) return;
            if (b.buttonBackground != null) b.buttonBackground.canvasRenderer.SetColor(c);
            if (b.buttonInterior != null) b.buttonInterior.canvasRenderer.SetColor(c);
        }

        // The first run's tutorial puts its own big Play button (a controller-only
        // one) at the bottom of the screen, over every menu: the tutorial never
        // expected another screen before the first hop, but Y (character select)
        // opens one. It is hidden away from the title and back on it.
        static bool tutorialPlayHidden;
        static void TutorialPlay(GameController gc, string group)
        {
            var t = gc.firstTimeTutorial;
            if (t == null || t.buttonPlayForController == null) return;
            bool title = group == "start of game" || group == "startofgametv";
            var b = t.buttonPlayForController.gameObject;
            if (!title && b.activeSelf && t.gameObject.activeInHierarchy)
            {
                b.SetActive(false);
                tutorialPlayHidden = true;
            }
            else if (title && tutorialPlayHidden)
            {
                tutorialPlayHidden = false;
                if (t.gameObject.activeInHierarchy && !t.hasTutorialPlayStarted) b.SetActive(true);
            }
        }

        // the boot scene's permission question: nothing focuses it off a TV
        static void BootScreen(EventSystem es)
        {
            if (es.currentSelectedGameObject != null && es.currentSelectedGameObject.activeInHierarchy) return;
            if (Loader.FrameCount % 15 != 0) return;
            var go = GameObject.Find("GameSceneBootUI/BootPermissionsOverlay/Popup Contents/Window/PlayButton");
            if (go != null && go.activeInHierarchy)
                es.SetSelectedGameObject(go);
        }

        // ------------------------------------------------------------ groups
        static bool Showing(PanelMover p)
        {
            return p != null && p.gameObject.activeInHierarchy &&
                   (p.panelState == PanelMoverState.onscreen || p.panelState == PanelMoverState.movingOnscreen);
        }

        public static PanelGroup TopGroup()
        {
            var mc = MenuController.instance;
            if (mc == null || mc.panelHelper == null) return null;
            var ph = mc.panelHelper;
            var g = ph.currentShowingGroup;
            bool shown = false;
            if (g != null && g.panels != null)
                foreach (var p in g.panels)
                    if (Showing(p)) { shown = true; break; }
            // the TV title sits outside the groups (and stays up under a screen
            // opened over it, as the multiplayer room is after a match): it is on
            // top only while the title's own group is the current one
            bool title = g == null || g.name == null || g.name.ToLowerInvariant() == "start of game";
            var tv = ph.panelWithName("TVStartOfGame");
            if (title && Showing(tv)) return ph.groupWithName("StartOfGameTV");
            return shown ? g : null;
        }

        static bool Under(Transform t, PanelGroup g)
        {
            foreach (var p in g.panels)
                if (p != null && Showing(p) && t.IsChildOf(p.transform)) return true;
            return false;
        }

        static bool IsValid(GameObject sel, PanelGroup g)
        {
            if (sel == null || !sel.activeInHierarchy) return false;
            var s = sel.GetComponent<Selectable>();
            if (s == null || !s.IsInteractable()) return false;
            if (sel.name == "Hack") return false;
            return Under(sel.transform, g);
        }

        static bool Usable(Selectable s)
        {
            return s != null && s.gameObject.activeInHierarchy && s.IsInteractable() && s.enabled && s.name != "Hack" &&
                   s.name != "Background" && s.name != "Backing" && s.navigation.mode != Navigation.Mode.None;
        }

        static readonly string[] preferred = {
            "Controller Play Button", "Character Move Focus", "NextButton", "PrizeButton", "PixelButton", "GiftButton", "Gift Button",
            "BuyButton", "GetReward", "ChallengePlayButton", "MissionPlayButton", "PlayButton", "Play Button", "PlayButton (1)",
            "OKButton", "OkButton", "Confirm Button", "Reconnect", "AgreeButton", "Current", "Top100", "Friend", "MuteMusic Toggle",
            "QuickJoin", "CreateRoom", "CollectionCoin", "Next", "No Button" };

        static Selectable DefaultFor(PanelGroup g)
        {
            var all = new List<Selectable>();
            foreach (var p in g.panels)
            {
                if (!Showing(p)) continue;
                foreach (var s in p.GetComponentsInChildren<Selectable>(false))
                    if (Usable(s)) all.Add(s);
            }
            if (all.Count == 0) return null;
            foreach (string n in preferred)
                foreach (var s in all)
                    if (s.name == n) return s;
            foreach (var s in all)
                if (s is Button && s.GetComponent<AndroidBackButton>() == null) return s;
            return all[0];
        }

        static void FixNavigation(PanelGroup g)
        {
            foreach (var p in g.panels)
            {
                if (p == null || !Showing(p)) continue;
                int id = p.GetInstanceID();
                if (navFixed.Contains(id)) continue;
                navFixed.Add(id);
                foreach (var s in p.GetComponentsInChildren<Selectable>(true))
                {
                    var n = s.navigation;
                    if (n.mode == Navigation.Mode.Horizontal || n.mode == Navigation.Mode.Vertical)
                    {
                        n.mode = Navigation.Mode.Automatic;
                        s.navigation = n;
                    }
                }
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        static Selectable FindInGroup(PanelGroup g, string name)
        {
            foreach (var p in g.panels)
            {
                if (!Showing(p)) continue;
                var t = FindDeep(p.transform, name);
                if (t != null && t.gameObject.activeInHierarchy) return t.GetComponent<Selectable>();
            }
            return null;
        }

        public static void Press(Selectable s)
        {
            if (s != null && s.gameObject.activeInHierarchy && s.IsInteractable())
                ExecuteEvents.Execute(s.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
        }

        // ------------------------------------------------------------ lists
        static bool listShown;
        static GameObject lastSel;

        // the largest vertical list on the screen on top that has more than it shows
        static ScrollRect ListIn(PanelGroup g)
        {
            ScrollRect best = null;
            float bestH = 0f;
            foreach (var p in g.panels)
            {
                if (!Showing(p)) continue;
                foreach (var sr in p.GetComponentsInChildren<ScrollRect>(false))
                {
                    if (!sr.vertical || sr.content == null || !sr.enabled) continue;
                    var view = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
                    float h = view.rect.height;
                    if (sr.content.rect.height <= h + 1f || h <= bestH) continue;
                    best = sr; bestH = h;
                }
            }
            return best;
        }

        static PanelGroup listGroup;
        static ScrollRect listCached;
        static void ScrollLists(PanelGroup top, GameObject sel, Pad pad)
        {
            // looked for when the screen changes, and twice a second (lists fill in late)
            if (top != listGroup || Loader.FrameCount % 30 == 0) { listGroup = top; listCached = ListIn(top); }
            var sr = listCached;
            if (sr != null && !sr.isActiveAndEnabled) sr = null;
            listShown = sr != null;
            if (sr == null) { lastSel = sel; return; }
            var view = sr.viewport != null ? sr.viewport : (RectTransform)sr.transform;
            float room = sr.content.rect.height - view.rect.height;
            // a button focused inside the list: brought into view
            if (sel != lastSel && sel != null && sel.transform.IsChildOf(sr.content))
            {
                var rt = sel.transform as RectTransform;
                if (rt != null)
                {
                    Vector3 local = sr.content.InverseTransformPoint(rt.position); // content space, y down from its pivot
                    float top0 = sr.content.rect.yMax;
                    float fromTop = top0 - local.y;                                  // how far down the content it is
                    float shownTop = (1f - sr.verticalNormalizedPosition) * room;
                    if (fromTop - rt.rect.height < shownTop || fromTop + rt.rect.height > shownTop + view.rect.height)
                        sr.verticalNormalizedPosition = Mathf.Clamp01(1f - (fromTop - view.rect.height * 0.5f) / room);
                }
            }
            lastSel = sel;
            // the sticks: the left one while the D-pad is not held, the right one always
            float v = 0f;
            if ((pad.Held & (Btn.Up | Btn.Down)) == 0 && Mathf.Abs(pad.LY) > 0.2f) v = pad.LY;
            if (Mathf.Abs(pad.RY) > 0.2f) v = pad.RY;
            if (v == 0f) return;
            float speed = 1100f * Mathf.Sign(v) * (Mathf.Abs(v) - 0.2f) / 0.8f; // canvas units a second at full tilt
            sr.velocity = Vector2.zero;
            sr.verticalNormalizedPosition = Mathf.Clamp01(sr.verticalNormalizedPosition + speed * Time.unscaledDeltaTime / room);
        }

        // ------------------------------------------------------------ character select
        static void CharacterSelect(PanelGroup top, GameObject sel, Pad pad, bool credits)
        {
            bool fig = sel != null && sel.name == "Character Move Focus";
            bool uni = sel != null && sel.name == "Universe Move Focus";
            var gc = GameController.instance;
            CharacterSelectionSwipeNew row = null;
            if (!credits && gc.characterMeshes != null) row = gc.characterMeshes.GetCurrentRow();
            else if (credits)
            {
                var cr = UnityEngine.Object.FindObjectOfType<CharacterSelectionCreditsRowController>();
                if (cr != null) row = cr.GetCurrentRow();
            }
            if (fig != figFocus || uni != uniFocus || row != lastRow || stripPending)
            {
                figFocus = fig; uniFocus = uni; lastRow = row;
                try { NotificationServer.instance.postNotification("FigurineCarouselSelection", fig); }
                catch (Exception e) { Loader.Report(e); }
                // the theme strip directly: through the notification, one strip whose
                // cards were not built yet threw and the others never heard it
                stripPending = false;
                foreach (var u in UnityEngine.Object.FindObjectsOfType<CharacterSelectionUniverseScrollHorizontal>())
                {
                    if (!u.initialized || u.universeCards == null || u.universeCards.Count < u.cardCount) { stripPending = true; continue; }
                    try { u.UpdateGamepadEnable(uni); }
                    catch (Exception e) { Loader.Report(e); }
                }
            }
            // A on the carousel: play the figurine in the middle, or go to Buy
            if (fig && pad.Pressed(Btn.A))
            {
                var play = FindInGroup(top, "PlayButton");
                var buy = FindInGroup(top, "BuyButton");
                if (play != null && play.IsInteractable() && play.gameObject.activeInHierarchy) Press(play);
                else if (buy != null && buy.IsInteractable() && buy.gameObject.activeInHierarchy) InputManager.UpdateEventSystemSelectedObject(buy.gameObject);
            }
            // L/R, ZL/ZR: the next theme. With the thumbnails focused, the left
            // stick's own step. From the characters the strip glides there itself --
            // the game's own touch snap, sped up -- with the focus left where it is:
            // moving it up and back played the focus sound twice, dimmed the
            // characters and raised the theme card each time.
            if (strip == null || !strip.gameObject.activeInHierarchy || !strip.initialized)
            {
                strip = null;
                // a scene search every frame cost the character select its frame rate
                if (Loader.FrameCount >= stripSearchAt)
                {
                    stripSearchAt = Loader.FrameCount + 30;
                    foreach (var u in UnityEngine.Object.FindObjectsOfType<CharacterSelectionUniverseScrollHorizontal>())
                        if (u.gameObject.activeInHierarchy && u.initialized) { strip = u; break; }
                }
            }
            if (pad.Pressed(Btn.L | Btn.ZL | Btn.R | Btn.ZR) && strip != null)
            {
                int step = pad.Pressed(Btn.L | Btn.ZL) ? -1 : 1;
                if (uni) StickStep(step);
                else if (fig && !strip.isGamepadInputAllowed)
                {
                    int from = glideTo >= 0 && strip.isSnapping ? glideTo : strip.CurrentCard();
                    int to = Mathf.Clamp(from + step, 0, strip.cardCount - 1);
                    if (to != from && to < strip.positions.Count)
                    {
                        if (glideTo < 0) stripSpeed = strip.snapSpeed;
                        strip.snapSpeed = GlideSpeed;
                        strip.currentCard = to;
                        strip.snapTarget = strip.positions[to];
                        strip.isSnapping = true; // it posts the theme change once it settles
                        glideTo = to;
                    }
                }
            }
            if (glideTo >= 0 && (strip == null || !strip.isSnapping)) EndGlide();
            if (LocalMP.ChoosingFigurine) return; // a multiplayer seat's pick: Play only
            if (pad.Pressed(Btn.Y)) Press(FindInGroup(top, "RandomButton"));
            if (pad.Pressed(Btn.X)) Press(FindInGroup(top, "InfoButton"));
        }

        // ------------------------------------------------------------ B
        static bool OverrideBack()
        {
            var gc = GameController.instance;
            if (gc == null) return false;
            if (gc.newPolaroid != null && gc.newPolaroid.gameObject.activeInHierarchy)
            {
                gc.newPolaroid.Close();
                return true;
            }
            if (gc.pauseController != null && gc.pauseController.isPaused())
            {
                gc.pauseController.Unpause();
                return true;
            }
            var top = TopGroup();
            if (top == null || top.name == null) return false;
            if (LocalMP.Back(top.name)) return true;
            // the daily missions card turned to its prize info ("6 = Exclusive
            // Figurine", "Chance to win"): B turns it back, as A on its back arrow
            // (the Info button) does -- the card's X, which B pressed, closed the missions
            foreach (var p in top.panels)
            {
                var dm = p != null && Showing(p) ? p.GetComponent<DailyMissionsPanel>() : null;
                if (dm == null || !dm.showingSide2 || dm.InfoButton == null || !dm.InfoButton.gameObject.activeInHierarchy) continue;
                if (!dm.transitioningSides) Press(dm.InfoButton);
                return true;
            }
            switch (top.name.ToLowerInvariant())
            {
                case "coinstopixelsshop":
                    {
                        var c = FindInGroup(top, "CloseButton");
                        if (c == null) return false;
                        Press(c);
                        return true;
                    }
                case "event interstitial":
                    {
                        var c = FindInGroup(top, "CloseButton");
                        if (c == null) return false;
                        Press(c);
                        return true;
                    }
                case "notification pop up screen":
                    {
                        var c = FindInGroup(top, "No Button");
                        if (c == null) return false;
                        Press(c);
                        return true;
                    }
            }
            return false;
        }
    }
}
