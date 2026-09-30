// Loader.cs -- the entry point and the mod's frame.
//
// source/dcr_mod.c calls DcrMod.Loader.Init once the game's assemblies are
// loaded (on the engine's main thread, between two frames). Unity 5.6 does not
// run Start/Update on a MonoBehaviour from an assembly it did not load itself
// (Awake and OnEnable came, Update never did: Ryujinx 2026-09-29), so the mod's
// frame rides on the game's own: a hook on KlicktockInput.InputManager.Update
// -- the game's input step, a DontDestroyOnLoad singleton that runs every
// frame from the first scene on -- runs Frame() and then the game's code.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DcrMod
{
    public static class Loader
    {
        public static int FrameCount;
        static readonly List<Action> frameHandlers = new List<Action>();
        static readonly List<Action> lateHandlers = new List<Action>();

        // per-frame work of the features, in the order added (before the game's input step)
        public static void OnFrame(Action a) { frameHandlers.Add(a); }
        // after the game's own input step (InputManager.Update)
        public static void OnAfterInput(Action a) { lateHandlers.Add(a); }

        public static void Init()
        {
            try
            {
                Log.Line("DcrMod on Unity " + Application.unityVersion);
                Hook.Install(typeof(KlicktockInput.InputManager), "Update", null, typeof(Loader), "InputUpdate", "Orig_InputUpdate");
                Features.Install();
                Log.Line("ready: " + Hook.Installed + " hooks" + (Hook.Failed > 0 ? ", " + Hook.Failed + " FAILED" : ""));
            }
            catch (Exception e)
            {
                Log.Line("Init failed: " + e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Orig_InputUpdate(KlicktockInput.InputManager self) { throw new InvalidOperationException("hook stub"); }

        static void InputUpdate(KlicktockInput.InputManager self)
        {
            Frame();
            Orig_InputUpdate(self);
            for (int i = 0; i < lateHandlers.Count; i++)
            {
                try { lateHandlers[i](); }
                catch (Exception e) { Report(e); }
            }
        }

        static int errors;
        public static void Report(Exception e)
        {
            if (errors++ < 40)
                Log.Line("error: " + e);
        }

        static void Frame()
        {
            FrameCount++;
            Pads.Update(Time.unscaledDeltaTime);
            for (int i = 0; i < frameHandlers.Count; i++)
            {
                try { frameHandlers[i](); }
                catch (Exception e) { Report(e); }
            }
        }
    }

    // every feature's Install, in order (Features.cs lists them)
}
