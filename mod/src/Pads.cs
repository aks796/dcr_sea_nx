// Pads.cs -- every controller, read once per frame (source/dcr_input.c through
// Native.Pad): buttons as the player holds the controller (a lone Joy-Con
// sideways is remapped there), presses and releases since the last frame, and
// the left stick as a d-pad with menu-style repeat.
using System;

namespace DcrMod
{
    public class Pad
    {
        public int Slot;              // 0-7 = players 1-8, 8 = the attached Joy-Cons
        public int Style;             // 0 = not connected
        public uint Held, Down, Up;
        public float LX, LY, RX, RY;
        // directions (d-pad or left stick) as presses with repeat, for menus
        public uint Nav;              // Btn.Left/Right/Up/Down this frame (first press + repeats)
        public uint NavRepeat;        // only the repeats (a direction held past 0.4 s)
        uint navHeld;
        float navTimer;

        public bool Connected { get { return Style != 0; } }
        public bool Pressed(uint b) { return (Down & b) != 0; }
        public bool Holding(uint b) { return (Held & b) != 0; }

        const float Dead = 0.5f;

        internal void Read(float dt)
        {
            uint b; float lx, ly, rx, ry;
            Style = Native.Pad(Slot, out b, out lx, out ly, out rx, out ry);
            if (Style == 0) b = 0;
            Down = b & ~Held;
            Up = Held & ~b;
            Held = b;
            LX = lx; LY = ly; RX = rx; RY = ry;

            uint dir = b & (Btn.Left | Btn.Right | Btn.Up | Btn.Down);
            if (lx < -Dead) dir |= Btn.Left; else if (lx > Dead) dir |= Btn.Right;
            if (ly > Dead) dir |= Btn.Up; else if (ly < -Dead) dir |= Btn.Down;
            // one direction at a time: the strongest axis wins for the stick
            if ((dir & (Btn.Left | Btn.Right)) != 0 && (dir & (Btn.Up | Btn.Down)) != 0 && (b & (Btn.Left | Btn.Right | Btn.Up | Btn.Down)) == 0)
                dir &= Math.Abs(lx) >= Math.Abs(ly) ? (Btn.Left | Btn.Right) : (Btn.Up | Btn.Down);
            Nav = 0;
            NavRepeat = 0;
            if (dir != navHeld)
            {
                Nav = dir & ~navHeld;
                navTimer = 0.40f;       // first repeat after 0.4 s
            }
            else if (dir != 0)
            {
                navTimer -= dt;
                if (navTimer <= 0f)
                {
                    Nav = dir;
                    NavRepeat = dir;
                    navTimer = 0.11f;   // then about 9 a second
                }
            }
            navHeld = dir;
        }
    }

    public static class Pads
    {
        public static readonly Pad[] All = new Pad[9];
        static Pads()
        {
            for (int i = 0; i < All.Length; i++) All[i] = new Pad { Slot = i };
        }

        public static void Update(float dt)
        {
            for (int i = 0; i < All.Length; i++) All[i].Read(dt);
        }

        // player 1's controller: slot 1, else the attached Joy-Cons
        public static Pad P1 { get { return All[0].Connected || !All[8].Connected ? All[0] : All[8]; } }

        // the controller that drives the game's own pad (source/dcr_input.c pick_active):
        // player 1, the attached Joy-Cons, then players 2-8
        static readonly int[] order = { 0, 8, 1, 2, 3, 4, 5, 6, 7 };
        // one player's controller has the menus (local multiplayer: a player choosing a figurine)
        public static Pad GameOverride;
        public static Pad Game
        {
            get
            {
                if (GameOverride != null) return GameOverride;
                foreach (int i in order) if (All[i].Connected) return All[i];
                return All[0];
            }
        }

        // any controller: menus take presses from everyone
        public static uint AnyDown()
        {
            uint d = 0;
            for (int i = 0; i < All.Length; i++) d |= All[i].Down;
            return d;
        }
        public static uint AnyHeld()
        {
            uint d = 0;
            for (int i = 0; i < All.Length; i++) d |= All[i].Held;
            return d;
        }
        public static uint AnyNav()
        {
            uint d = 0;
            for (int i = 0; i < All.Length; i++) d |= All[i].Nav;
            return d;
        }
        public static int ConnectedCount()
        {
            int n = 0;
            for (int i = 0; i < 8; i++) if (All[i].Connected) n++;
            if (n == 0 && All[8].Connected) n = 1;
            return n;
        }
    }
}
