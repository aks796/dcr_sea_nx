// Ui.cs -- the port's own screen furniture (the multiplayer room's legend and
// world card, the badge of the player choosing a figurine, the pause screen),
// drawn the way the game draws its own: its two fonts -- UtahOTS-Bold for
// words, EditUndoBRK (the pixel lettering of its scores and 3D signs) for
// player numbers -- outlined like its labels, player numbers in the colours
// its multiplayer gives them, and controller buttons as the Switch draws
// them (tools/mod/make_button_icons.py: the Sonic racing port's drawing) on
// a dark pill. It all sits on one
// overlay canvas above the game's, 1280x720 units, that takes no touches;
// each screen's furniture is a Layer that fades in and out.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DcrMod
{
    public static class Ui
    {
        static Canvas canvas;
        static Font bold, pixel;
        static Sprite disc;
        static readonly List<Layer> layers = new List<Layer>();

        public static void Install()
        {
            Loader.OnFrame(Tick);
            // a new scene: every layer goes at once (its first frames run none of
            // the port's frame work, so a fade would stand still over it); the
            // screens that want theirs show it again
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (a, b) => HideAll();
        }

        public static void HideAll()
        {
            for (int i = 0; i < layers.Count; i++)
            {
                try { layers[i].Hide(); }
                catch (Exception e) { Loader.Report(e); }
            }
        }

        public static RectTransform Root
        {
            get
            {
                if (canvas == null)
                {
                    var go = new GameObject("DcrOverlay", typeof(RectTransform));
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    canvas = go.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    canvas.sortingOrder = 20000;
                    var sc = go.AddComponent<CanvasScaler>();
                    sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    sc.referenceResolution = new Vector2(1280f, 720f);
                    sc.matchWidthOrHeight = 0.5f;
                }
                return (RectTransform)canvas.transform;
            }
        }

        // ------------------------------------------------------------ the game's look
        public static Font Bold { get { FindFonts(); return bold; } }
        public static Font Pixel { get { FindFonts(); return pixel ?? bold; } }

        static void FindFonts()
        {
            if (bold != null && pixel != null) return;
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null) continue;
                if (bold == null && f.name == "UtahOTS-Bold") bold = f;
                else if (pixel == null && f.name == "EditUndoBRK-Regular") pixel = f;
            }
            if (bold == null)
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Text>())
                    if (t.font != null) { bold = t.font; break; }
        }

        static readonly Color[] fallback = {
            new Color(0.91f, 0.16f, 0.13f), new Color(0.98f, 0.62f, 0.10f), new Color(0.27f, 0.75f, 0.20f), new Color(0.18f, 0.55f, 0.95f) };

        // the colour the game's multiplayer gives a player number (the room's list, the flags over their heads)
        public static Color Player(int seat)
        {
            try
            {
                var mc = MultiplayerController.Instance();
                if (mc != null && mc.listColorBG != null && mc.listColorBG.Length > 0) { var c = mc.GetBGColorBasedNetID(seat); c.a = 1f; return c; }
            }
            catch (Exception) { }
            return fallback[Mathf.Clamp(seat, 0, fallback.Length - 1)];
        }

        // a white disc (anti-aliased), sliced: a circle when square, a pill when wide
        static Sprite Disc()
        {
            if (disc != null) return disc;
            const int n = 64;
            float r = n / 2f;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            disc = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                                 new Vector4(r - 1f, r - 1f, r - 1f, r - 1f));
            disc.name = "DcrDisc";
            return disc;
        }

        public static Image Round(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = Disc();
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        // ------------------------------------------------------------ pieces
        public static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // anchored at a point of the parent (0..1 each way), pivot the same, `pos` units from it
        public static void Place(RectTransform rt, Vector2 anchor, Vector2 pos)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
        }

        public static void Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        public static Text Label(Transform parent, string s, Font font, int size, Color color)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.color = color;
            t.text = s;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        // the game's lettering: a dark outline, and a drop below it for the pixel font's 3D look
        public static void Outlined(Text t, float width, float drop)
        {
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0f, 0f, 0f, 0.9f);
            o.effectDistance = new Vector2(width, -width);
            if (drop <= 0f) return;
            var s = t.gameObject.AddComponent<Shadow>();
            s.effectColor = new Color(0f, 0f, 0f, 0.9f);
            s.effectDistance = new Vector2(0f, -drop);
        }

        // a player's number, "P2", in the pixel lettering and their colour
        public static Text PlayerTag(Transform parent, int seat, int size)
        {
            var t = Label(parent, "P" + (seat + 1), Pixel, size, Player(seat));
            Outlined(t, Mathf.Max(2f, size / 22f), Mathf.Max(2f, size / 11f));
            return t;
        }

        // the buttons' pictures (ButtonIconsData.cs: one row, a PNG)
        static Texture2D iconTex;
        static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
        static Sprite Icon(string button)
        {
            Sprite sp;
            if (icons.TryGetValue(button, out sp)) return sp;
            int i = Array.IndexOf(ButtonIconsData.Names, button);
            if (i < 0) return null;
            if (iconTex == null)
            {
                iconTex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                iconTex.LoadImage(Convert.FromBase64String(ButtonIconsData.Png));
                iconTex.wrapMode = TextureWrapMode.Clamp;
                iconTex.filterMode = FilterMode.Trilinear;
                iconTex.name = "DcrButtonIcons";
            }
            sp = Sprite.Create(iconTex, new Rect(ButtonIconsData.X[i], 0, ButtonIconsData.W[i], ButtonIconsData.Height), new Vector2(0.5f, 0.5f), 100f);
            sp.name = "Button " + button;
            icons[button] = sp;
            return sp;
        }

        // a controller button, `size` units high: A B X Y + - L R ZL ZR
        public static RectTransform Glyph(Transform parent, string button, float size)
        {
            var sp = Icon(button);
            float w = sp != null ? size * sp.rect.width / sp.rect.height : size;
            var go = new GameObject("Glyph " + button, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(w, size);
            var img = go.AddComponent<Image>();
            img.sprite = sp;
            img.preserveAspect = true;
            img.raycastTarget = false;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = w;
            le.minHeight = le.preferredHeight = size;
            return rt;
        }

        // A copy of one of the game's own UI pieces (a theme card, a button),
        // lifeless: made under a switched-off holder so none of its scripts wake
        // up, its animators, buttons and scripts off (but `keep`), then moved.
        static Transform holder;
        public static RectTransform Copy(GameObject template, Transform parent, params Type[] keep)
        {
            if (holder == null)
            {
                var h = Node(Root, "DcrCopies");
                h.gameObject.SetActive(false);
                holder = h;
            }
            var go = UnityEngine.Object.Instantiate(template, holder, false);
            go.SetActive(true);
            foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (b is Graphic || b is Mask || b is BaseMeshEffect || b is LayoutElement || b is LayoutGroup || b is ContentSizeFitter) continue;
                if (Array.IndexOf(keep, b.GetType()) >= 0) continue;
                b.enabled = false;
            }
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // what a piece draws, in its own units: every visible picture's rectangle together
        public static Rect DrawnRect(RectTransform root)
        {
            bool any = false;
            float x0 = 0f, y0 = 0f, x1 = 0f, y1 = 0f;
            var c = new Vector3[4];
            foreach (var g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (!g.enabled || g.color.a < 0.02f || !Visible(g.transform, root)) continue;
                g.rectTransform.GetWorldCorners(c);
                for (int i = 0; i < 4; i++)
                {
                    var p = root.InverseTransformPoint(c[i]);
                    if (!any) { x0 = x1 = p.x; y0 = y1 = p.y; any = true; }
                    else { x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y); }
                }
            }
            return any && x1 - x0 > 1f && y1 - y0 > 1f ? Rect.MinMaxRect(x0, y0, x1, y1) : root.rect;
        }

        // one piece's rectangle in another's units
        public static Rect RectIn(RectTransform piece, RectTransform root)
        {
            var c = new Vector3[4];
            piece.GetWorldCorners(c);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                var p = root.InverseTransformPoint(c[i]);
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
            }
            return x1 - x0 > 1f && y1 - y0 > 1f ? Rect.MinMaxRect(x0, y0, x1, y1) : root.rect;
        }

        static bool Visible(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        // a copied piece's natural size: its layout's, else its rectangle's
        public static Vector2 NaturalSize(GameObject template, Vector2 fallback)
        {
            var le = template.GetComponent<LayoutElement>();
            var rt = (RectTransform)template.transform;
            float w = le != null && le.preferredWidth > 1f ? le.preferredWidth : rt.rect.width > 1f ? rt.rect.width : fallback.x;
            float h = le != null && le.preferredHeight > 1f ? le.preferredHeight : rt.rect.height > 1f ? rt.rect.height : fallback.y;
            return new Vector2(w, h);
        }

        // Button hints on a dark pill, as pairs: Hints(parent, 30, "A", "Join", "L R", "Theme")
        // (several buttons in one pair are separated by spaces; no buttons: a note).
        public static RectTransform Hints(Transform parent, float size, params string[] pairs)
        {
            var bg = Round(parent, "Hints", new Color(0f, 0f, 0f, 0.62f));
            var rt = bg.rectTransform;
            var hl = bg.gameObject.AddComponent<HorizontalLayoutGroup>();
            int v = Mathf.RoundToInt(size * 0.28f);
            hl.padding = new RectOffset(Mathf.RoundToInt(size * 0.5f), Mathf.RoundToInt(size * 0.75f), v, v);
            hl.spacing = size * 0.22f;
            hl.childAlignment = TextAnchor.MiddleCenter;
            hl.childControlWidth = hl.childControlHeight = true;
            hl.childForceExpandWidth = hl.childForceExpandHeight = false;
            var fit = bg.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (i > 0) Gap(rt, size * 0.6f);
                // no buttons: a note (who is playing), in the game's highlight yellow
                bool note = string.IsNullOrEmpty(pairs[i]);
                if (!note) foreach (var b in pairs[i].Split(' ')) Glyph(rt, b, size);
                var t = Label(rt, pairs[i + 1], Bold, Mathf.RoundToInt(size * 0.8f), note ? new Color(1f, 0.84f, 0.2f) : Color.white);
                t.alignment = TextAnchor.MiddleLeft;
            }
            return rt;
        }

        public static void Gap(Transform parent, float w)
        {
            var g = Node(parent, "Gap");
            var le = g.gameObject.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = w;
        }

        // ------------------------------------------------------------ layers
        // One screen's furniture: a full-screen node that fades in and out
        // (unscaled time: the pause stops the clock).
        public class Layer
        {
            public readonly RectTransform Rt;
            readonly CanvasGroup group;
            float target;

            public Layer(string name)
            {
                Rt = Node(Root, name);
                Fill(Rt);
                group = Rt.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.interactable = false;
                group.blocksRaycasts = false;
                Rt.gameObject.SetActive(false);
                layers.Add(this);
            }

            public bool Shown { get { return target > 0f; } }

            public void Show(bool on)
            {
                target = on ? 1f : 0f;
                if (on && !Rt.gameObject.activeSelf) { Rt.gameObject.SetActive(true); Rt.SetAsLastSibling(); }
            }

            public void Hide() { target = 0f; group.alpha = 0f; Rt.gameObject.SetActive(false); }

            public void Clear()
            {
                for (int i = Rt.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(Rt.GetChild(i).gameObject);
            }

            internal void Tick(float dt)
            {
                if (Rt == null || !Rt.gameObject.activeSelf) return;
                group.alpha = Mathf.MoveTowards(group.alpha, target, dt / 0.16f);
                if (group.alpha <= 0f && target <= 0f) Rt.gameObject.SetActive(false);
            }
        }

        static bool launchReady;
        static void Tick()
        {
            // the first menu on screen: the start-up is over (its CPU boost ends)
            if (!launchReady && GameController.instance != null && MenuPad.TopGroup() != null)
            {
                launchReady = true;
                Native.LaunchReady();
                Log.Line("start-up: the first menu is up at " + Time.realtimeSinceStartup.ToString("F1") + " s");
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            for (int i = 0; i < layers.Count; i++)
            {
                try { layers[i].Tick(dt); }
                catch (Exception e) { Loader.Report(e); }
            }
        }

        // a texture of one colour (an avatar for a player without a profile)
        static readonly Dictionary<int, Texture2D> solids = new Dictionary<int, Texture2D>();
        public static Texture2D Solid(Color c)
        {
            int key = ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255);
            Texture2D t;
            if (solids.TryGetValue(key, out t) && t != null) return t;
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.SetPixels32(new[] { (Color32)c, (Color32)c, (Color32)c, (Color32)c });
            t.Apply(false, true);
            solids[key] = t;
            return t;
        }
    }
}
