// Sorcerer.cs -- Sorcerer's Apprentice Mickey's magic in a run, like the
// Genie's in Aladdin: every 3-4 seconds (the Genie's own timing) he looks down
// the column he faces, up to 10 tiles, and if an obstacle stands there he
// raises his wand and it turns into one of Fantasia's enchanted brooms, which
// marches on the spot. The obstacle still blocks the way, as the Genie's do.
//
// Obstacles are BlockingObjects: their mesh comes from QAnimatorFrame
// (qanimatorMain, and qanimatorSwitch for the Mickey & Friends world's
// black-and-white to colour switch); the broom plays on both. wizardHit, the
// game's own "already changed" flag (Ralph's and Baloo's guns set it), marks
// an enchanted obstacle, and BlockingObject.Setup clears it and puts the
// obstacle's own model back when the pool hands it out again.
// Local multiplayer: every Sorcerer on the field casts.
using System.Collections.Generic;
using UnityEngine;

namespace DcrMod
{
    public static class Sorcerer
    {
        public const string Id = "809";
        public const string CastAnim = "char-disney_friends_SorcerersApprenticeMickey-cast";
        const float Range = 10f, Cooldown = 3f, CooldownRand = 1f, SpellDelay = 0.3f, BroomFrame = 0.2f;
        static readonly string[] BroomGuids = {   // disney_fantasiabroom idle, jump 1, idle, jump 2
            "2f2943bd3e6a94352ba0db4cb425fb39", "315a41df0ec9648709519ec1fc4a7ee4",
            "2f2943bd3e6a94352ba0db4cb425fb39", "174bc42815cf9460eb769ffdd5b6cbb9" };

        class Enchanted
        {
            public BlockingObject B;
            public Vector3 Pos;
            public float At;      // when the spell lands
            public bool Landed;
            public int Frame;
            public float Next;
        }

        static QAnimation broom;
        static int casts, tries;
        static PlayerController[] players = new PlayerController[0];
        static readonly Dictionary<int, float> nextCast = new Dictionary<int, float>();
        static readonly List<Enchanted> live = new List<Enchanted>();

        // TestDriver (test_sorcerer): spells on the title screen too, in every direction
        public static bool TestMode;

        public static void Install()
        {
            if (Native.Config("game.sorcerer_mickey", 1) == 0) return;
            Loader.OnFrame(Frame);
        }

        static void Frame()
        {
            var gc = GameController.instance;
            if (gc == null || !(gc.isGameActive && gc.isGameStarted || TestMode) || CrossyPhysics.instance == null)
            {
                if (live.Count > 0 || nextCast.Count > 0) { live.Clear(); nextCast.Clear(); }
                return;
            }
            if (gc.pauseController != null && gc.pauseController.isPaused()) return;
            float now = Time.time;
            if (Loader.FrameCount % 30 == 0 || players.Length == 0)
                players = Object.FindObjectsOfType<PlayerController>();
            if (TestMode && Loader.FrameCount % 120 == 0)
                Log.Line("sorcerer test: " + players.Length + " player(s)" + (players.Length > 0 && players[0] != null ? ", " + (players[0].character != null ? players[0].character.id : "no character") + (players[0].IsDead ? " dead" : "") : ""));
            foreach (var p in players)
            {
                if (p == null || !p.gameObject.activeInHierarchy || p.IsDead || p.character == null || p.character.id != Id) continue;
                int key = p.GetInstanceID();
                float t;
                if (!nextCast.TryGetValue(key, out t))
                {
                    nextCast[key] = now + Cooldown;
                    if (broom == null && (broom = BuildBroom(p)) != null)
                        foreach (var f in broom.frames.List) f.LoadMesh(null); // loaded before the first spell
                    continue;
                }
                if (now < t) continue;
                nextCast[key] = Cast(p, now)
                    ? now + Cooldown + Random.value * CooldownRand
                    : now + (Cooldown + Random.value * CooldownRand) / 5f; // nothing to enchant: look again soon
            }
            for (int i = live.Count - 1; i >= 0; i--)
                if (!Tick(live[i], now)) live.RemoveAt(i);
        }

        static bool Cast(PlayerController p, float now)
        {
            Vector3 dir = p.Directon;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            var pos = p.transform.position;
            var origin = new Vector3(Mathf.RoundToInt(pos.x), 1f, Mathf.RoundToInt(pos.z));
            GameObject target = CrossyPhysics.instance.RaycastBlockingObjects(origin, dir, Range);
            if (TestMode && target == null)
                for (int row = 0; row <= 4 && target == null; row++)
                    foreach (var d in new[] { Vector3.left, Vector3.right })
                    {
                        var t = CrossyPhysics.instance.RaycastBlockingObjects(origin + Vector3.forward * row, d, Range);
                        var tb = t != null ? t.GetComponent<BlockingObject>() : null;
                        if (tb != null && !tb.wizardHit) { target = t; break; }
                    }
            if (TestMode && tries++ < 6) Log.Line("sorcerer test: at " + origin + " facing " + dir + " -> " + (target == null ? "nothing" : target.name + (target.GetComponent<BlockingObject>() != null ? " (blocking)" : "")));
            if (target == null) return false;
            var b = target.GetComponent<BlockingObject>();
            if (b == null || b.wizardHit || b.qanimatorMain == null) return false;
            // secrets and characters standing in the world stay as they are
            if (target.GetComponent<SpecialCharacter>() != null || target.GetComponent<FindCharacterSecret>() != null ||
                target.GetComponent<FacePlayer>() != null || target.GetComponent<PlayerController>() != null) return false;
            if (broom == null) return false;
            b.wizardHit = true;
            if (p.qanimator != null) p.qanimator.PlayAnimation(CastAnim);
            if (AudioController.instance != null) AudioController.instance.PlaySound("Common-TransitionMagicPuff");
            live.Add(new Enchanted { B = b, Pos = target.transform.position, At = now + SpellDelay });
            if (casts++ < 3) Log.Line("sorcerer: " + b.objectType + " at " + target.transform.position + " becomes a broom");
            return true;
        }

        // false when the obstacle is gone (back in its pool, or set up anew)
        static bool Tick(Enchanted e, float now)
        {
            var b = e.B;
            if (b == null || !b.gameObject.activeInHierarchy || !b.wizardHit || (b.transform.position - e.Pos).sqrMagnitude > 0.01f) return false;
            if (!e.Landed)
            {
                if (now < e.At) return true;
                e.Landed = true;
                Flash(e.Pos);
                SetFrame(b, 0, true);
                e.Next = now + BroomFrame;
                return true;
            }
            if (now >= e.Next)
            {
                e.Frame = (e.Frame + 1) % BroomGuids.Length;
                SetFrame(b, e.Frame, false);
                e.Next = now + BroomFrame;
            }
            return true;
        }

        static void SetFrame(BlockingObject b, int frame, bool start)
        {
            foreach (var q in new[] { b.qanimatorMain, b.qanimatorSwitch })
            {
                if (q == null) continue;
                if (start) q.PlayAnimation(broom);
                else q.SetFrame(frame);
            }
        }

        // the pick-up flash the game's own gun figurines use, when this world has it
        static void Flash(Vector3 at)
        {
            var op = ObjectPooler.instance;
            if (op == null || op.dictionaryPool == null || !op.dictionaryPool.ContainsKey("pickupflash")) return;
            var go = op.GetPooledGameObject("pickupflash");
            if (go != null) go.transform.position = at + Vector3.up * 0.5f;
        }

        static QAnimation BuildBroom(PlayerController p)
        {
            Material mat = null;
            var c = p.character;
            if (c.idleAnimations != null && c.idleAnimations.Length > 0 && c.idleAnimations[0].frames.List.Count > 0)
                mat = c.idleAnimations[0].frames.List[0].material;
            if (mat == null) return null;
            var mats = new Material[BroomGuids.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            return Characters.Anim("sorcerer-enchanted-broom", "sorcerer-enchanted-broom", 0f, BroomGuids, mats);
        }
    }
}
