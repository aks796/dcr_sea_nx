// Characters.cs -- characters the SEA edition does not have, added to its
// character select.
//
// A character in this game is a Character ScriptableObject (name, scales,
// animations as mesh GUIDs + materials, hop sounds, universe, the in-game
// prefab) listed by CharacterManager, plus a `defineCharacter` config line
// (sort order, universe, rarity, prize machine). Both are added before the
// game builds its theme lists (UniverseManager.bootstrap, in the boot chain
// after the config and the asset-bundle table are loaded).
//
// SORCERER'S APPRENTICE MICKEY (id 809, Epic): his two models are in the
// game's own Mickey & Friends bundle and his name ("809") in every language
// table, but he was never released. He is Magician Mickey's copy with his own
// models: the idle pose, and now and then the wand raised. In a run he works
// magic like the Genie (Sorcerer.cs).
//
// THE REST OF THE GAME FILES (ExtrasData.cs): the seven figurines the
// world-wide edition released and this one never got (Golden Camel, Dragon
// Genie, Elf Pleakley, Santa Jumba, Vampire Stitch, Witch Lilo, The Ocean) and
// eight that were never released anywhere (Classic Mickey, the Fantasia Broom,
// Safari Mickey, Max, Oswald, Ortensia, the Scuba Diver, Human Cadenza) -- all
// of their models are in this game's bundles. Each is a copy of a figurine of
// its theme (the in-game body, death effects, world) with its own models. The
// Meowana and Hanging Tree figurines the config hides are shown. All of them
// are won in the prize machine, weighted by rarity like the game's own.
//
// DUCKTALES (ids 3100-3125): the SEA edition has DuckTales half-built -- a
// hidden theme with the whole Duckburg world, one early model per character,
// the theme's name -- but no characters, music or sounds. They come from the
// player's own Disney Crossy Road (world-wide edition) APK, dcr.apk: the setup
// copies its DuckTales character models, music and sounds bundles and its
// English theme logos (source/dcr_setup.c); here they are opened and their
// GUIDs added to the game's asset table, the theme is shown with its music and
// its card logo, and the 26 characters are built from their recipe
// (DuckTalesData.cs: GUIDs, names, scales -- references only) on the game's
// generic player. If Unity cannot open those 2017.4 bundles, the characters
// fall back to the early models in the SEA edition's own bundle (Gizmoduck,
// whose early model is broken, is then left out).
// Config: [game] ducktales, sorcerer_mickey, hidden_characters.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using AssetBundles;
using Shakespeare;
using UnityEngine;

namespace DcrMod
{
    public static class Characters
    {
        static bool sorcerer, ducktales, extras, injected;
        const string DcrDir = "/data/data/net.gogame.disney.crossyroad/files/dcr/";
        static readonly string[] DcrBundles = { "models-ducktales-characters", "music-ducktales", "sounds-ducktales" };
        static readonly Dictionary<string, string> dcrMap = new Dictionary<string, string>(); // guid -> "dcr-<bundle>\t<asset>"
        static readonly Dictionary<string, string> names = new Dictionary<string, string>();  // id -> English name
        static bool dcrModels, dcrMusic, dcrSounds;
        const string DuckMusic = "79a1a52fef138493b9e68e134556a574";
        const string FriendsMusic = "af9d51489610d42109158fcf140024ce";

        public static void Install()
        {
            sorcerer = Native.Config("game.sorcerer_mickey", 1) != 0;
            ducktales = Native.Config("game.ducktales", 1) != 0;
            extras = Native.Config("game.hidden_characters", 1) != 0;
            if (!sorcerer && !ducktales && !extras) return;
            Hook.Install(typeof(UniverseManager), "bootstrap", null, typeof(Characters), "Bootstrap", "Orig_Bootstrap");
            Loader.OnFrame(Upkeep);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_Bootstrap(UniverseManager self) { throw new InvalidOperationException("hook stub"); }
        static void Bootstrap(UniverseManager self)
        {
            try { Inject(self); }
            catch (Exception e) { Loader.Report(e); }
            Orig_Bootstrap(self);
        }

        // ------------------------------------------------------------ common
        internal static Character Find(string id)
        {
            var cm = CharacterManager.Instance;
            if (cm == null || cm.characterList == null) return null;
            foreach (var c in cm.characterList)
                if (c != null && c.id == id) return c;
            return null;
        }

        static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
        static void CollectMaterials(QAnimation[] l)
        {
            if (l == null) return;
            foreach (var a in l)
                if (a != null && a.frames != null && a.frames.List != null)
                    foreach (var f in a.frames.List)
                        if (f != null && f.material != null && !materials.ContainsKey(f.material.name))
                            materials[f.material.name] = f.material;
        }
        internal static Material MaterialNamed(string n, Material fallback)
        {
            if (n == OceanMaterial) return Ocean(fallback);
            if (materials.Count == 0)
                foreach (var c in CharacterManager.Instance.characterList)
                {
                    if (c == null) continue;
                    CollectMaterials(c.idleAnimations); CollectMaterials(c.jumpAnimations); CollectMaterials(c.selectAnimations);
                }
            Material m;
            return n != null && materials.TryGetValue(n, out m) ? m : fallback;
        }

        internal static QAnimation Anim(string name, string next, float speed, string[] guids, Material[] mats)
        {
            var a = new QAnimation();
            a.name = name; a.next = next; a.speed = speed;
            a.frames = new ReorderableList_QFrame();
            a.frames.List = new List<QFrame>();
            for (int i = 0; i < guids.Length; i++)
            {
                var f = new QFrame();
                f.meshGUID = guids[i];
                f.material = mats[i];
                a.frames.List.Add(f);
            }
            return a;
        }

        static void Define(string line)
        {
            var cmd = new Command();
            cmd.setCommand(line);
            CharacterManager.Instance.defineCharacter(cmd);
        }

        static void Add(List<Character> list, Character c)
        {
            list.Add(c);
            var cm = CharacterManager.Instance;
            if (cm.characterMap != null && cm.characterMap.Count > 0) cm.characterMap[c.id] = c;
            if (cm.notSyncFigurines != null && !cm.notSyncFigurines.Contains(c.id)) cm.notSyncFigurines.Add(c.id); // not the cloud's
        }

        static void Inject(UniverseManager um)
        {
            if (injected) return;
            var cm = CharacterManager.Instance;
            if (cm == null || cm.characterList == null) return;
            injected = true;
            var list = new List<Character>(cm.characterList);
            int before = list.Count;
            if (sorcerer && Find("809") == null) AddSorcerer(list);
            if (extras) AddExtras(list);
            if (ducktales && Find("3100") == null) AddDuckTales(um, list);
            if (list.Count != before)
            {
                cm.characterList = list.ToArray();
                Log.Line("characters: " + (list.Count - before) + " added (" + list.Count + " in all)");
            }
        }

        // ------------------------------------------------------------ Sorcerer's Apprentice Mickey
        const string SorcererIdle = "e8ca9e81481c2470fbee0e3a42fd082f";
        const string SorcererWand = "8551367c9e9ee4573a9b193a8ff603ea";

        static void AddSorcerer(List<Character> list)
        {
            var src = Find("937"); // Magician Mickey: Mickey & Friends, the generic player, no secret attached
            if (src == null || src.idleAnimations == null || src.idleAnimations.Length == 0) { Log.Line("characters: no Magician Mickey to base Sorcerer's Apprentice Mickey on"); return; }
            var mat = src.idleAnimations[0].frames.List[0].material;
            var c = UnityEngine.Object.Instantiate(src);
            c.name = "809_SorcerersApprenticeMickey";
            c.id = "809";
            c.anim = "disney_friends_SorcerersApprenticeMickey";
            const string pre = "char-disney_friends_SorcerersApprenticeMickey-";
            c.idleAnimationBehaviour = IdleAnimationBehaviour.RandomNoRepeat;
            c.idleAnimations = new[] {
                Anim(pre + "idle-1", pre + "idle-1", 0f, new[] { SorcererIdle }, new[] { mat }),
                Anim(pre + "idle-2", pre + "idle-2", 3f, new[] { SorcererIdle, SorcererWand, SorcererWand, SorcererWand, SorcererIdle }, new[] { mat, mat, mat, mat, mat }) };
            c.selectAnimations = new[] { Anim(pre + "select-1", pre + "select-1", 1.5f, new[] { SorcererIdle, SorcererWand }, new[] { mat, mat }) };
            // his hop: the wand raised (his one other pose), as the Genie's hop is his own pose
            c.jumpAnimations = new[] { Anim(pre + "jump-1", pre + "idle-1", 6f, new[] { SorcererWand }, new[] { mat }) };
            c.readyAnimations = new QAnimation[0];
            c.altJumpAnimations = new QAnimation[0];
            c.deadAnimations = new[] { Anim(pre + "dead-1", pre + "dead-1", 0f, new[] { SorcererIdle }, new[] { mat }) };
            c.blockedAnimations = new[] { Anim(pre + "wand", pre + "idle-1", 2f, new[] { SorcererWand }, new[] { mat }) };
            // the spell (Sorcerer.cs): the wand up for half a second
            c.additionalAnimations = new[] { Anim(Sorcerer.CastAnim, pre + "idle-1", 6f, new[] { SorcererWand, SorcererWand, SorcererWand }, new[] { mat, mat, mat }) };
            c.characterSelectPrefab = null;
            c.promotionalDisplayPrefab = null;
            c.specialBannerSprite = null;
            // sort 117 (Mickey's): right after him in the carousel; Epic, won in the
            // prize machine with the Epic weights (Pluto's, Willie the Giant's)
            Define("defineCharacter\t809\tSorcerers Apprentice Mickey\t117\t2\tNO\tNO\tNO\tNO\tYES\tNO\tNO\tNO\tNO\tNO\tNO\tNO\tNO\tNO\tYES\tNO\tNO\tnone\tnone\t166\t166\t142");
            Add(list, c);
            Log.Line("characters: Sorcerer's Apprentice Mickey (809) in Mickey & Friends");
        }

        // ------------------------------------------------------------ the rest of the game files
        static void AddExtras(List<Character> list)
        {
            int n = 0;
            foreach (var x in ExtrasData.Chars)
            {
                if (Find(x.Id) != null) continue;
                var t = Find(x.Template);
                if (t == null || t.idleAnimations == null || t.idleAnimations.Length == 0) { Log.Line("characters: no figurine " + x.Template + " to base " + x.Id + " on"); continue; }
                var fallbackMat = t.idleAnimations[0].frames.List[0].material;
                var c = UnityEngine.Object.Instantiate(t); // its theme's in-game body, world, death effects, music
                c.name = x.Id + "_" + x.Anim;
                c.id = x.Id;
                c.anim = x.Anim;
                c.selectScreenScale = x.Scale;
                c.selectedZoomPercentage = x.Zoom;
                c.idleAnimationBehaviour = (IdleAnimationBehaviour)x.IdleBehaviour;
                c.selectAnimations = BuildSea(x.Select, fallbackMat);
                c.idleAnimations = BuildSea(x.Idle, fallbackMat);
                c.jumpAnimations = BuildSea(x.Jump, fallbackMat);
                c.readyAnimations = new QAnimation[0];
                c.altJumpAnimations = new QAnimation[0];
                c.deadAnimations = new QAnimation[0];
                c.blockedAnimations = new QAnimation[0];
                c.additionalAnimations = new QAnimation[0];
                if (x.Hop != null) c.hopAudioKey = x.Hop;
                if (x.GenericHop != null) c.genericHopsKey = x.GenericHop;
                c.characterSelectPrefab = null;
                c.promotionalDisplayPrefab = null;
                c.specialBannerSprite = null;
                Define(x.Config);
                if (x.Name != null) names[x.Id] = x.Name;
                Add(list, c);
                n++;
            }
            // the two the config hides (a secret nothing unlocks): shown, and in the prize machine as Rares
            foreach (string id in new[] { "2735", "2226" })
            {
                var cfg = CharacterManager.Instance.characterConfigForID(id);
                if (cfg == null || Find(id) == null) continue;
                cfg.hidden = false;
                cfg.REDACTED = false;
                cfg.rare = true;
                cfg.availableInPrizeMachine = true;
                cfg.machineWeight = 333;
                cfg.pixelMachineWeight = 333;
                cfg.maxedWeight = 250;
                n++;
            }
            AddNames();
            Log.Line("characters: " + n + " figurines from the game files (never released, or only in the world-wide edition)");
        }

        static QAnimation[] BuildSea(DuckTalesData.A[] src, Material fallbackMat)
        {
            if (src == null || src.Length == 0) return new QAnimation[0];
            var r = new QAnimation[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var a = src[i];
                var g = new string[a.Frames.Length];
                var m = new Material[a.Frames.Length];
                for (int k = 0; k < a.Frames.Length; k++)
                {
                    g[k] = a.Frames[k].Guid;
                    m[k] = MaterialNamed(a.Frames[k].Mat, fallbackMat);
                }
                r[i] = Anim(a.Name, a.Next, a.Speed, g, m);
            }
            return r;
        }

        // The Ocean's see-through water: the world-wide edition's own material is
        // not in this one, but its shader is, and Moana's ghost material has the
        // same settings (colour, rim) -- the ghost material with the water shader.
        const string OceanMaterial = "VertexColourCharactersMoanaOcean";
        static Material ocean;
        static Material Ocean(Material fallback)
        {
            if (ocean != null) return ocean;
            var ghost = MaterialNamed("VertexColourCharactersMoanaGhost", null);
            var sh = Shader.Find("Mobile/VertexColor+Transparent+NoBackFace");
            if (ghost == null || sh == null) { Log.Line("characters: no water shader for The Ocean; opaque"); return fallback; }
            ocean = new Material(ghost);
            ocean.shader = sh;
            ocean.name = OceanMaterial;
            ocean.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return ocean;
        }

        // ------------------------------------------------------------ DuckTales
        static AssetBundle Open(string name)
        {
            string path = DcrDir + name;
            try
            {
                var b = AssetBundle.LoadFromFile(path);
                if (b == null) { Log.Line("ducktales: " + name + " did not open (Unity 5.6 and this 2017.4 bundle)"); return null; }
                string key = "dcr-" + name;
                if (!AssetBundleManager.m_LoadedAssetBundles.ContainsKey(key))
                {
                    var lb = new LoadedAssetBundle(b);
                    lb.m_ReferencedCount = 1 << 28; // the game's 1 s unloads never reach 0
                    AssetBundleManager.m_LoadedAssetBundles.Add(key, lb);
                }
                return b;
            }
            catch (Exception e)
            {
                Log.Line("ducktales: " + name + ": " + e.Message);
                return null;
            }
        }

        static void AddDuckTales(UniverseManager um, List<Character> list)
        {
            var uni = um.universeList != null ? Array.Find(um.universeList, u => u != null && u.Id == 12) : null;
            if (uni == null) { Log.Line("ducktales: this game has no DuckTales theme (12)"); return; }
            bool haveFiles = File.Exists(DcrDir + "guids-to-asset-mapping.txt");
            if (!haveFiles)
            {
                Log.Line("ducktales: no dcr.apk in the game folder -- the DuckTales pack needs your copy of Disney Crossy Road (the world-wide edition)");
                return;
            }
            // the DCR bundles, under names of their own ("dcr-..."): the SEA edition's
            // bundle of the same name holds the same internal file and must never be
            // open at the same time -- every DuckTales character GUID goes to DCR's
            dcrModels = Open(DcrBundles[0]) != null;
            dcrMusic = Open(DcrBundles[1]) != null;
            dcrSounds = Open(DcrBundles[2]) != null;
            try
            {
                foreach (string line in File.ReadAllLines(DcrDir + "guids-to-asset-mapping.txt"))
                {
                    var p = line.TrimEnd('\r').Split('\t');
                    if (p.Length != 3) continue;
                    if ((p[1] == DcrBundles[0] && dcrModels) || (p[1] == DcrBundles[1] && dcrMusic) || (p[1] == DcrBundles[2] && dcrSounds))
                        dcrMap[p[0]] = "dcr-" + p[1] + "\t" + p[2];
                }
            }
            catch (Exception e) { Log.Line("ducktales: its asset table: " + e.Message); }
            MergeMap();

            // the theme: shown, its own sounds, its music, first among the regular themes
            uni.isHidden = false;
            uni.audioKey = UniverseAssetKey.Ducktales;
            uni.SortOrder = 20;
            uni.ShowCardLabel = true; // its name on the card until the logo is in
            duckUni = uni;
            LoadLogo();
            uni.WorldMusic = dcrMusic ? DuckMusic : FriendsMusic;
            uni.TopScoreLabelColor = new Color(0.8f, 0.52f, 0.22f, 1f);
            uni.EventLiveBackgroundColor = new Color(0.8f, 0.52f, 0.22f, 1f);
            var ul = new List<Universe>(um.universeList);
            ul.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            um.universeList = ul.ToArray();

            AddSounds();

            var template = Find("937");
            if (template == null) { Log.Line("ducktales: no template character"); return; }
            var fallbackMat = template.idleAnimations[0].frames.List[0].material;
            int n = 0;
            foreach (var d in DuckTalesData.Chars)
            {
                if (!dcrModels && d.Id == "3120") continue; // Gizmoduck's early model is broken
                var c = UnityEngine.Object.Instantiate(template);
                c.name = d.Id + "_" + d.Anim;
                c.id = d.Id;
                c.anim = d.Anim;
                c.selectScreenScale = d.Scale;
                c.selectedZoomPercentage = d.Zoom;
                c.idleAnimationBehaviour = (IdleAnimationBehaviour)d.IdleBehaviour;
                c.selectAnimations = Build(d.Select, d, fallbackMat);
                c.idleAnimations = Build(d.Idle, d, fallbackMat);
                c.readyAnimations = Build(d.Ready, d, fallbackMat);
                c.jumpAnimations = Build(d.Jump, d, fallbackMat);
                c.altJumpAnimations = Build(d.AltJump, d, fallbackMat);
                c.deadAnimations = Build(d.Dead, d, fallbackMat);
                c.blockedAnimations = Build(d.Blocked, d, fallbackMat);
                c.additionalAnimations = Build(d.Additional, d, fallbackMat);
                c.hopAudioKey = SoundKey(d.Hop);
                c.genericHopsKey = SoundKey(d.GenericHop);
                c.audioScreamKey = d.Scream;
                c.firstHopKey = d.FirstHop;
                c.randomPitchHop = d.RandomPitch;
                c.worldPieceSwap = new[] { "ducktales" };
                c.universe = uni;
                c.sort = 800;
                c.characterSelectPrefab = null;
                c.promotionalDisplayPrefab = null;
                c.specialBannerSprite = null;
                c.overrideMusic = "";
                Define(ConfigLine(d.Config));
                names[d.Id] = d.Name;
                Add(list, c);
                n++;
            }
            AddNames();
            Log.Line("ducktales: " + n + " characters; models " + (dcrModels ? "from Disney Crossy Road" : "the SEA edition's early ones") +
                     ", music " + (dcrMusic ? "DuckTales" : "Mickey & Friends'") + ", sounds " + (dcrSounds ? "from Disney Crossy Road" : "the shared ones"));
        }

        static QAnimation[] Build(DuckTalesData.A[] src, DuckTalesData.C d, Material fallbackMat)
        {
            if (src == null || src.Length == 0) return new QAnimation[0];
            string baseGuid = d.Idle.Length > 0 && d.Idle[0].Frames.Length > 0 ? d.Idle[0].Frames[0].Guid : null;
            var r = new QAnimation[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var a = src[i];
                var g = new string[a.Frames.Length];
                var m = new Material[a.Frames.Length];
                for (int k = 0; k < a.Frames.Length; k++)
                {
                    var f = a.Frames[k];
                    // without DCR's bundle only the SEA edition's early models exist
                    g[k] = dcrModels || f.InSea ? f.Guid : baseGuid;
                    m[k] = MaterialNamed(f.Mat, fallbackMat);
                }
                r[i] = Anim(a.Name, a.Next, a.Speed, g, m);
            }
            return r;
        }

        // The theme card's logo. DCR's English logos bundle has it (DuckTalesLogo,
        // in a 1024x2048 atlas); that bundle has the same internal name as the
        // SEA game's own logos bundle, which the game opens for the other cards,
        // so it is open only for a moment: the logo is copied into a texture of
        // its own (310x139) and the bundle closed with everything it loaded.
        const string DuckLogoName = "DuckTalesLogo";
        static Universe duckUni;
        static Sprite duckLogo;
        static int logoTries;
        static void LoadLogo()
        {
            if (duckLogo != null || duckUni == null || logoTries >= 10 || !File.Exists(DcrDir + "logos.en-us")) return;
            logoTries++;
            AssetBundle b = null;
            try
            {
                b = AssetBundle.LoadFromFile(DcrDir + "logos.en-us");
                if (b == null) { Log.Line("ducktales: the logo bundle did not open (try " + logoTries + ")"); return; }
                var s = b.LoadAsset<Sprite>(DuckLogoName);
                if (s == null) { Log.Line("ducktales: no DuckTales logo in dcr.apk"); logoTries = 99; return; }
                duckLogo = CopySprite(s);
            }
            catch (Exception e) { Log.Line("ducktales: its logo: " + e.Message); logoTries = 99; }
            finally { if (b != null) b.Unload(true); }
            if (duckLogo == null) return;
            duckUni.CardLogoSprite = duckLogo;
            duckUni.CardLogoX = 0f;
            duckUni.CardLogoY = 0f;
            duckUni.CardBackgroundSpriteColor = Color.white; // DCR's card: the logo on white
            duckUni.ShowCardLabel = false;
            CacheLogo();
            Log.Line("ducktales: card logo " + duckLogo.rect.width + "x" + duckLogo.rect.height);
        }

        // the atlas drawn into a render target of its size, the logo's rectangle read back
        static Sprite CopySprite(Sprite s)
        {
            var tex = s.texture;
            Rect r = s.textureRect;
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.name = DuckLogoName;
                copy.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0, false);
                copy.Apply(false, true); // uploaded; no CPU copy kept
                copy.hideFlags = HideFlags.DontUnloadUnusedAsset;
                var pivot = new Vector2(s.pivot.x / s.rect.width, s.pivot.y / s.rect.height);
                var sp = Sprite.Create(copy, new Rect(0, 0, w, h), pivot, s.pixelsPerUnit);
                sp.name = DuckLogoName;
                sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return sp;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // the cards ask UniverseManager for the logo by name (from the SEA logos bundle, which lacks it)
        static void CacheLogo()
        {
            var um = UniverseManager.Instance;
            if (duckLogo != null && um != null && um.universeSprites != null && !um.universeSprites.ContainsKey(DuckLogoName))
                um.universeSprites[DuckLogoName] = duckLogo;
        }

        // DCR's config line, with every DuckTales figurine winnable in the prize
        // machine (their secrets and events are not in this edition) and no
        // collection sets (the SEA config does not define DuckTales' sets)
        static string ConfigLine(string line)
        {
            var p = line.Split('\t');
            if (p.Length < 27) return line;
            p[6] = "NO";   // secret
            p[13] = "NO";  // hidden unlock
            p[17] = "NO";  // event prize
            p[18] = "NO";  // hidden
            p[19] = "YES"; // in the prize machine
            if (p[24] == "0") { p[24] = "333"; p[25] = "333"; p[26] = "250"; }
            p[23] = "none";
            return string.Join("\t", p);
        }

        // hop sounds whose clips are only in DCR's sounds bundle fall back when it did not open
        static string SoundKey(string k)
        {
            if (dcrSounds || k == null) return k;
            if (k == "Ducktales-JumpScrooge" || k == "Ducktales-JumpHeli") return "Ducktales-JumpNormal";
            return k;
        }

        static void AddSounds()
        {
            var ac = AudioController.instance;
            if (ac == null || ac.audioMappings == null) return;
            AudioClipMapping character = null, effects = null;
            foreach (var m in ac.audioMappings)
            {
                if (m == null) continue;
                if (m.name == "Christmas-BellHop") character = m;
                if (m.name == "Common-BirdFlapping") effects = m;
            }
            foreach (var s in DuckTalesData.Sounds)
            {
                bool dcrOnly = false;
                foreach (var g in s.Guids) if (dcrMap.ContainsKey(g)) dcrOnly = true;
                if (dcrOnly && !dcrSounds) continue;
                var m = new AudioClipMapping();
                m.name = s.Name;
                m.sounds = s.Guids;
                m.mixerGroup = s.Mixer == 0 ? (character != null ? character.mixerGroup : null) : (effects != null ? effects.mixerGroup : null);
                m.universeSorting = UniverseAssetKey.Ducktales;
                m.clipOverrides = new List<AudioClipOverride>();
                ac.audioMappings.Add(m);
                if (ac.audioMappingGroups != null && ac.audioMappingGroups.Count > 0 && !ac.audioMappingGroups.ContainsKey(m.name))
                    ac.audioMappingGroups[m.name] = m;
            }
            if (!dcrSounds) return;
            foreach (var o in DuckTalesData.Overrides)
                foreach (var m in ac.audioMappings)
                    if (m != null && m.name == o[0])
                    {
                        if (m.clipOverrides == null) m.clipOverrides = new List<AudioClipOverride>();
                        var co = new AudioClipOverride();
                        co.name = "Ducktales";
                        co.universeAudioKey = UniverseAssetKey.Ducktales;
                        var g = new string[o.Length - 1];
                        Array.Copy(o, 1, g, 0, g.Length);
                        co.sounds = g;
                        m.clipOverrides.Add(co);
                    }
        }

        // the names (the SEA tables have none for 3100-3125): English in every language
        static string anyName = "";
        static void AddNames()
        {
            var lc = LocalizationController.instance;
            if (lc == null || lc.currentLanguageDictionary == null) return;
            foreach (var kv in names)
            {
                lc.currentLanguageDictionary[kv.Key.ToLower()] = kv.Value;
                anyName = kv.Key.ToLower();
            }
        }

        // the asset table is filled (and can be refilled) by the game's own loader
        static Dictionary<string, string> mergedInto;
        static void MergeMap()
        {
            var abl = SingletonMonobehaviour<AssetBundleLoader>.Instance;
            if (abl == null || abl.GUIDsToAssetMapping == null || dcrMap.Count == 0) return;
            if (ReferenceEquals(mergedInto, abl.GUIDsToAssetMapping)) return;
            foreach (var kv in dcrMap) abl.GUIDsToAssetMapping[kv.Key] = kv.Value;
            mergedInto = abl.GUIDsToAssetMapping;
        }

        static void Upkeep()
        {
            if (Loader.FrameCount % 20 != 0) return;
            MergeMap();
            if (duckUni != null && duckLogo == null && logoTries < 10) LoadLogo(); // the SEA logos bundle was open
            CacheLogo();
            if (names.Count > 0)
            {
                var lc = LocalizationController.instance;
                if (lc != null && lc.currentLanguageDictionary != null && !lc.currentLanguageDictionary.ContainsKey(anyName)) AddNames();
            }
            // a DuckTales figurine chosen while dcr.apk was there, and now it is not
            if (!injected || dcrMap.Count > 0) return;
            string cur = PlayerPrefs.GetString("CurrentCharacter", "");
            if (cur.StartsWith("31") && cur.Length == 4 && Find(cur) == null)
                PlayerPrefs.SetString("CurrentCharacter", "802");
        }
    }
}
