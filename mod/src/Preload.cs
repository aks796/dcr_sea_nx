// Preload.cs -- a wipe never waits on a mesh that is not coming.
//
// Every map change's wipe (and the start-up) runs QAnimationManager.
// PreloadMeshCaches: a counter goes up for each mesh it asks the asset bundles
// for and down as each one arrives, and the wipe (WipeController.MidWipeSetup)
// finishes when it reaches zero. A load that fails never calls back, so the
// counter never gets there: the game then waits out its CheckForTooLongPreload
// (5 s; at the start-up 5 s more, with a second try), and the lost count stays
// in the counter, so every later wipe waits its 5 s as well. FastBundles fixes
// the failure seen on hardware (a bundle unloaded under its loads); this is
// for any other: once the count has not moved for 2 s and 60 frames with no
// asset bundle still loading, what is left is not coming -- the preload is
// declared done and the counter emptied.
using UnityEngine;

namespace DcrMod
{
    public static class Preload
    {
        const float StallSeconds = 2f;
        const int StallFrames = 60;
        static int lastCount, lastFrame, released;
        static float lastChange;

        public static void Install() { Loader.OnFrame(Frame); }

        static void Frame()
        {
            var m = QAnimationManager.sharedInstance;
            if (m == null)
                return;
            int n = m.meshesWaitingToPreload;
            float now = Time.realtimeSinceStartup;
            if (n <= 0 || n != lastCount || FastBundles.Busy || !BootstrapAssetBundles.FinishedInitializing)
            {
                lastCount = n;
                lastChange = now;
                lastFrame = Loader.FrameCount;
                return;
            }
            if (now - lastChange < StallSeconds || Loader.FrameCount - lastFrame < StallFrames)
                return;
            m.meshesWaitingToPreload = 0;
            lastCount = 0;
            if (released++ < 8)
                Log.Line("preload: " + n + " mesh load(s) never came back (" + StallSeconds + " s, nothing still loading); going on without them");
            NotificationServer.instance.postNotification("MeshPreloadComplete");
        }
    }
}
