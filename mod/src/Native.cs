// Native.cs -- what the port's C code offers the C# (source/dcr_mod.c: icalls).
using System;
using System.Runtime.CompilerServices;

namespace DcrMod
{
    public static class Native
    {
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Log(string text);

        // target/replacement/original: MonoMethod pointers (MethodBase.MethodHandle.Value)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Detour(IntPtr target, IntPtr replacement, IntPtr original);

        // slot 0-7 = players 1-8, 8 = the attached Joy-Cons; returns the style set (0 = none).
        // Buttons are HidNpadButton bits as the player holds the controller; sticks -1..1, y up.
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Pad(int slot, out uint buttons, out float lx, out float ly, out float rx, out float ry);

        // which slot drives the game's own gamepad (InControl); -1 = the usual order
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void SetGameSlot(int slot);

        // a stored entry of game.apk: 0 and where its data lies, or -1
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int ApkEntry(string name, out long offset, out long size);

        // config.ini "section.key": booleans as 1/0
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Config(string key, int dflt);

        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string GameRoot();

        // the game folder for System.IO: "/switch/..." (the port puts the SD card's "sdmc:" back;
        // Mono takes "sdmc:/..." for a relative name)
        public static string Root
        {
            get
            {
                string r = GameRoot();
                int c = r.IndexOf(':');
                return c > 0 && c < 8 ? r.Substring(c + 1) : r;
            }
        }

        [MethodImpl(MethodImplOptions.InternalCall)] public static extern long TicksMs();

        // the Switch's controller screen for min..max players (blocking); players after it, or -1
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int ControllerApplet(int min, int max);

        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void Rumble(int slot, float amplitude, int ms);
        // the title menu is up: the start-up's CPU boost ends (source/dcr_boost.c)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void LaunchReady();

        // the reply the game's next WWW request to exactly this URL gets (source/jni_www.c)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern void QueueWww(string url, string body);

        // The Switch's profiles (source/dcr_profile.c). uids: 32 hex digits.
        // ProfilePick: the system's profile picker (blocking), `excluded` (comma-separated uids)
        // greyed out; the uid picked, "" when the player backed out, "-" when there is no picker.
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string ProfilePick(string excluded);
        // the nickname ("" if unknown)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string ProfileName(string uid);
        // the icon written to <game folder>/profiles/<uid>.jpg: its path, or ""
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string ProfileIcon(string uid);
        // every profile on this Switch, comma-separated
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern string ProfileList();
    }

    // HidNpadButton bits
    public static class Btn
    {
        public const uint A = 1u << 0, B = 1u << 1, X = 1u << 2, Y = 1u << 3;
        public const uint StickL = 1u << 4, StickR = 1u << 5, L = 1u << 6, R = 1u << 7;
        public const uint ZL = 1u << 8, ZR = 1u << 9, Plus = 1u << 10, Minus = 1u << 11;
        public const uint Left = 1u << 12, Up = 1u << 13, Right = 1u << 14, Down = 1u << 15;
        public const uint StickLLeft = 1u << 16, StickLUp = 1u << 17, StickLRight = 1u << 18, StickLDown = 1u << 19;
    }
}
